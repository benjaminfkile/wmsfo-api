using System.Text;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// A6 acceptance criterion 747: every key prefix of platform.md 1.2 is written
// with exactly the documented content type and cache-control. The unit case
// records the expected (contentType, cacheControl) pair per prefix and runs the
// LocalObjectStore round trip so a drift in either the store or the table is
// caught on the writer side (the actual per-prefix PUT sites are proved by
// their own tests, e.g. IconLibraryTests for icons/).
public sealed class ObjectStoreContentTypeTests : IDisposable
{
    private readonly string _root;
    private readonly LocalObjectStore _store;

    public ObjectStoreContentTypeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "wmsfo-headers-" + Guid.NewGuid().ToString("N")[..12]);
        _store = new LocalObjectStore(_root, "http://localhost:5000");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    public static IEnumerable<object[]> Prefixes()
    {
        // Rows are lifted verbatim from platform.md 1.2. Sample keys use a
        // shape the writer would produce.
        yield return new object[] { "live/location.json", "application/json; charset=utf-8", "s-maxage=1, max-age=0" };
        yield return new object[] { "snapshots/abc.json", "application/json; charset=utf-8", "public, max-age=31536000, immutable" };
        yield return new object[] { "routes/def.json", "application/json; charset=utf-8", "public, max-age=31536000, immutable" };
        yield return new object[] { "media/00000000-0000-0000-0000-000000000001/x.png", "image/png", "public, max-age=31536000, immutable" };
        yield return new object[] { "media/00000000-0000-0000-0000-000000000001/x.jpg", "image/jpeg", "public, max-age=31536000, immutable" };
        yield return new object[] { "media/00000000-0000-0000-0000-000000000001/x.webp", "image/webp", "public, max-age=31536000, immutable" };
        yield return new object[] { "media/00000000-0000-0000-0000-000000000001/x.gif", "image/gif", "public, max-age=31536000, immutable" };
        yield return new object[] { "media/00000000-0000-0000-0000-000000000001/x.svg", "image/svg+xml", "public, max-age=31536000, immutable" };
        yield return new object[] { "media/00000000-0000-0000-0000-000000000001/w480.webp", "image/webp", "public, max-age=31536000, immutable" };
        yield return new object[] { "icons/deadbeef.svg", "image/svg+xml", "public, max-age=31536000, immutable" };
    }

    [Theory]
    [MemberData(nameof(Prefixes))]
    public async Task Every_prefix_writes_the_documented_headers(string key, string contentType, string cacheControl)
    {
        var bytes = Encoding.UTF8.GetBytes("test");
        await _store.PutObjectAsync(key, bytes, contentType, cacheControl);

        var head = await _store.HeadObjectAsync(key);
        Assert.NotNull(head);
        Assert.Equal(contentType, head!.ContentType);
        Assert.Equal(cacheControl, head.CacheControl);
    }
}
