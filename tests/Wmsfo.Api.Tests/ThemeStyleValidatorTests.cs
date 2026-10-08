using System.Text;
using System.Text.Json;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.Tests;

// contracts 4.5 Themes "Style rules" and "Chrome and overlay": the seeded
// bodies and colours pass unchanged, each rule refuses with its reason, and
// the contrast ratio reads as WCAG defines it.
public class ThemeStyleValidatorTests
{
    private const string Cdn = "https://cdn.example";

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private const string SmallMapLibre =
        """{"version":8,"sources":{"basemap":{"type":"vector"},"terrain":{"type":"raster-dem"}},"layers":[{"id":"bg","type":"background","paint":{"background-color":"#ffffff"}},{"id":"water","type":"fill","source":"basemap","source-layer":"water"}]}""";

    [Fact]
    public void The_seeded_bodies_pass_and_rewrite_unchanged()
    {
        var root = ThemeStyles.ResolveRoot(ContractsPaths.RepoRoot)!;
        var styles = ThemeStyles.Load(root);
        foreach (var (seed, body) in TrackerThemeSeed.Themes.Zip(styles.Bodies))
        {
            using var doc = JsonDocument.Parse(body.Bytes);
            Assert.Empty(ThemeStyleValidator.Validate(seed.Renderer, doc.RootElement));
            var rewritten = ThemeStyleValidator.Rewrite(seed.Renderer, doc.RootElement, Cdn, spriteBase: null);
            Assert.Equal(seed.StyleSha256, CanonicalJson.Sha256Hex(rewritten));
        }
    }

    [Fact]
    public void The_seeded_colours_pass()
    {
        foreach (var seed in TrackerThemeSeed.Themes)
        {
            Assert.Empty(ChromeContrast.ValidateChrome(Parse(seed.Chrome)));
            Assert.Empty(ChromeContrast.ValidateOverlay(Parse(seed.Overlay)));
        }
    }

