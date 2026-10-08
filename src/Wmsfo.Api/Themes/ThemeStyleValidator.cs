using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wmsfo.Api.Themes;

// contracts 4.5 Themes "Style rules". Validate checks a style body's shape
// and returns the reasons it is refused (empty when it passes): the size cap
// on the canonical form (64 KB google, 512 KB maplibre); a google body is an
// array of objects with only featureType, elementType, and stylers; a
// maplibre body is an object with version 8, sources exactly basemap (vector)
// and optionally terrain (raster-dem), neither with url or tiles, every layer
// with a source (every type but background) naming one of the two, and no
// string value starting with http. Rewrite answers the canonical bytes the
// API stores: for maplibre, glyphs (when present) becomes the CDN glyph
// template and sprite becomes the theme's sprite base, or is removed while the
// theme has no confirmed set. Rewrite runs after Validate, so the http rule
// sees only what the caller sent.
public static class ThemeStyleValidator
{
    public const int GoogleMaxBytes = 64 * 1024;
    public const int MapLibreMaxBytes = 512 * 1024;
    public const string Google = "google";
    public const string MapLibre = "maplibre";

    private static readonly HashSet<string> GoogleKeys = new(StringComparer.Ordinal)
    {
        "featureType", "elementType", "stylers",
    };

    public static IReadOnlyList<string> Validate(string renderer, JsonElement style)
    {
        var reasons = new List<string>();
        var max = renderer == Google ? GoogleMaxBytes : MapLibreMaxBytes;
        var size = ThemeStyles.Canonicalize(style).Length;
        if (size > max)
        {
            reasons.Add(string.Format(CultureInfo.InvariantCulture,
                "the canonical style is {0} bytes; a {1} style must be at most {2} bytes", size, renderer, max));
            return reasons;
        }
        if (renderer == Google) ValidateGoogle(style, reasons);
        else ValidateMapLibre(style, reasons);
        return reasons;
    }

    // The canonical bytes of the stored body. spriteBase is the theme's sprite
    // base (1.3 spriteUrl) when it has a confirmed set, null otherwise.
    public static byte[] Rewrite(string renderer, JsonElement style, string cdnBaseUrl, string? spriteBase)
    {
        if (renderer != MapLibre) return ThemeStyles.Canonicalize(style);
        var node = JsonNode.Parse(style.GetRawText()) as JsonObject
            ?? throw new ArgumentException("a maplibre style is an object", nameof(style));
        if (node.ContainsKey("glyphs"))
            node["glyphs"] = GlyphsTemplate(cdnBaseUrl);
        if (spriteBase is not null) node["sprite"] = spriteBase;
        else node.Remove("sprite");
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return ThemeStyles.Canonicalize(doc.RootElement);
    }

    public static string GlyphsTemplate(string cdnBaseUrl) =>
        cdnBaseUrl.TrimEnd('/') + "/basemap/glyphs/{fontstack}/{range}.pbf";

    // contracts 1.3 spriteUrl: the sprite base of a confirmed set.
    public static string SpriteBase(string cdnBaseUrl, long themeId, string spriteSha256) =>
        cdnBaseUrl.TrimEnd('/') + "/themes/" + themeId.ToString(CultureInfo.InvariantCulture)
        + "/sprites/" + spriteSha256 + "/sprite";

    private static void ValidateGoogle(JsonElement style, List<string> reasons)
    {
        if (style.ValueKind != JsonValueKind.Array)
        {
            reasons.Add("a google style must be a JSON array of objects");
            return;
        }
        var i = 0;
        foreach (var entry in style.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                reasons.Add($"entry {i} must be an object");
            }
            else
            {
                foreach (var p in entry.EnumerateObject())
                {
                    if (!GoogleKeys.Contains(p.Name))
                        reasons.Add($"entry {i} has key `{p.Name}`; only featureType, elementType, and stylers are allowed");
                }
            }
            i++;
        }
    }

    private static void ValidateMapLibre(JsonElement style, List<string> reasons)
    {
        if (style.ValueKind != JsonValueKind.Object)
        {
            reasons.Add("a maplibre style must be a JSON object");
            return;
        }
        if (!style.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out var v) || v != 8)
        {
            reasons.Add("version must be 8");
        }

        if (!style.TryGetProperty("sources", out var sources) || sources.ValueKind != JsonValueKind.Object)
        {
            reasons.Add("sources must be an object holding basemap and optionally terrain");
        }
        else
        {
            var hasBasemap = false;
            foreach (var source in sources.EnumerateObject())
            {
                string expectedType;
                if (source.Name == "basemap") { expectedType = "vector"; hasBasemap = true; }
                else if (source.Name == "terrain") expectedType = "raster-dem";
                else
                {
                    reasons.Add($"source `{source.Name}` is not allowed; sources are basemap and optionally terrain");
                    continue;
                }
                if (source.Value.ValueKind != JsonValueKind.Object)
                {
                    reasons.Add($"source `{source.Name}` must be an object");
                    continue;
                }
                if (!source.Value.TryGetProperty("type", out var type)
                    || type.ValueKind != JsonValueKind.String || type.GetString() != expectedType)
                    reasons.Add($"source `{source.Name}` must have type {expectedType}");
                if (source.Value.TryGetProperty("url", out _))
                    reasons.Add($"source `{source.Name}` must not carry url");
                if (source.Value.TryGetProperty("tiles", out _))
                    reasons.Add($"source `{source.Name}` must not carry tiles");
            }
            if (!hasBasemap) reasons.Add("sources must hold basemap");
        }

        if (!style.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
        {
            reasons.Add("layers must be an array");
        }
        else
        {
            var i = 0;
            foreach (var layer in layers.EnumerateArray())
            {
                if (layer.ValueKind != JsonValueKind.Object)
                {
                    reasons.Add($"layer {i} must be an object");
                    i++;
                    continue;
                }
                var type = layer.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : null;
                if (type != "background")
                {
                    var source = layer.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String
                        ? s.GetString() : null;
                    if (source is not ("basemap" or "terrain"))
                        reasons.Add($"layer {i} must name source basemap or terrain, not `{source}`");
                }
                i++;
            }
        }

        var httpAt = FindHttpString(style, "");
        if (httpAt is not null) reasons.Add($"the string at `{httpAt}` starts with http; a style carries no URLs");
    }

    // The path of the first string value starting with http, or null.
    private static string? FindHttpString(JsonElement el, string path)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.String:
                return el.GetString()!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                    ? (path.Length == 0 ? "$" : path) : null;
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    var found = FindHttpString(p.Value, path.Length == 0 ? p.Name : path + "." + p.Name);
                    if (found is not null) return found;
                }
                return null;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var child in el.EnumerateArray())
                {
                    var found = FindHttpString(child, path + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                    if (found is not null) return found;
                    i++;
                }
                return null;
            default:
                return null;
        }
    }
}
