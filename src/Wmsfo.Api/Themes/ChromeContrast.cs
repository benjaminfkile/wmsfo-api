using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Themes;

// contracts 4.5 Themes "Chrome and overlay": every colour `#rrggbb` or
// `#rrggbbaa`, every opacity 0 to 1, every key required and no other key, and
// `chrome.text` on `chrome.bg` and `chrome.tileFg` on `chrome.tile` at 4.5:1 or
// better by WCAG relative luminance. Ratio composites a colour carrying alpha
// over the other colour before it measures.
public static partial class ChromeContrast
{
    public const double MinimumRatio = 4.5;

    public static readonly IReadOnlyList<string> ChromeKeys =
        ["bg", "fg", "text", "tile", "tileFg", "panel", "accent"];

    public static readonly IReadOnlyList<string> OverlayColourKeys =
        ["routeColor", "arrowColor", "timeLabelBg", "timeLabelFg", "userColor"];

    public static readonly IReadOnlyList<string> OverlayOpacityKeys =
        ["routeOpacity", "timeLabelOpacity"];

    [GeneratedRegex("^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$")]
    private static partial Regex ColourPattern();

    public static bool IsColour(string? value) => value is not null && ColourPattern().IsMatch(value);

    // The WCAG contrast ratio of fg on bg. A colour with alpha is composited
    // over the other colour's opaque rgb first.
    public static double Ratio(string fg, string bg)
    {
        var f = Parse(fg);
        var b = Parse(bg);
        var fo = Composite(f, b);
        var bo = Composite(b, f);
        var lf = Luminance(fo);
        var lb = Luminance(bo);
        var hi = Math.Max(lf, lb);
        var lo = Math.Min(lf, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    // Validates a chrome object; returns (path, reason) per failure.
    public static IReadOnlyList<(string Path, string Reason)> ValidateChrome(JsonElement chrome)
    {
        var failures = new List<(string, string)>();
        if (chrome.ValueKind != JsonValueKind.Object)
        {
            failures.Add(("chrome", "must be an object"));
            return failures;
        }
        foreach (var p in chrome.EnumerateObject())
        {
            if (!ChromeKeys.Contains(p.Name)) failures.Add(("chrome." + p.Name, "is not a chrome key"));
        }
        var colours = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in ChromeKeys)
        {
            if (!chrome.TryGetProperty(key, out var el))
                failures.Add(("chrome." + key, "is required"));
            else if (el.ValueKind != JsonValueKind.String || !IsColour(el.GetString()))
                failures.Add(("chrome." + key, "must be #rrggbb or #rrggbbaa"));
            else
                colours[key] = el.GetString()!;
        }
        CheckPair(colours, "text", "bg", failures);
        CheckPair(colours, "tileFg", "tile", failures);
        return failures;
    }

    // Validates an overlay object; returns (path, reason) per failure.
    public static IReadOnlyList<(string Path, string Reason)> ValidateOverlay(JsonElement overlay)
    {
        var failures = new List<(string, string)>();
        if (overlay.ValueKind != JsonValueKind.Object)
        {
            failures.Add(("overlay", "must be an object"));
            return failures;
        }
        foreach (var p in overlay.EnumerateObject())
        {
            if (!OverlayColourKeys.Contains(p.Name) && !OverlayOpacityKeys.Contains(p.Name))
                failures.Add(("overlay." + p.Name, "is not an overlay key"));
        }
        foreach (var key in OverlayColourKeys)
        {
            if (!overlay.TryGetProperty(key, out var el))
                failures.Add(("overlay." + key, "is required"));
            else if (el.ValueKind != JsonValueKind.String || !IsColour(el.GetString()))
                failures.Add(("overlay." + key, "must be #rrggbb or #rrggbbaa"));
        }
        foreach (var key in OverlayOpacityKeys)
        {
            if (!overlay.TryGetProperty(key, out var el))
                failures.Add(("overlay." + key, "is required"));
            else if (el.ValueKind != JsonValueKind.Number || !el.TryGetDouble(out var d) || d < 0 || d > 1)
                failures.Add(("overlay." + key, "must be a number from 0 to 1"));
        }
        return failures;
    }

    // The DTOs of a chrome and an overlay that passed validation.
    public static Chrome ToChrome(JsonElement chrome) => new()
    {
        Bg = chrome.GetProperty("bg").GetString()!,
        Fg = chrome.GetProperty("fg").GetString()!,
        Text = chrome.GetProperty("text").GetString()!,
        Tile = chrome.GetProperty("tile").GetString()!,
        TileFg = chrome.GetProperty("tileFg").GetString()!,
        Panel = chrome.GetProperty("panel").GetString()!,
        Accent = chrome.GetProperty("accent").GetString()!,
    };

    public static Overlay ToOverlay(JsonElement overlay) => new()
    {
        RouteColor = overlay.GetProperty("routeColor").GetString()!,
        RouteOpacity = overlay.GetProperty("routeOpacity").GetDouble(),
        ArrowColor = overlay.GetProperty("arrowColor").GetString()!,
        TimeLabelBg = overlay.GetProperty("timeLabelBg").GetString()!,
        TimeLabelFg = overlay.GetProperty("timeLabelFg").GetString()!,
        TimeLabelOpacity = overlay.GetProperty("timeLabelOpacity").GetDouble(),
        UserColor = overlay.GetProperty("userColor").GetString()!,
    };

    private static void CheckPair(Dictionary<string, string> colours, string fg, string bg, List<(string, string)> failures)
    {
        if (!colours.TryGetValue(fg, out var f) || !colours.TryGetValue(bg, out var b)) return;
        var ratio = Ratio(f, b);
        if (ratio < MinimumRatio)
        {
            failures.Add(("chrome." + fg, string.Format(CultureInfo.InvariantCulture,
                "contrast on chrome.{0} is {1:0.00}:1; must be 4.5:1 or better", bg, ratio)));
        }
    }

    private readonly record struct Rgba(double R, double G, double B, double A);

    private static Rgba Parse(string hex)
    {
        if (!IsColour(hex)) throw new FormatException($"not a #rrggbb or #rrggbbaa colour: {hex}");
        static double Byte(string s, int at) =>
            int.Parse(s.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255.0;
        return new Rgba(Byte(hex, 1), Byte(hex, 3), Byte(hex, 5), hex.Length == 9 ? Byte(hex, 7) : 1.0);
    }

    // c composited over the opaque rgb of under.
    private static Rgba Composite(Rgba c, Rgba under) => new(
        c.R * c.A + under.R * (1 - c.A),
        c.G * c.A + under.G * (1 - c.A),
        c.B * c.A + under.B * (1 - c.A),
        1.0);

    private static double Luminance(Rgba c)
    {
        static double Linear(double v) => v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        return 0.2126 * Linear(c.R) + 0.7152 * Linear(c.G) + 0.0722 * Linear(c.B);
    }
}