    [Theory]
    [InlineData("""[{"featureType":"road","colour":"red"}]""", "only featureType, elementType, and stylers")]
    [InlineData("""{"featureType":"road"}""", "array of objects")]
    public void Google_rules_refuse_with_their_reason(string style, string reason)
    {
        var reasons = ThemeStyleValidator.Validate("google", Parse(style));
        Assert.Contains(reasons, r => r.Contains(reason, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""{"version":7,"sources":{"basemap":{"type":"vector"}},"layers":[]}""", "version must be 8")]
    [InlineData("""{"version":8,"sources":{"basemap":{"type":"vector"},"osm":{"type":"vector"}},"layers":[]}""", "source `osm` is not allowed")]
    [InlineData("""{"version":8,"sources":{"basemap":{"type":"vector","url":"pmtiles://x"}},"layers":[]}""", "must not carry url")]
    [InlineData("""{"version":8,"sources":{"basemap":{"type":"vector","tiles":["x"]}},"layers":[]}""", "must not carry tiles")]
    [InlineData("""{"version":8,"sources":{"basemap":{"type":"raster"}},"layers":[]}""", "must have type vector")]
    [InlineData("""{"version":8,"sources":{"basemap":{"type":"vector"}},"layers":[{"id":"a","type":"fill","source":"osm"}]}""", "must name source basemap or terrain")]
    [InlineData("""{"version":8,"sources":{"basemap":{"type":"vector"}},"layers":[{"id":"a","type":"fill"}]}""", "must name source basemap or terrain")]
    [InlineData("""{"version":8,"sources":{"basemap":{"type":"vector"}},"layers":[],"metadata":{"x":"https://tiles.example"}}""", "starts with http")]
    public void MapLibre_rules_refuse_with_their_reason(string style, string reason)
    {
        var reasons = ThemeStyleValidator.Validate("maplibre", Parse(style));
        Assert.Contains(reasons, r => r.Contains(reason, StringComparison.Ordinal));
    }

    [Fact]
    public void The_size_cap_is_on_the_canonical_form()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < 2000; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append("""{"featureType":"road","stylers":[{"color":"#123456"}]}""");
        }
        sb.Append(']');
        var reasons = ThemeStyleValidator.Validate("google", Parse(sb.ToString()));
        Assert.Contains(reasons, r => r.Contains("at most 65536 bytes", StringComparison.Ordinal));
        Assert.Empty(ThemeStyleValidator.Validate("maplibre", Parse(SmallMapLibre)));
    }

    [Fact]
    public void Rewrite_sets_glyphs_and_sprite_or_removes_sprite()
    {
        var style = Parse(SmallMapLibre.Replace("\"version\":8", "\"version\":8,\"glyphs\":\"{fontstack}/{range}.pbf\",\"sprite\":\"sprite\""));
        Assert.Empty(ThemeStyleValidator.Validate("maplibre", style));

        using (var without = JsonDocument.Parse(ThemeStyleValidator.Rewrite("maplibre", style, Cdn + "/", spriteBase: null)))
        {
            Assert.Equal("https://cdn.example/basemap/glyphs/{fontstack}/{range}.pbf",
                without.RootElement.GetProperty("glyphs").GetString());
            Assert.False(without.RootElement.TryGetProperty("sprite", out _));
        }
        var spriteBase = ThemeStyleValidator.SpriteBase(Cdn, 7, new string('a', 64));
        Assert.Equal("https://cdn.example/themes/7/sprites/" + new string('a', 64) + "/sprite", spriteBase);
        using var with = JsonDocument.Parse(ThemeStyleValidator.Rewrite("maplibre", style, Cdn, spriteBase));
        Assert.Equal(spriteBase, with.RootElement.GetProperty("sprite").GetString());
    }

    [Fact]
    public void Contrast_reads_as_wcag()
    {
        Assert.Equal(21.0, ChromeContrast.Ratio("#000000", "#ffffff"), 3);
        Assert.Equal(1.0, ChromeContrast.Ratio("#777777", "#777777"), 3);
        // Fully transparent text takes the background's colour.
        Assert.Equal(1.0, ChromeContrast.Ratio("#00000000", "#ffffff"), 3);
        Assert.True(ChromeContrast.Ratio("#000000", "#ffffff80") > 4.5);
    }

    [Fact]
    public void Chrome_and_overlay_failures_are_at_their_paths()
    {
        var chrome = """{"bg":"#ffffff","fg":"#5f6368","text":"#eeeeee","tile":"#e8f0fe","tileFg":"#e8f0fe","panel":"#ffffffe6","accent":"#1a56c4"}""";
        var paths = ChromeContrast.ValidateChrome(Parse(chrome)).Select(f => f.Path).ToArray();
        Assert.Contains("chrome.text", paths);
        Assert.Contains("chrome.tileFg", paths);

        var missing = """{"bg":"#ffffff","fg":"#5f6368","text":"#202124","tile":"#e8f0fe","tileFg":"#1a56c4","panel":"#ffffff"}""";
        Assert.Contains(ChromeContrast.ValidateChrome(Parse(missing)), f => f.Path == "chrome.accent");

        var extra = """{"bg":"#ffffff","fg":"#5f6368","text":"#202124","tile":"#e8f0fe","tileFg":"#1a56c4","panel":"#ffffff","accent":"#123","glow":"#ffffff"}""";
        var extraPaths = ChromeContrast.ValidateChrome(Parse(extra)).Select(f => f.Path).ToArray();
        Assert.Contains("chrome.glow", extraPaths);
        Assert.Contains("chrome.accent", extraPaths);

        var overlay = """{"routeColor":"#1a56c4","routeOpacity":1.5,"arrowColor":"#1a56c4","timeLabelBg":"#ffffff","timeLabelFg":"#202124","timeLabelOpacity":1,"userColor":"#c62828"}""";
        Assert.Equal(["overlay.routeOpacity"], ChromeContrast.ValidateOverlay(Parse(overlay)).Select(f => f.Path).ToArray());
    }
}
