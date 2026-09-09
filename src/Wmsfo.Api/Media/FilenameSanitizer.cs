using System.Text;

namespace Wmsfo.Api.Media;

// api.md 11.2: `[^A-Za-z0-9._-]` to `-`, collapse repeats, trim to 100, and
// make sure the extension matches the declared type: `png`, `jpg` or `jpeg`,
// `webp`, `gif`, `svg`. Returns null when the extension does not match; the
// caller returns 400 validation_failed then.
public static class FilenameSanitizer
{
    public const int MaxLength = 100;

    public static string? Sanitize(string filename, string contentType)
    {
        if (string.IsNullOrWhiteSpace(filename)) return null;
        var trimmed = filename.Trim();

        // Drop any path prefix defensively - the browser file input carries the
        // base name, but a keyed request could send anything.
        var lastSep = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        if (lastSep >= 0) trimmed = trimmed[(lastSep + 1)..];
        if (trimmed.Length == 0) return null;

        var sb = new StringBuilder(trimmed.Length);
        var prevHyphen = false;
        foreach (var ch in trimmed)
        {
            if (IsAllowed(ch))
            {
                sb.Append(ch);
                prevHyphen = false;
            }
            else
            {
                if (!prevHyphen) sb.Append('-');
                prevHyphen = true;
            }
        }
        var sanitized = sb.ToString().Trim('-');
        if (sanitized.Length == 0) return null;

        // Extension check first: extract the (allowed) extension and truncate
        // the base name so total length ≤ MaxLength while the extension is kept.
        var dot = sanitized.LastIndexOf('.');
        if (dot < 0 || dot == sanitized.Length - 1) return null;
        var ext = sanitized[(dot + 1)..].ToLowerInvariant();
        if (!IsExtensionAllowedFor(ext, contentType)) return null;
        // Trim trailing hyphens on the base name (a replaced char right before
        // the dot leaves a cosmetically ugly `foo-.ext`).
        var baseTrimmed = sanitized[..dot].TrimEnd('-');
        if (baseTrimmed.Length == 0) return null;
        sanitized = baseTrimmed + "." + ext;

        if (sanitized.Length > MaxLength)
        {
            var extWithDot = "." + ext;
            var baseName = sanitized[..dot];
            var keep = MaxLength - extWithDot.Length;
            if (keep <= 0) return null;
            sanitized = baseName[..Math.Min(baseName.Length, keep)].TrimEnd('-') + extWithDot;
            if (sanitized.Length == 0) return null;
        }

        return sanitized;
    }

    private static bool IsAllowed(char c) =>
        (c >= 'A' && c <= 'Z')
        || (c >= 'a' && c <= 'z')
        || (c >= '0' && c <= '9')
        || c == '.' || c == '_' || c == '-';

    private static bool IsExtensionAllowedFor(string ext, string contentType) => contentType switch
    {
        ImageSniffer.Png => ext == "png",
        ImageSniffer.Jpeg => ext is "jpg" or "jpeg",
        ImageSniffer.Webp => ext == "webp",
        ImageSniffer.Gif => ext == "gif",
        ImageSniffer.Svg => ext == "svg",
        _ => false,
    };
}
