namespace Wmsfo.Api.Media;

// api.md 11.3 step 3: sniff the object type from the leading bytes. Returns the
// canonical MIME string ("image/png", "image/jpeg", "image/webp", "image/gif",
// "image/svg+xml") or null. Confirm compares this against the ticket's declared
// content type; a mismatch is a 400 validation_failed.
public static class ImageSniffer
{
    public const string Png = "image/png";
    public const string Jpeg = "image/jpeg";
    public const string Webp = "image/webp";
    public const string Gif = "image/gif";
    public const string Svg = "image/svg+xml";

    public static string? Detect(ReadOnlySpan<byte> bytes)
    {
        if (StartsWith(bytes, PngMagic)) return Png;
        if (StartsWith(bytes, JpegMagic)) return Jpeg;
        if (IsWebp(bytes)) return Webp;
        if (StartsWith(bytes, Gif87Magic) || StartsWith(bytes, Gif89Magic)) return Gif;
        if (LooksLikeSvg(bytes)) return Svg;
        return null;
    }

    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
    private static readonly byte[] JpegMagic = { 0xFF, 0xD8, 0xFF };
    private static readonly byte[] Gif87Magic = { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 };
    private static readonly byte[] Gif89Magic = { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 };

    private static bool StartsWith(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> prefix)
    {
        if (bytes.Length < prefix.Length) return false;
        return bytes[..prefix.Length].SequenceEqual(prefix);
    }

    // RIFF....WEBP header (12 bytes).
    private static bool IsWebp(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return false;
        return bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F'
            && bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';
    }

    // Look for an SVG root within the first 512 bytes (skips BOM and leading
    // whitespace, tolerates an <?xml prologue and comments in between).
    private static bool LooksLikeSvg(ReadOnlySpan<byte> bytes)
    {
        var scan = bytes.Length > 512 ? bytes[..512] : bytes;
        for (var i = 0; i < scan.Length - 4; i++)
        {
            if (scan[i] == '<'
                && (scan[i + 1] == 's' || scan[i + 1] == 'S')
                && (scan[i + 2] == 'v' || scan[i + 2] == 'V')
                && (scan[i + 3] == 'g' || scan[i + 3] == 'G'))
            {
                // Must be either end of tag, whitespace, or attribute after `svg`.
                var next = scan[i + 4];
                if (next == '>' || next == ' ' || next == '\t' || next == '\r' || next == '\n')
                    return true;
            }
        }
        return false;
    }
}
