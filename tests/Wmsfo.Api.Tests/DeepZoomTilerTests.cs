using System.Collections.Concurrent;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Media;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// api.md 11.3 step 5, contracts 1.3b: the tile pyramid.
public sealed class DeepZoomTilerTests
{
    // A 3000 by 2000 source: level n = ceil(log2(3000)) = 12; each lower level
    // halves both dims (rounding up); level 0 = 1 by 1.
    [Fact]
    public async Task Source_3000_by_2000_writes_levels_0_to_12_with_matching_tile_counts()
    {
        var mediaId = Guid.NewGuid();
        var store = new InMemoryStore();
        using var img = new Image<Rgba32>(3000, 2000, new Rgba32(200, 200, 200, 255));
        var result = await DeepZoomTiler.RunAsync(
            img, mediaId, store, "public, max-age=31536000, immutable", "state=pending", CancellationToken.None);

        Assert.Equal(DeepZoomTiler.DescriptorKey(mediaId), result.DescriptorKey);

        // Expected per-level tile counts (levelDims: level 12 = 3000x2000):
        // 12: cols=12, rows=8 -> 96
        // 11: 1500x1000 -> 6x4 = 24
        // 10:  750x500  -> 3x2 = 6
        //  9:  375x250  -> 2x1 = 2
        //  8..0: single tile each = 9
        var expectedCounts = new Dictionary<int, int>
        {
            [12] = 96, [11] = 24, [10] = 6, [9] = 2,
            [8]  = 1,  [7] = 1,  [6] = 1, [5] = 1, [4] = 1, [3] = 1, [2] = 1, [1] = 1, [0] = 1,
        };
        var expectedTotal = expectedCounts.Values.Sum();
        Assert.Equal(expectedTotal, result.TileKeys.Count);

        foreach (var (level, expected) in expectedCounts)
        {
            var prefix = $"media/{mediaId}/dzi/poster_files/{level}/";
            var count = store.Keys.Count(k => k.StartsWith(prefix, StringComparison.Ordinal));
            Assert.Equal(expected, count);
        }

        // Descriptor Size matches source dimensions.
        var descriptorBytes = store.Bodies[result.DescriptorKey];
        Assert.Equal("application/xml", store.ContentTypes[result.DescriptorKey]);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(descriptorBytes));
        var image = xml.Root!;
        Assert.Equal("Image", image.Name.LocalName);
        Assert.Equal("http://schemas.microsoft.com/deepzoom/2008", image.Name.NamespaceName);
        Assert.Equal("254", image.Attribute("TileSize")!.Value);
        Assert.Equal("1", image.Attribute("Overlap")!.Value);
        Assert.Equal("jpg", image.Attribute("Format")!.Value);
        var size = image.Elements().Single();
        Assert.Equal("Size", size.Name.LocalName);
        Assert.Equal("3000", size.Attribute("Width")!.Value);
        Assert.Equal("2000", size.Attribute("Height")!.Value);
    }

    // Any tile is a JPEG (magic bytes FF D8 FF).
    [Fact]
    public async Task Tile_bytes_are_jpeg()
    {
        var mediaId = Guid.NewGuid();
        var store = new InMemoryStore();
        using var img = new Image<Rgba32>(2100, 1000, new Rgba32(100, 200, 100, 255));
        var result = await DeepZoomTiler.RunAsync(
            img, mediaId, store, "public, max-age=31536000, immutable", "state=pending", CancellationToken.None);
        var tileKey = result.TileKeys.First();
        var bytes = store.Bodies[tileKey];
        Assert.Equal("image/jpeg", store.ContentTypes[tileKey]);
        Assert.True(bytes.Length >= 3);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[2]);
        // Every object carries the immutable header and the pending tag.
        Assert.Equal("public, max-age=31536000, immutable", store.CacheControls[tileKey]);
        Assert.Equal("state=pending", store.Tags[tileKey]);
        Assert.Equal("state=pending", store.Tags[result.DescriptorKey]);
    }

    // Sub-2048 sources: no pyramid needed.
    [Theory]
    [InlineData(1024, 1024)]
    [InlineData(2047, 500)]
    [InlineData(500, 2047)]
    public void Should_tile_is_false_below_the_2048_threshold(int w, int h)
    {
        Assert.False(DeepZoomTiler.ShouldTile(w, h));
    }

    [Theory]
    [InlineData(2048, 100)]
    [InlineData(100, 2048)]
    [InlineData(3000, 2000)]
    public void Should_tile_is_true_at_or_above_the_2048_threshold(int w, int h)
    {
        Assert.True(DeepZoomTiler.ShouldTile(w, h));
    }

    // Level dimensions: the top level is the source; each step halves rounding up.
    [Fact]
    public void Level_dimensions_halve_rounding_up_down_to_1x1()
    {
        Assert.Equal((3000, 2000), DeepZoomTiler.LevelDimensions(3000, 2000, 12));
        Assert.Equal((1500, 1000), DeepZoomTiler.LevelDimensions(3000, 2000, 11));
        Assert.Equal((750, 500),   DeepZoomTiler.LevelDimensions(3000, 2000, 10));
        Assert.Equal((375, 250),   DeepZoomTiler.LevelDimensions(3000, 2000, 9));
        Assert.Equal((188, 125),   DeepZoomTiler.LevelDimensions(3000, 2000, 8));
        Assert.Equal((1, 1),       DeepZoomTiler.LevelDimensions(3000, 2000, 0));
    }

    // In-memory store the tiler PUTs into. Records every write for assertions.
    private sealed class InMemoryStore : IObjectStore
    {
        public ConcurrentDictionary<string, byte[]> Bodies { get; } = new();
        public ConcurrentDictionary<string, string> ContentTypes { get; } = new();
        public ConcurrentDictionary<string, string> CacheControls { get; } = new();
        public ConcurrentDictionary<string, string> Tags { get; } = new();
        public IReadOnlyCollection<string> Keys => Bodies.Keys.ToList();

        public Task PutObjectAsync(
            string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl,
            string? tag = null, CancellationToken cancellationToken = default)
        {
            Bodies[key] = bytes.ToArray();
            ContentTypes[key] = contentType;
            CacheControls[key] = cacheControl;
            if (tag is not null) Tags[key] = tag;
            return Task.CompletedTask;
        }

        public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
        {
            Bodies.TryRemove(key, out _);
            ContentTypes.TryRemove(key, out _);
            CacheControls.TryRemove(key, out _);
            Tags.TryRemove(key, out _);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(
            string prefix,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var k in Bodies.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
                yield return new ObjectListEntry(k, Bodies[k].Length);
            await Task.CompletedTask;
        }

        public Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default)
        {
            Tags[key] = tag;
            return Task.CompletedTask;
        }
        public Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default)
        {
            Tags.TryRemove(key, out _);
            return Task.CompletedTask;
        }
        public Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(Tags.TryGetValue(key, out var t) ? t : null);
        public Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<ObjectHead?>(Bodies.TryGetValue(key, out var b)
                ? new ObjectHead(b.Length, ContentTypes[key], CacheControls[key])
                : null);
        public Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default) =>
            Task.FromResult<ObjectContent?>(Bodies.TryGetValue(key, out var b)
                ? new ObjectContent(b, ContentTypes[key])
                : null);
        public string PresignPut(string key, string contentType, string tag) => "";
    }
}
