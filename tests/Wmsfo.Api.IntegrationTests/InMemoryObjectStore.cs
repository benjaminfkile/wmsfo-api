using System.Collections.Concurrent;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A tiny IObjectStore used by the A16 orphan-collector tests, the theme
// style boot step, and the theme and map endpoints. It records tag calls and every PUT (bytes and headers,
// which HEAD and GET answer from), and can be configured to throw so the "S3
// call fails" case is covered without hitting a real bucket. Multipart
// uploads live in memory; a test writes a part through UploadPart with the
// URL PresignUploadPart answered.
public sealed class InMemoryObjectStore : IObjectStore
{
    public sealed record StoredObject(byte[] Bytes, string ContentType, string CacheControl);

    private readonly ConcurrentDictionary<string, string?> _tags = new(StringComparer.Ordinal);
    public ConcurrentDictionary<string, StoredObject> Objects { get; } = new(StringComparer.Ordinal);
    public List<string> PutKeys { get; } = new();
    public List<(string Key, string Tag)> TagCalls { get; } = new();
    public List<string> UntagCalls { get; } = new();

    public HashSet<string> TagsToFail { get; } = new(StringComparer.Ordinal);
    public HashSet<string> UntagsToFail { get; } = new(StringComparer.Ordinal);

    public Task PutObjectAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl, string? tag = null, CancellationToken cancellationToken = default)
    {
        _tags[key] = tag;
        Objects[key] = new StoredObject(bytes.ToArray(), contentType, cacheControl);
        lock (PutKeys) PutKeys.Add(key);
        return Task.CompletedTask;
    }

    public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        _tags.TryRemove(key, out _);
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var kv in _tags.Keys)
        {
            if (kv.StartsWith(prefix, StringComparison.Ordinal))
                yield return new ObjectListEntry(kv, 0);
        }
    }

    public Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default)
    {
        TagCalls.Add((key, tag));
        if (TagsToFail.Contains(key)) throw new IOException("simulated tag failure for " + key);
        _tags[key] = tag;
        return Task.CompletedTask;
    }

    public Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default)
    {
        UntagCalls.Add(key);
        if (UntagsToFail.Contains(key)) throw new IOException("simulated untag failure for " + key);
        _tags[key] = null;
        return Task.CompletedTask;
    }

    public Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default)
    {
        _tags.TryGetValue(key, out var t);
        return Task.FromResult(t);
    }

    public Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Objects.TryGetValue(key, out var o)
            ? new ObjectHead(o.Bytes.Length, o.ContentType, o.CacheControl)
            : null);

    public Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(Objects.TryGetValue(key, out var o)
            ? new ObjectContent(o.Bytes, o.ContentType)
            : null);

    // The URL names the key so a test can read which object a ticket signs.
    public string PresignPut(string key, string contentType, string tag) => "https://example/local-upload/" + key;

    public void SeedObject(string key, string? initialTag = null) => _tags[key] = initialTag;

    public sealed class OpenUpload(string key, string contentType, string cacheControl)
    {
        public string Key { get; } = key;
        public string ContentType { get; } = contentType;
        public string CacheControl { get; } = cacheControl;
        public ConcurrentDictionary<int, byte[]> Parts { get; } = new();
    }

    public ConcurrentDictionary<string, OpenUpload> Uploads { get; } = new(StringComparer.Ordinal);

    public Task<string> StartMultipartAsync(string key, string contentType, string cacheControl, CancellationToken cancellationToken = default)
    {
        var uploadId = Guid.NewGuid().ToString("N");
        Uploads[uploadId] = new OpenUpload(key, contentType, cacheControl);
        return Task.FromResult(uploadId);
    }

    public string PresignUploadPart(string key, string uploadId, int partNumber, TimeSpan expires) =>
        $"https://example/local-upload/parts/{uploadId}/{partNumber}";

    // The PUT a part URL receives: stores the bytes and answers the etag.
    public string UploadPart(string url, byte[] bytes)
    {
        var segments = new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var uploadId = segments[^2];
        var partNumber = int.Parse(segments[^1], System.Globalization.CultureInfo.InvariantCulture);
        Uploads[uploadId].Parts[partNumber] = bytes;
        return ETag(bytes);
    }

    public Task CompleteMultipartAsync(string key, string uploadId, IReadOnlyList<MultipartPart> parts, CancellationToken cancellationToken = default)
    {
        if (!Uploads.TryGetValue(uploadId, out var upload) || upload.Key != key)
            throw new IOException("no such upload");
        using var joined = new MemoryStream();
        foreach (var part in parts)
        {
            if (!upload.Parts.TryGetValue(part.PartNumber, out var bytes) || ETag(bytes) != part.ETag)
                throw new IOException($"part {part.PartNumber} is missing or has another etag");
            joined.Write(bytes);
        }
        Uploads.TryRemove(uploadId, out _);
        return PutObjectAsync(key, joined.ToArray(), upload.ContentType, upload.CacheControl, null, cancellationToken);
    }

    public Task AbortMultipartAsync(string key, string uploadId, CancellationToken cancellationToken = default)
    {
        Uploads.TryRemove(uploadId, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<MultipartUploadEntry>> ListMultipartUploadsAsync(string prefix, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MultipartUploadEntry> open = Uploads
            .Where(u => u.Value.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(u => new MultipartUploadEntry(u.Value.Key, u.Key))
            .ToList();
        return Task.FromResult(open);
    }

    public Task<byte[]?> GetObjectRangeAsync(string key, long from, long to, CancellationToken cancellationToken = default)
    {
        if (!Objects.TryGetValue(key, out var o)) return Task.FromResult<byte[]?>(null);
        var start = (int)Math.Min(from, o.Bytes.Length);
        var end = (int)Math.Min(to + 1, o.Bytes.Length);
        return Task.FromResult<byte[]?>(o.Bytes[start..end]);
    }

    private static string ETag(byte[] bytes) =>
        "\"" + Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(bytes)) + "\"";
}
