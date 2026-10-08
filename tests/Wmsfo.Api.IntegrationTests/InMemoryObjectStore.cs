using System.Collections.Concurrent;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A tiny IObjectStore used by the A16 orphan-collector tests, the theme
// style boot step, and the theme endpoints. It records tag calls and every PUT (bytes and headers,
// which HEAD and GET answer from), and can be configured to throw so the "S3
// call fails" case is covered without hitting a real bucket.
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
}
