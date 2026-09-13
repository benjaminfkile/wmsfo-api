using System.Globalization;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Media;

// api.md 11.3 step 5, contracts 1.3b: build the Deep Zoom pyramid for a raster
// whose longest side is 2048 px or more. Tile size 254, overlap 1, JPEG quality
// 82. Level n = ceil(log2(max(width, height))) is the full image; each lower
// level halves both dimensions (rounding up) down to level 0 at 1 by 1. Tiles
// land at media/{id}/dzi/poster_files/{level}/{col}_{row}.jpg with the
// immutable header and the pending tag, PUT eight at a time with a 3 s timeout
// each. The descriptor lands at media/{id}/dzi/poster.dzi with Content-Type
// application/xml, the immutable header, and the pending tag. Any PUT failure
// throws so the confirm handler answers 502 media_write_failed and the row
// stays pending.
public static class DeepZoomTiler
{
    public const int TileSize = 254;
    public const int Overlap = 1;
    public const int JpegQuality = 82;
    public const int Threshold = 2048;
    public const int MaxConcurrentPuts = 8;
    public static readonly TimeSpan PutTimeout = TimeSpan.FromSeconds(3);
    public const string TileContentType = "image/jpeg";
    public const string DescriptorContentType = "application/xml";
    public const string DeepZoomNamespace = "http://schemas.microsoft.com/deepzoom/2008";

    public static bool ShouldTile(int width, int height) =>
        Math.Max(width, height) >= Threshold;

    public static int MaxLevel(int width, int height)
    {
        var longest = Math.Max(width, height);
        if (longest <= 1) return 0;
        return (int)Math.Ceiling(Math.Log2(longest));
    }

    public static (int Width, int Height) LevelDimensions(int width, int height, int level)
    {
        var top = MaxLevel(width, height);
        if (level > top) throw new ArgumentOutOfRangeException(nameof(level));
        var w = width;
        var h = height;
        for (var l = top; l > level; l--)
        {
            w = Math.Max(1, (w + 1) / 2);
            h = Math.Max(1, (h + 1) / 2);
        }
        return (w, h);
    }

    public static (int Cols, int Rows) TileGrid(int levelWidth, int levelHeight) =>
        ((levelWidth + TileSize - 1) / TileSize, (levelHeight + TileSize - 1) / TileSize);

    // The descriptor is the source dimensions inside a fixed XML skeleton.
    public static byte[] BuildDescriptor(int width, int height)
    {
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            $"<Image TileSize=\"{TileSize.ToString(CultureInfo.InvariantCulture)}\"" +
            $" Overlap=\"{Overlap.ToString(CultureInfo.InvariantCulture)}\"" +
            " Format=\"jpg\"" +
            $" xmlns=\"{DeepZoomNamespace}\">" +
            $"<Size Width=\"{width.ToString(CultureInfo.InvariantCulture)}\"" +
            $" Height=\"{height.ToString(CultureInfo.InvariantCulture)}\"/>" +
            "</Image>";
        return Encoding.UTF8.GetBytes(xml);
    }

    public static string TileKey(Guid mediaId, int level, int col, int row) =>
        $"media/{mediaId}/dzi/poster_files/{level.ToString(CultureInfo.InvariantCulture)}/" +
        $"{col.ToString(CultureInfo.InvariantCulture)}_{row.ToString(CultureInfo.InvariantCulture)}.jpg";

    public static string DescriptorKey(Guid mediaId) => $"media/{mediaId}/dzi/poster.dzi";

    // Runs the full pyramid: PUT every tile (8 at a time, 3 s each), then PUT
    // the descriptor. Returns the descriptor key and every tile key so the
    // caller can untag them as part of confirm step 6. On any failure the
    // caller throws 502 media_write_failed and the row stays pending.
    public static async Task<DeepZoomResult> RunAsync(
        Image image,
        Guid mediaId,
        IObjectStore store,
        string immutableCacheControl,
        string pendingTag,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrEmpty(immutableCacheControl);
        ArgumentException.ThrowIfNullOrEmpty(pendingTag);

        var width = image.Width;
        var height = image.Height;
        var top = MaxLevel(width, height);

        var tileKeys = new List<string>();
        var jpegEncoder = new JpegEncoder { Quality = JpegQuality };

        // Level `top` is the source; each lower level halves both dimensions
        // (rounding up). Downsampling from the previous level keeps the source
        // decoded once and matches how OpenSeadragon renders the pyramid.
        using var levelImage = image.CloneAs<Rgba32>();

        for (var level = top; level >= 0; level--)
        {
            var lw = levelImage.Width;
            var lh = levelImage.Height;

            var (cols, rows) = TileGrid(lw, lh);

            var tasks = new List<(string Key, byte[] Bytes)>(cols * rows);
            for (var row = 0; row < rows; row++)
            {
                for (var col = 0; col < cols; col++)
                {
                    var (x0, y0, tw, th) = TileBounds(col, row, lw, lh);
                    using var tile = levelImage.Clone(ctx => ctx.Crop(new Rectangle(x0, y0, tw, th)));
                    using var ms = new MemoryStream();
                    tile.Save(ms, jpegEncoder);
                    tasks.Add((TileKey(mediaId, level, col, row), ms.ToArray()));
                }
            }

            await PutManyAsync(tasks, store, immutableCacheControl, pendingTag, ct).ConfigureAwait(false);
            foreach (var (k, _) in tasks) tileKeys.Add(k);

            if (level > 0)
            {
                var (nw, nh) = (Math.Max(1, (lw + 1) / 2), Math.Max(1, (lh + 1) / 2));
                levelImage.Mutate(ctx => ctx.Resize(nw, nh));
            }
        }

        var descriptorKey = DescriptorKey(mediaId);
        var descriptorBytes = BuildDescriptor(width, height);
        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(PutTimeout);
            await store.PutObjectAsync(
                descriptorKey, descriptorBytes, DescriptorContentType,
                immutableCacheControl, pendingTag, cts.Token).ConfigureAwait(false);
        }

        return new DeepZoomResult(descriptorKey, tileKeys);
    }

    // Deep Zoom tile bounds: the interior cell is `TileSize x TileSize`; the
    // tile extends by `Overlap` pixels on inner edges (never past the level).
    public static (int X, int Y, int Width, int Height) TileBounds(
        int col, int row, int levelWidth, int levelHeight)
    {
        var xStart = col * TileSize;
        var yStart = row * TileSize;
        var xEnd = Math.Min(xStart + TileSize, levelWidth);
        var yEnd = Math.Min(yStart + TileSize, levelHeight);
        if (col > 0) xStart -= Overlap;
        if (row > 0) yStart -= Overlap;
        if (xEnd < levelWidth) xEnd += Overlap;
        if (yEnd < levelHeight) yEnd += Overlap;
        return (xStart, yStart, xEnd - xStart, yEnd - yStart);
    }

    private static async Task PutManyAsync(
        IReadOnlyList<(string Key, byte[] Bytes)> items,
        IObjectStore store,
        string cacheControl,
        string tag,
        CancellationToken ct)
    {
        if (items.Count == 0) return;
        using var gate = new SemaphoreSlim(MaxConcurrentPuts, MaxConcurrentPuts);
        var tasks = new List<Task>(items.Count);
        foreach (var (key, bytes) in items)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(PutTimeout);
                    await store.PutObjectAsync(
                        key, bytes, TileContentType, cacheControl, tag, cts.Token)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

public sealed record DeepZoomResult(string DescriptorKey, IReadOnlyList<string> TileKeys);
