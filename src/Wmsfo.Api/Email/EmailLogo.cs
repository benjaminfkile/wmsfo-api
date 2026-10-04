using System.Security.Cryptography;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Wmsfo.Api.Media;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Email;

// contracts 7.8 / platform.md 1.2: a bundled email image under
// templates/email/ (the logo, logo.png; the ornaments, ornaments.png; the
// light string, lights.png), served from the CDN at email/{sha256}.png where
// {sha256} is the lowercase hex SHA-256 of the file. The boot migrator calls
// EnsureWrittenAsync, which PUTs the object only when the key is absent, so
// each image change is written once and an existing key is never overwritten.
// The bundled logo is the fallback of EmailLogoResolver, which serves the
// published `logoMedia` as a tile DeriveTile cuts.
public sealed class EmailLogo
{
    public const string FileName = "logo.png";
    public const string OrnamentsFileName = "ornaments.png";
    public const string LightsFileName = "lights.png";
    public const string CdnPathPrefix = "email/";
    public const string PngContentType = "image/png";
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    // The derived tile: TileSize square, the source fitted inside TilePadding
    // on every side, on a white fill with TileCornerRadius rounded corners.
    public const int TileSize = 192;
    public const int TilePadding = 16;
    public const int TileCornerRadius = 24;

    private EmailLogo(byte[] bytes, string sha256, string key, string url)
    {
        Bytes = bytes;
        Sha256 = sha256;
        Key = key;
        Url = url;
    }

    public byte[] Bytes { get; }
    public string Sha256 { get; }
    public string Key { get; }

    // WMSFO_CDN_BASE_URL/email/{sha256}.png, the layout's {{logoUrl}},
    // {{ornamentsUrl}}, or {{lightsUrl}}.
    public string Url { get; }

    // The bundled image templates/email/{fileName}, logo.png by default.
    public static EmailLogo Load(string templatesDir, string cdnBaseUrl, string fileName = FileName)
    {
        ArgumentNullException.ThrowIfNull(templatesDir);
        ArgumentNullException.ThrowIfNull(cdnBaseUrl);
        ArgumentNullException.ThrowIfNull(fileName);
        var path = Path.Combine(templatesDir, fileName);
        if (!File.Exists(path))
            throw new InvalidOperationException($"Missing email image: {path}");

        var bytes = File.ReadAllBytes(path);
        var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var key = CdnPathPrefix + sha + ".png";
        return new EmailLogo(bytes, sha, key, UrlFor(cdnBaseUrl, key));
    }

    // PUTs the image with the immutable cache header when its key is not in the
    // store yet. Returns true when it wrote.
    public async Task<bool> EnsureWrittenAsync(IObjectStore store, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        var head = await store.HeadObjectAsync(Key, cancellationToken).ConfigureAwait(false);
        if (head is not null) return false;

        await store.PutObjectAsync(
            Key,
            Bytes,
            PngContentType,
            ImmutableCacheControl,
            tag: null,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    // The object key and CDN URL of PNG bytes: email/{sha256 of bytes}.png.
    public static string KeyFor(byte[] pngBytes) =>
        CdnPathPrefix + Convert.ToHexStringLower(SHA256.HashData(pngBytes)) + ".png";

    public static string UrlFor(string cdnBaseUrl, string key) => cdnBaseUrl.TrimEnd('/') + "/" + key;

    // The email tile of a raster logo (png, jpeg, webp, or gif; the first
    // frame of an animation): the image fitted inside a TileSize square less
    // TilePadding on each side, centred over an opaque white tile with
    // rounded corners, encoded as PNG. Transparent parts of the source show
    // the white tile, so the logo reads the same on light and dark clients.
    // Throws MediaDecodeException for svg, any other content type, or bytes
    // that do not decode.
    public static byte[] DeriveTile(byte[] source, string contentType)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (contentType is not (ImageSniffer.Png or ImageSniffer.Jpeg or ImageSniffer.Webp or ImageSniffer.Gif))
            throw new MediaDecodeException("unsupported_content_type");

        using var image = VariantDeriver.LoadRaster(source);
        while (image.Frames.Count > 1) image.Frames.RemoveFrame(1);

        var box = TileSize - 2 * TilePadding;
        var scale = Math.Min((double)box / image.Width, (double)box / image.Height);
        var width = Math.Clamp((int)Math.Round(image.Width * scale), 1, box);
        var height = Math.Clamp((int)Math.Round(image.Height * scale), 1, box);
        image.Mutate(ctx => ctx.Resize(width, height));

        using var tile = new Image<Rgba32>(TileSize, TileSize);
        tile.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32(255, 255, 255, CornerAlpha(x, y));
            }
        });
        var offset = new Point((TileSize - width) / 2, (TileSize - height) / 2);
        tile.Mutate(ctx => ctx.DrawImage(image, offset, 1f));

        using var ms = new MemoryStream();
        tile.SaveAsPng(ms, new PngEncoder { ColorType = PngColorType.RgbWithAlpha });
        return ms.ToArray();
    }

    // Coverage of pixel (x, y) by the rounded tile: 255 inside, 0 outside a
    // corner's arc, and the covered fraction along the arc.
    private static byte CornerAlpha(int x, int y)
    {
        const double r = TileCornerRadius;
        var px = x + 0.5;
        var py = y + 0.5;
        var cx = px < r ? r : px > TileSize - r ? TileSize - r : px;
        var cy = py < r ? r : py > TileSize - r ? TileSize - r : py;
        var dx = px - cx;
        var dy = py - cy;
        if (dx == 0 || dy == 0) return 255;
        var coverage = Math.Clamp(r - Math.Sqrt(dx * dx + dy * dy) + 0.5, 0, 1);
        return (byte)Math.Round(coverage * 255);
    }
}
