using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Wmsfo.Api.Media;

// api.md 11.3 step 4: decode the raster bytes with a 40-megapixel ceiling; for
// each width in (480, 960, 1600) that is less than the source width, resize
// (aspect kept) and encode WebP quality 82. Returns the source dimensions and
// the encoded variants keyed by width. GIF: dimensions only, no variants. The
// caller has already sniffed the type.
public static class VariantDeriver
{
    public const int MaxMegapixels = 40;
    public const long MaxPixelCount = MaxMegapixels * 1_000_000L;
    public const int WebpQuality = 82;
    public static readonly int[] TargetWidths = { 480, 960, 1600 };

    // Decode a raster (png, jpeg, webp) and derive the WebP variants narrower
    // than the source width.
    public static RasterDecodeResult DecodeRaster(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        using var image = TryLoad(bytes)
            ?? throw new MediaDecodeException("decode_failed");

        var pixels = (long)image.Width * image.Height;
        if (pixels > MaxPixelCount)
            throw new MediaDecodeException("over_megapixel_ceiling");

        var width = image.Width;
        var height = image.Height;
        var variants = new SortedDictionary<int, byte[]>();
        foreach (var target in TargetWidths)
        {
            if (target >= width) continue;
            var scaledHeight = Math.Max(1, (int)Math.Round((double)height * target / width));
            using var resized = image.Clone(ctx => ctx.Resize(target, scaledHeight));
            using var ms = new MemoryStream();
            resized.SaveAsWebp(ms, new WebpEncoder { Quality = WebpQuality });
            variants[target] = ms.ToArray();
        }

        return new RasterDecodeResult(width, height, variants);
    }

    // GIF: dimensions only, no variants. Uses ImageSharp so the caller does not
    // need a second decoder.
    public static (int Width, int Height) DecodeGifDimensions(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        try
        {
            var info = Image.Identify(bytes);
            if (info is null) throw new MediaDecodeException("decode_failed");
            return (info.Width, info.Height);
        }
        catch (Exception ex) when (ex is not MediaDecodeException)
        {
            throw new MediaDecodeException("decode_failed");
        }
    }

    private static Image? TryLoad(byte[] bytes)
    {
        try
        {
            return Image.Load(bytes);
        }
        catch (Exception)
        {
            return null;
        }
    }
}

public sealed record RasterDecodeResult(int Width, int Height, SortedDictionary<int, byte[]> Variants);

public sealed class MediaDecodeException : Exception
{
    public string Reason { get; }
    public MediaDecodeException(string reason) : base(reason)
    {
        Reason = reason;
    }
}
