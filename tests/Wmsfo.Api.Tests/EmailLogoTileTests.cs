using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Email;
using Wmsfo.Api.Media;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// contracts 7.8: EmailLogo.DeriveTile cuts the published logo into a
// 192 px PNG on an opaque white rounded tile; EmailLogoResolver answers the
// bundled logo when the published settings name no logoMedia.
public sealed class EmailLogoTileTests
{
    private static string TemplatesDir => Path.Combine(ContractsPaths.RepoRoot, "templates", "email");

    // A wide logo, red with a fully transparent left half.
    private static Image<Rgba32> Source()
    {
        var image = new Image<Rgba32>(400, 100, new Rgba32(220, 20, 20, 255));
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < 200; x++) row[x] = new Rgba32(0, 0, 0, 0);
            }
        });
        return image;
    }

    private static byte[] Encode(Action<Image<Rgba32>, Stream> save)
    {
        using var image = Source();
        using var ms = new MemoryStream();
        save(image, ms);
        return ms.ToArray();
    }

    private static void AssertOpaqueTile(byte[] tile)
    {
        Assert.Equal(ImageSniffer.Png, ImageSniffer.Detect(tile));
        using var image = Image.Load<Rgba32>(tile);
        Assert.Equal(EmailLogo.TileSize, image.Width);
        Assert.Equal(EmailLogo.TileSize, image.Height);

        // Every pixel inside the rounded tile is opaque, the transparent part
        // of the source included; only the corners outside the arc are clear.
        const int inset = EmailLogo.TileCornerRadius;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    var inCorner = (x < inset || x >= EmailLogo.TileSize - inset)
                        && (y < inset || y >= EmailLogo.TileSize - inset);
                    if (!inCorner) Assert.Equal(255, row[x].A);
                }
            }
        });
        Assert.Equal(0, image[0, 0].A);

        // The padding is white, the transparent half shows white, and the red
        // half is drawn.
        Assert.Equal(new Rgba32(255, 255, 255, 255), image[96, 8]);
        Assert.Equal(new Rgba32(255, 255, 255, 255), image[40, 96]);
        var red = image[150, 96];
        Assert.True(red.R > 200 && red.G < 60 && red.B < 60, $"expected red, got {red}");
    }

    [Fact]
    public void Derives_a_192_px_opaque_png_tile_from_a_png()
    {
        var png = Encode((img, s) => img.SaveAsPng(s));
        AssertOpaqueTile(EmailLogo.DeriveTile(png, ImageSniffer.Png));
    }

    [Fact]
    public void Derives_a_192_px_opaque_png_tile_from_a_webp()
    {
        var webp = Encode((img, s) => img.SaveAsWebp(s, new SixLabors.ImageSharp.Formats.Webp.WebpEncoder
        {
            FileFormat = SixLabors.ImageSharp.Formats.Webp.WebpFileFormatType.Lossless,
        }));
        AssertOpaqueTile(EmailLogo.DeriveTile(webp, ImageSniffer.Webp));
    }

    [Fact]
    public void The_same_source_derives_the_same_bytes()
    {
        var png = Encode((img, s) => img.SaveAsPng(s));
        Assert.Equal(
            EmailLogo.KeyFor(EmailLogo.DeriveTile(png, ImageSniffer.Png)),
            EmailLogo.KeyFor(EmailLogo.DeriveTile(png, ImageSniffer.Png)));
    }

    [Fact]
    public void Rejects_an_svg()
    {
        var svg = "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\"><rect width=\"10\" height=\"10\"/></svg>"u8.ToArray();
        Assert.Throws<MediaDecodeException>(() => EmailLogo.DeriveTile(svg, ImageSniffer.Svg));
    }

    [Fact]
    public async Task Resolver_answers_the_bundled_logo_when_logo_media_is_absent()
    {
        var templates = EmailTemplates.Load(TemplatesDir, "https://cdn.example.com", "https://site.example.com");
        var store = new RecordingStore();
        var resolver = new EmailLogoResolver(
            _ => Task.FromResult(new PublishedEmailSettings("North Pole Tracker", null)),
            "https://cdn.example.com", store, templates.Logo, NullLogger<EmailLogoResolver>.Instance);

        var brand = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal(templates.Logo.Url, brand.LogoUrl);
        Assert.Equal("North Pole Tracker", brand.SiteName);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task Resolver_answers_the_fallbacks_when_the_settings_cannot_be_read()
    {
        var templates = EmailTemplates.Load(TemplatesDir, "https://cdn.example.com", "https://site.example.com");
        var resolver = new EmailLogoResolver(
            _ => throw new InvalidOperationException("database down"),
            "https://cdn.example.com", new RecordingStore(), templates.Logo, NullLogger<EmailLogoResolver>.Instance);

        var brand = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal(templates.Logo.Url, brand.LogoUrl);
        Assert.Equal(EmailTemplates.DefaultSiteName, brand.SiteName);
    }

    [Fact]
    public async Task Resolver_answers_the_bundled_logo_for_an_svg_asset()
    {
        var templates = EmailTemplates.Load(TemplatesDir, "https://cdn.example.com", "https://site.example.com");
        var store = new RecordingStore();
        var asset = new EmailLogoAsset(Guid.NewGuid(), "svg", ImageSniffer.Svg, "ready", "media/x/logo.svg", DateTimeOffset.UnixEpoch);
        var resolver = new EmailLogoResolver(
            _ => Task.FromResult(new PublishedEmailSettings("", asset)),
            "https://cdn.example.com", store, templates.Logo, NullLogger<EmailLogoResolver>.Instance);

        var brand = await resolver.ResolveAsync(CancellationToken.None);

        Assert.Equal(templates.Logo.Url, brand.LogoUrl);
        Assert.Equal(EmailTemplates.DefaultSiteName, brand.SiteName);
        Assert.Equal(0, store.Calls);
    }

    // Counts every call; holds nothing.
    private sealed class RecordingStore : IObjectStore
    {
        public int Calls;

        public Task PutObjectAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl, string? tag = null, CancellationToken cancellationToken = default)
        { Calls++; return Task.CompletedTask; }

        public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
        { Calls++; return Task.CompletedTask; }

        public async IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        { Calls++; await Task.CompletedTask; yield break; }

        public Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default)
        { Calls++; return Task.CompletedTask; }

        public Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default)
        { Calls++; return Task.CompletedTask; }

        public Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<string?>(null); }

        public Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<ObjectHead?>(null); }

        public Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default)
        { Calls++; return Task.FromResult<ObjectContent?>(null); }

        public string PresignPut(string key, string contentType, string tag)
        { Calls++; return "https://example/upload"; }
    }
}
