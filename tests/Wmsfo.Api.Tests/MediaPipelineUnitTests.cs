using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Media;

namespace Wmsfo.Api.Tests;

// api.md 21 unit coverage: image sniffing, variant width selection (700 px source
// yields 480 only), and filename sanitizing. The integration tests exercise the
// full media pipeline end-to-end; these tests keep the pure functions honest.
public sealed class MediaPipelineUnitTests
{
    // ---------- ImageSniffer ----------

    [Fact]
    public void Sniffer_detects_png()
    {
        var bytes = BuildImage(new PngEncoder(), 32, 32);
        Assert.Equal("image/png", ImageSniffer.Detect(bytes));
    }

    [Fact]
    public void Sniffer_detects_jpeg()
    {
        var bytes = BuildImage(new JpegEncoder(), 32, 32);
        Assert.Equal("image/jpeg", ImageSniffer.Detect(bytes));
    }

    [Fact]
    public void Sniffer_detects_gif()
    {
        var bytes = BuildImage(new GifEncoder(), 32, 32);
        Assert.Equal("image/gif", ImageSniffer.Detect(bytes));
    }

    [Fact]
    public void Sniffer_detects_webp()
    {
        var bytes = BuildImage(new WebpEncoder(), 32, 32);
        Assert.Equal("image/webp", ImageSniffer.Detect(bytes));
    }

    [Fact]
    public void Sniffer_detects_svg()
    {
        var svg = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        Assert.Equal("image/svg+xml", ImageSniffer.Detect(svg));
    }

    [Fact]
    public void Sniffer_returns_null_for_arbitrary_bytes()
    {
        Assert.Null(ImageSniffer.Detect(new byte[] { 0x00, 0x01, 0x02, 0x03 }));
    }

    // ---------- VariantDeriver ----------

    // Task 272 acceptance criterion: a 700 px source yields the 480 variant only.
    [Fact]
    public void Variants_from_700px_source_only_has_480()
    {
        var bytes = BuildImage(new PngEncoder(), 700, 400);
        var result = VariantDeriver.DecodeRaster(bytes);
        Assert.Equal(700, result.Width);
        Assert.Equal(400, result.Height);
        Assert.Equal(new[] { 480 }, result.Variants.Keys.ToArray());
    }

    // A 1024 px source narrower than 1600 and 960 yields the two smaller widths.
    [Fact]
    public void Variants_from_1024px_source_has_480_and_960()
    {
        var bytes = BuildImage(new PngEncoder(), 1024, 512);
        var result = VariantDeriver.DecodeRaster(bytes);
        Assert.Equal(new[] { 480, 960 }, result.Variants.Keys.ToArray());
    }

    // A source at exactly 1600 px produces variants only strictly narrower.
    [Fact]
    public void Variants_from_1600px_source_has_480_and_960_only()
    {
        var bytes = BuildImage(new PngEncoder(), 1600, 800);
        var result = VariantDeriver.DecodeRaster(bytes);
        Assert.Equal(new[] { 480, 960 }, result.Variants.Keys.ToArray());
    }

    // A source narrower than every target width has no variants.
    [Fact]
    public void Variants_from_320px_source_are_empty()
    {
        var bytes = BuildImage(new PngEncoder(), 320, 200);
        var result = VariantDeriver.DecodeRaster(bytes);
        Assert.Empty(result.Variants);
    }

    [Fact]
    public void Decode_bogus_bytes_throws_decode_failed()
    {
        var ex = Assert.Throws<MediaDecodeException>(() => VariantDeriver.DecodeRaster(new byte[] { 0, 1, 2, 3 }));
        Assert.Equal("decode_failed", ex.Reason);
    }

    // ---------- FilenameSanitizer ----------

    [Fact]
    public void Sanitize_keeps_allowed_characters()
    {
        Assert.Equal("hangar_1.png", FilenameSanitizer.Sanitize("hangar_1.png", "image/png"));
    }

    [Fact]
    public void Sanitize_replaces_disallowed_and_collapses_repeats()
    {
        Assert.Equal("santa-flyover.png", FilenameSanitizer.Sanitize("santa  flyover!!.png", "image/png"));
    }

    [Fact]
    public void Sanitize_strips_path_prefixes()
    {
        Assert.Equal("logo.svg", FilenameSanitizer.Sanitize("C:\\stuff\\logo.svg", "image/svg+xml"));
        Assert.Equal("logo.svg", FilenameSanitizer.Sanitize("/tmp/uploads/logo.svg", "image/svg+xml"));
    }

    [Fact]
    public void Sanitize_rejects_extension_mismatch()
    {
        Assert.Null(FilenameSanitizer.Sanitize("hangar.png", "image/jpeg"));
        Assert.Null(FilenameSanitizer.Sanitize("noext", "image/png"));
    }

    [Fact]
    public void Sanitize_accepts_both_jpg_and_jpeg_for_jpeg_type()
    {
        Assert.Equal("cat.jpg", FilenameSanitizer.Sanitize("cat.jpg", "image/jpeg"));
        Assert.Equal("cat.jpeg", FilenameSanitizer.Sanitize("cat.jpeg", "image/jpeg"));
    }

    [Fact]
    public void Sanitize_trims_to_100_characters()
    {
        var raw = new string('a', 150) + ".png";
        var sanitized = FilenameSanitizer.Sanitize(raw, "image/png");
        Assert.NotNull(sanitized);
        Assert.Equal(100, sanitized!.Length);
    }

    private static byte[] BuildImage(SixLabors.ImageSharp.Formats.IImageEncoder encoder, int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 200, 200, 255));
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }
}
