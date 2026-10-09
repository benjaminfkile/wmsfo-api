using System.Runtime.CompilerServices;

namespace Wmsfo.Api.Objects;

// api.md 20: the directory object store used in local development and tests.
// Bytes land at <root>/<key>; the two per-object headers documented in
// platform.md 1.2 land in <root>/<key>.wmsfo.headers as a two-line file
// (Content-Type first, Cache-Control second); the object tag lands next to
// them in <root>/<key>.wmsfo.tag as the raw `state=...` string. PresignPut
// cannot sign anything, so it returns the WMSFO_PUBLIC_API_BASE_URL +
// /local-upload/{id} URL the admin panel PUTs to (that endpoint writes the
// bytes and the pending tag through this store). A multipart upload keeps its
// parts as files under <root>/.multipart/<uploadId>/<partNumber> beside an
// `upload` file naming the key, the content type, and the cache header; part
// URLs point at PUT /local-upload/parts/{uploadId}/{partNumber} on the API,
// which writes the part through WritePartAsync.
public sealed class LocalObjectStore : IObjectStore
{
    private const string HeadersSuffix = ".wmsfo.headers";
    private const string TagSuffix = ".wmsfo.tag";
    private const string MultipartDir = ".multipart";
    private const string UploadFile = "upload";

    private readonly string _rootDir;
    private readonly string _uploadBaseUrl;

