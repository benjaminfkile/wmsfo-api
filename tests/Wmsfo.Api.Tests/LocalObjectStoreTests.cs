using System.Text;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// A6 acceptance criterion 748: the local store round-trips put, head, get, tag,
// untag, list, delete. api.md 20 / platform.md 1.2: LocalObjectStore is the
// dev-and-tests peer of S3ObjectStore, so every method the API's callers touch
// has to behave the same.
public sealed class LocalObjectStoreTests : IDisposable
{
    private readonly string _root;
    private readonly LocalObjectStore _store;
    private const string UploadBase = "http://localhost:5000";

    public LocalObjectStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wmsfo-local-store-" + Guid.NewGuid().ToString("N")[..12]);
        _store = new LocalObjectStore(_root, UploadBase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Put_head_get_delete_round_trip()
    {
        var bytes = Encoding.UTF8.GetBytes("hello world");
        await _store.PutObjectAsync("live/location.json", bytes, "application/json; charset=utf-8", "s-maxage=1, max-age=0");

        var head = await _store.HeadObjectAsync("live/location.json");
        Assert.NotNull(head);
        Assert.Equal(bytes.Length, head!.ContentLength);
        Assert.Equal("application/json; charset=utf-8", head.ContentType);
        Assert.Equal("s-maxage=1, max-age=0", head.CacheControl);

        var got = await _store.GetObjectAsync("live/location.json");
        Assert.NotNull(got);
        Assert.Equal(bytes, got!.Bytes);
        Assert.Equal("application/json; charset=utf-8", got.ContentType);

        await _store.DeleteObjectAsync("live/location.json");
        Assert.Null(await _store.HeadObjectAsync("live/location.json"));
        Assert.Null(await _store.GetObjectAsync("live/location.json"));
    }

    [Fact]
    public async Task Head_and_get_return_null_when_missing()
    {
        Assert.Null(await _store.HeadObjectAsync("missing/key"));
        Assert.Null(await _store.GetObjectAsync("missing/key"));
    }

    [Fact]
    public async Task Put_persists_bytes_and_headers_to_disk_layout()
    {
        await _store.PutObjectAsync("icons/abc.svg", Encoding.UTF8.GetBytes("<svg/>"),
            IconContentType, ImmutableCache);

        var expectedPath = Path.Combine(_root, "icons", "abc.svg");
        Assert.True(File.Exists(expectedPath), "object bytes should be at <root>/<key>");
        Assert.Equal("<svg/>", File.ReadAllText(expectedPath));
    }

    [Fact]
    public async Task Tag_round_trip_put_get_delete()
    {
        await _store.PutObjectAsync("media/abc/x.png", new byte[] { 1, 2, 3 }, "image/png", ImmutableCache);
        Assert.Null(await _store.GetObjectTaggingAsync("media/abc/x.png"));

        await _store.PutObjectTaggingAsync("media/abc/x.png", ObjectTags.Pending);
        Assert.Equal(ObjectTags.Pending, await _store.GetObjectTaggingAsync("media/abc/x.png"));

        await _store.PutObjectTaggingAsync("media/abc/x.png", ObjectTags.Orphaned);
        Assert.Equal(ObjectTags.Orphaned, await _store.GetObjectTaggingAsync("media/abc/x.png"));

        await _store.DeleteObjectTaggingAsync("media/abc/x.png");
        Assert.Null(await _store.GetObjectTaggingAsync("media/abc/x.png"));
    }

    [Fact]
    public async Task Tag_written_by_put_lands_and_can_be_removed_by_untag()
    {
        await _store.PutObjectAsync("media/abc/x.png", new byte[] { 1, 2, 3 }, "image/png", ImmutableCache, tag: ObjectTags.Pending);
        Assert.Equal(ObjectTags.Pending, await _store.GetObjectTaggingAsync("media/abc/x.png"));

        await _store.DeleteObjectTaggingAsync("media/abc/x.png");
        Assert.Null(await _store.GetObjectTaggingAsync("media/abc/x.png"));
    }

    [Fact]
    public async Task Tagging_missing_object_throws()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _store.PutObjectTaggingAsync("media/missing/x.png", ObjectTags.Pending));
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _store.DeleteObjectTaggingAsync("media/missing/x.png"));
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => _store.GetObjectTaggingAsync("media/missing/x.png"));
    }

    [Fact]
    public async Task List_prefix_returns_keys_under_the_prefix_only()
    {
        await _store.PutObjectAsync("media/a/one.png", new byte[] { 1 }, "image/png", ImmutableCache);
        await _store.PutObjectAsync("media/a/two.png", new byte[] { 2, 3 }, "image/png", ImmutableCache);
        await _store.PutObjectAsync("media/b/three.png", new byte[] { 4, 5, 6 }, "image/png", ImmutableCache);
        await _store.PutObjectAsync("routes/one.json", new byte[] { 7 }, "application/json", ImmutableCache);

        var underA = new List<ObjectListEntry>();
        await foreach (var entry in _store.ListPrefixAsync("media/a/"))
            underA.Add(entry);
        Assert.Equal(2, underA.Count);
        Assert.Contains(underA, e => e.Key == "media/a/one.png" && e.Size == 1);
        Assert.Contains(underA, e => e.Key == "media/a/two.png" && e.Size == 2);

        var underMedia = new List<ObjectListEntry>();
        await foreach (var entry in _store.ListPrefixAsync("media/"))
            underMedia.Add(entry);
        Assert.Equal(3, underMedia.Count);
        Assert.All(underMedia, e => Assert.StartsWith("media/", e.Key));
    }

    [Fact]
    public async Task List_prefix_skips_sidecar_files()
    {
        await _store.PutObjectAsync("media/a/x.png", new byte[] { 1, 2 }, "image/png", ImmutableCache, tag: ObjectTags.Pending);

        var entries = new List<ObjectListEntry>();
        await foreach (var entry in _store.ListPrefixAsync("media/"))
            entries.Add(entry);
        Assert.Single(entries);
        Assert.Equal("media/a/x.png", entries[0].Key);
    }

    [Fact]
    public async Task List_prefix_returns_empty_when_directory_absent()
    {
        var entries = new List<ObjectListEntry>();
        await foreach (var entry in _store.ListPrefixAsync("does-not-exist/"))
            entries.Add(entry);
        Assert.Empty(entries);
    }

    [Fact]
    public void PresignPut_points_at_local_upload_endpoint()
    {
        var url = _store.PresignPut("media/abc/x.png", "image/png", ObjectTags.Pending);
        Assert.Equal(UploadBase + "/local-upload/abc", url);
    }

    [Fact]
    public async Task WriteUploadAsync_applies_pending_tag_and_immutable_cache()
    {
        await _store.WriteUploadAsync("media/abc/x.png", new byte[] { 9 }, "image/png");
        Assert.Equal(ObjectTags.Pending, await _store.GetObjectTaggingAsync("media/abc/x.png"));
        var head = await _store.HeadObjectAsync("media/abc/x.png");
        Assert.NotNull(head);
        Assert.Equal("image/png", head!.ContentType);
        Assert.Equal("public, max-age=31536000, immutable", head.CacheControl);
    }

    [Fact]
    public void Path_traversal_is_refused()
    {
        Assert.Throws<ArgumentException>(() => _store.PresignPut("../escape.png", "image/png", ObjectTags.Pending));
    }

    private const string IconContentType = "image/svg+xml";
    private const string ImmutableCache = "public, max-age=31536000, immutable";
}
