using System.Runtime.CompilerServices;

namespace Wmsfo.Api.Objects;

// api.md 20: the directory object store used in local development and tests.
// Bytes land at <root>/<key>; the two per-object headers documented in
// platform.md 1.2 land in <root>/<key>.wmsfo.headers as a two-line file
// (Content-Type first, Cache-Control second); the object tag lands next to
// them in <root>/<key>.wmsfo.tag as the raw `state=...` string. PresignPut
// cannot sign anything, so it returns the WMSFO_PUBLIC_API_BASE_URL +
// /local-upload/{id} URL the admin panel PUTs to (that endpoint writes the
// bytes and the pending tag through this store).
public sealed class LocalObjectStore : IObjectStore
{
    private const string HeadersSuffix = ".wmsfo.headers";
    private const string TagSuffix = ".wmsfo.tag";

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
        // media/ key (platform.md 1.2).
        var uploadId = ExtractUploadId(key);
        return $"{_uploadBaseUrl}/local-upload/{uploadId}";
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
}