    public LocalObjectStore(string rootDir, string uploadBaseUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDir);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadBaseUrl);
        _rootDir = Path.GetFullPath(rootDir);
        _uploadBaseUrl = uploadBaseUrl.TrimEnd('/');
        Directory.CreateDirectory(_rootDir);
    }

    public string RootDir => _rootDir;

    public async Task PutObjectAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        string cacheControl,
        string? tag = null,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheControl);

        var absolute = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);

        await using (var stream = new FileStream(absolute, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }

        await File.WriteAllTextAsync(HeadersPath(absolute), contentType + "\n" + cacheControl + "\n", cancellationToken).ConfigureAwait(false);
        await WriteTagAsync(absolute, tag, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var absolute = ResolvePath(key);
        if (File.Exists(absolute)) File.Delete(absolute);
        DeleteIfExists(HeadersPath(absolute));
        DeleteIfExists(TagPath(absolute));
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        // Match S3 semantics: empty prefix lists everything; the prefix is a raw
        // key prefix, so `media/abc` matches `media/abc/x` and `media/abcdef`.
        if (!Directory.Exists(_rootDir)) yield break;

        var prefixPath = ResolvePath(prefix, allowEmpty: true);
        var enumerateFrom = _rootDir;
        var enumerationPattern = "*";
        if (!string.IsNullOrEmpty(prefix))
        {
            // Walk from the closest existing ancestor of prefixPath so
            // enumeration does not fail on a missing directory. S3 returns an
            // empty list, and this mirrors that behavior.
            var parent = Path.GetDirectoryName(prefixPath);
            if (parent is null || parent.Length < _rootDir.Length) parent = _rootDir;
            if (!Directory.Exists(parent)) yield break;
            enumerateFrom = parent;
        }

        foreach (var path in Directory.EnumerateFiles(enumerateFrom, enumerationPattern, SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsSidecar(path)) continue;
            var relative = KeyOf(path);
            if (relative.StartsWith(MultipartDir + "/", StringComparison.Ordinal)) continue;
            if (!relative.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var length = new FileInfo(path).Length;
            yield return new ObjectListEntry(relative, length);
        }

        await Task.CompletedTask;
    }

    public Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        var absolute = ResolvePath(key);
        if (!File.Exists(absolute))
            throw new FileNotFoundException($"object not found: {key}");
        return WriteTagAsync(absolute, tag, cancellationToken);
    }

    public Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var absolute = ResolvePath(key);
        if (!File.Exists(absolute))
            throw new FileNotFoundException($"object not found: {key}");
        DeleteIfExists(TagPath(absolute));
        return Task.CompletedTask;
    }

    public async Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var absolute = ResolvePath(key);
        if (!File.Exists(absolute))
            throw new FileNotFoundException($"object not found: {key}");
        var tagPath = TagPath(absolute);
        if (!File.Exists(tagPath)) return null;
        var raw = await File.ReadAllTextAsync(tagPath, cancellationToken).ConfigureAwait(false);
        raw = raw.Trim();
        return raw.Length == 0 ? null : raw;
    }

    public async Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var absolute = ResolvePath(key);
        if (!File.Exists(absolute)) return null;
        var length = new FileInfo(absolute).Length;
        var (contentType, cacheControl) = await ReadHeadersAsync(absolute, cancellationToken).ConfigureAwait(false);
        return new ObjectHead(length, contentType, cacheControl);
    }

    public async Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        var absolute = ResolvePath(key);
        if (!File.Exists(absolute)) return null;
        var bytes = await File.ReadAllBytesAsync(absolute, cancellationToken).ConfigureAwait(false);
        var (contentType, _) = await ReadHeadersAsync(absolute, cancellationToken).ConfigureAwait(false);
        return new ObjectContent(bytes, contentType);
    }

    public string PresignPut(string key, string contentType, string tag)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(tag);
        // The API's PUT /local-upload/{id} route is the sole callsite; the id
        // segment is the media asset id, which is the second component of every
        // media/ key (platform.md 1.2). The filename lives after `media/{id}/`;
        // the local-upload endpoint reads it from the query string so the bytes
        // land at the ticket's key, matching the S3 presigned PUT.
        var uploadId = ExtractUploadId(key);
        var filename = ExtractFilename(key);
        var url = $"{_uploadBaseUrl}/local-upload/{uploadId}";
        return filename.Length == 0 ? url : $"{url}?filename={Uri.EscapeDataString(filename)}";
    }

    // Called by the /local-upload/{id} endpoint, which turns a browser PUT into
    // a store write. Applies the pending tag exactly as a presigned S3 PUT
    // would (platform.md 1.2), so the bucket lifecycle behaviour lines up with
    // the S3 store's.
    public Task WriteUploadAsync(
        string key,
        ReadOnlyMemory<byte> bytes,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        return PutObjectAsync(
            key,
            bytes,
            contentType,
            "public, max-age=31536000, immutable",
            ObjectTags.Pending,
            cancellationToken);
    }

    public async Task<string> StartMultipartAsync(
        string key,
        string contentType,
        string cacheControl,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheControl);
        var uploadId = Guid.NewGuid().ToString("N");
        var dir = UploadDir(uploadId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, UploadFile),
            key + "\n" + contentType + "\n" + cacheControl + "\n", cancellationToken).ConfigureAwait(false);
        return uploadId;
    }

    public string PresignUploadPart(string key, string uploadId, int partNumber, TimeSpan expires)
    {
        ValidateKey(key);
        ValidateUploadId(uploadId);
        return $"{_uploadBaseUrl}/local-upload/parts/{uploadId}/{partNumber}";
    }

    // Called by the /local-upload/parts/{uploadId}/{partNumber} endpoint:
    // writes one part of an open upload and answers its etag (the quoted MD5
    // hex of the bytes, as S3 answers), or null when no such upload is open or the part number is outside 1 to
    // 10,000.
    public async Task<string?> WritePartAsync(
        string uploadId,
        int partNumber,
        Stream body,
        CancellationToken cancellationToken = default)
    {
        if (!IsUploadId(uploadId) || partNumber is < 1 or > 10000) return null;
        var dir = UploadDir(uploadId);
        if (!File.Exists(Path.Combine(dir, UploadFile))) return null;
        var path = PartPath(dir, partNumber);
        await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            await body.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        return await PartETagAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task CompleteMultipartAsync(
        string key,
        string uploadId,
        IReadOnlyList<MultipartPart> parts,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ValidateUploadId(uploadId);
        ArgumentNullException.ThrowIfNull(parts);
        var dir = UploadDir(uploadId);
        var upload = await ReadUploadAsync(dir, cancellationToken).ConfigureAwait(false);
        if (upload is null || upload.Value.Key != key)
            throw new InvalidOperationException($"no open upload {uploadId} for {key}");
        if (parts.Count == 0) throw new InvalidOperationException("an upload completes with at least one part");
        var previous = 0;
        foreach (var part in parts)
        {
            if (part.PartNumber <= previous) throw new InvalidOperationException("parts must be in ascending order");
            previous = part.PartNumber;
            var path = PartPath(dir, part.PartNumber);
            if (!File.Exists(path)) throw new InvalidOperationException($"part {part.PartNumber} was not uploaded");
            var etag = await PartETagAsync(path, cancellationToken).ConfigureAwait(false);
            if (etag.Trim('"') != (part.ETag ?? "").Trim('"'))
                throw new InvalidOperationException($"part {part.PartNumber} has another etag");
        }

        var absolute = ResolvePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        await using (var target = new FileStream(absolute, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            foreach (var part in parts)
            {
                await using var source = new FileStream(PartPath(dir, part.PartNumber), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }
        await File.WriteAllTextAsync(HeadersPath(absolute),
            upload.Value.ContentType + "\n" + upload.Value.CacheControl + "\n", cancellationToken).ConfigureAwait(false);
        await WriteTagAsync(absolute, null, cancellationToken).ConfigureAwait(false);
        Directory.Delete(dir, recursive: true);
    }

    public Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ValidateUploadId(uploadId);
        var dir = UploadDir(uploadId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<MultipartUploadEntry>> ListMultipartUploadsAsync(
        string prefix,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        var uploads = new List<MultipartUploadEntry>();
        var root = Path.Combine(_rootDir, MultipartDir);
        if (!Directory.Exists(root)) return uploads;
        foreach (var dir in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            var upload = await ReadUploadAsync(dir, cancellationToken).ConfigureAwait(false);
            if (upload is null || !upload.Value.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
            uploads.Add(new MultipartUploadEntry(upload.Value.Key, Path.GetFileName(dir)));
        }
        return uploads;
    }

    public async Task<byte[]?> GetObjectRangeAsync(
        string key,
        long from,
        long to,
        CancellationToken cancellationToken = default)
    {
        ValidateKey(key);
        ArgumentOutOfRangeException.ThrowIfNegative(from);
        ArgumentOutOfRangeException.ThrowIfLessThan(to, from);
        var absolute = ResolvePath(key);
        if (!File.Exists(absolute)) return null;
        await using var stream = new FileStream(absolute, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        if (from >= stream.Length) return [];
        var count = (int)Math.Min(to - from + 1, stream.Length - from);
        var buffer = new byte[count];
        stream.Seek(from, SeekOrigin.Begin);
        await stream.ReadExactlyAsync(buffer, cancellationToken).ConfigureAwait(false);
        return buffer;
    }

    private string UploadDir(string uploadId) => Path.Combine(_rootDir, MultipartDir, uploadId);

    private static string PartPath(string uploadDir, int partNumber) =>
        Path.Combine(uploadDir, partNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));

    private static async Task<string> PartETagAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var hash = await System.Security.Cryptography.MD5.HashDataAsync(stream, ct).ConfigureAwait(false);
        return "\"" + Convert.ToHexStringLower(hash) + "\"";
    }

    private static async Task<(string Key, string ContentType, string CacheControl)?> ReadUploadAsync(
        string uploadDir, CancellationToken ct)
    {
        var path = Path.Combine(uploadDir, UploadFile);
        if (!File.Exists(path)) return null;
        var lines = await File.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
        if (lines.Length < 3) return null;
        return (lines[0], lines[1], lines[2]);
    }

    // An upload id is the 32 hex characters StartMultipartAsync mints, so it
    // can never name a path outside the multipart folder.
    private static bool IsUploadId(string uploadId) =>
        uploadId is { Length: 32 } && uploadId.All(char.IsAsciiHexDigitLower);

    private static void ValidateUploadId(string uploadId)
    {
        if (!IsUploadId(uploadId))
            throw new ArgumentException($"invalid upload id: '{uploadId}'", nameof(uploadId));
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (key.StartsWith('/') || key.Contains(".."))
            throw new ArgumentException($"invalid key: '{key}'", nameof(key));
    }

    private string ResolvePath(string key, bool allowEmpty = false)
    {
        if (string.IsNullOrEmpty(key)) return allowEmpty ? _rootDir : throw new ArgumentException("empty key", nameof(key));
        var normalized = key.Replace('/', Path.DirectorySeparatorChar);
        var absolute = Path.GetFullPath(Path.Combine(_rootDir, normalized));
        if (!absolute.StartsWith(_rootDir, StringComparison.Ordinal))
            throw new ArgumentException($"key escapes store root: '{key}'", nameof(key));
        return absolute;
    }

    private string KeyOf(string absolutePath)
    {
        var relative = Path.GetRelativePath(_rootDir, absolutePath);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string HeadersPath(string absolute) => absolute + HeadersSuffix;
    private static string TagPath(string absolute) => absolute + TagSuffix;

    private static bool IsSidecar(string absolute)
    {
        return absolute.EndsWith(HeadersSuffix, StringComparison.Ordinal)
            || absolute.EndsWith(TagSuffix, StringComparison.Ordinal);
    }

    private static async Task WriteTagAsync(string absolute, string? tag, CancellationToken ct)
    {
        var tagPath = TagPath(absolute);
        if (string.IsNullOrEmpty(tag))
        {
            DeleteIfExists(tagPath);
            return;
        }
        await File.WriteAllTextAsync(tagPath, tag, ct).ConfigureAwait(false);
    }

    private static async Task<(string ContentType, string CacheControl)> ReadHeadersAsync(string absolute, CancellationToken ct)
    {
        var headersPath = HeadersPath(absolute);
        if (!File.Exists(headersPath))
        {
            // Fall back to something sensible if the sidecar went missing; the
            // caller has the bytes already, so this only matters for HeadObject.
            return ("application/octet-stream", "");
        }
        var lines = await File.ReadAllLinesAsync(headersPath, ct).ConfigureAwait(false);
        var contentType = lines.Length > 0 ? lines[0] : "application/octet-stream";
        var cacheControl = lines.Length > 1 ? lines[1] : "";
        return (contentType, cacheControl);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }

    private static string ExtractUploadId(string key)
    {
        // The upload key shape is media/{id}/{filename} (platform.md 1.2). Fall
        // back to the raw key if the shape ever changes; the local endpoint
        // then simply keys by the whole string.
        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? parts[1] : key;
    }

    private static string ExtractFilename(string key)
    {
        var parts = key.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3 ? parts[^1] : "";
    }
}
