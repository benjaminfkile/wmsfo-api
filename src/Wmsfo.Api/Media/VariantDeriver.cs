using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Wmsfo.Api.Media;

// api.md 11.3 step 4: decode the raster bytes with a 40-megapixel ceiling; for
// each width in (480, 960, 1600) that is less than the source width, resize
// (aspect kept) and encode WebP quality 82. GIF: dimensions only, no variants.
// SVG has no decode path here. The tile pyramid step (api.md 11.3 step 5) uses
// LoadRaster + DeriveVariants so the decoded image is decoded once and reused.
public static class VariantDeriver
{
    public const int MaxMegapixels = 40;
    public const long MaxPixelCount = MaxMegapixels * 1_000_000L;
    public const int WebpQuality = 82;
    public static readonly int[] TargetWidths = { 480, 960, 1600 };

    // Load the raster into an ImageSharp Image and enforce the 40-megapixel
    // ceiling. The caller owns the returned image and must dispose it.
    public static Image LoadRaster(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        Image image;
        try
        {
            image = Image.Load(bytes);
        }
        catch (Exception)
        {
            throw new MediaDecodeException("decode_failed");
        }

        var pixels = (long)image.Width * image.Height;
        if (pixels > MaxPixelCount)
        {
            image.Dispose();
            throw new MediaDecodeException("over_megapixel_ceiling");
        }
        return image;
    }

    // Derive the WebP variants narrower than the source width, keyed by width.
    public static SortedDictionary<int, byte[]> DeriveVariants(Image image)
    {
        ArgumentNullException.ThrowIfNull(image);
        var variants = new SortedDictionary<int, byte[]>();
        var width = image.Width;
        var height = image.Height;
        foreach (var target in TargetWidths)
        {
            if (target >= width) continue;
            var scaledHeight = Math.Max(1, (int)Math.Round((double)height * target / width));
            using var resized = image.Clone(ctx => ctx.Resize(target, scaledHeight));
            using var ms = new MemoryStream();
            resized.SaveAsWebp(ms, new WebpEncoder { Quality = WebpQuality });
            variants[target] = ms.ToArray();
        }
        return variants;
    }

    // Convenience: decode + derive in one call, disposing the image. Used by
    // unit tests; the confirm handler keeps the image alive for the tiler.
    public static RasterDecodeResult DecodeRaster(byte[] bytes)
    {
        using var image = LoadRaster(bytes);
        return new RasterDecodeResult(image.Width, image.Height, DeriveVariants(image));
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
