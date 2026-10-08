using System.Text.Json;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.Tests;

// sql.md 6: the seeded tracker rows in TrackerThemeSeed equal
// contracts/fixtures/themes/seed.json, each style body hashes to its row, and
// the valley package key is the hash of the canonical package identity.
public class TrackerThemeSeedTests
{
    private static string ThemesDir => Path.Combine(ContractsPaths.FixturesDir, "themes");

    private static JsonElement[] SeedRows()
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ThemesDir, "seed.json")));
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    [Fact]
    public void Static_table_equals_the_seed_fixture_field_for_field()
    {
        var rows = SeedRows();
        Assert.Equal(rows.Length, TrackerThemeSeed.Themes.Count);
        for (var i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            var theme = TrackerThemeSeed.Themes[i];
            Assert.Equal(
                new[] { "chrome", "defaultDarkMode", "defaultLightMode", "key", "name", "overlay", "renderer", "sortOrder", "styleBytes", "styleSha256" },
                row.EnumerateObject().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
            Assert.Equal(row.GetProperty("renderer").GetString(), theme.Renderer);
            Assert.Equal(row.GetProperty("key").GetString(), theme.Key);
            Assert.Equal(row.GetProperty("name").GetString(), theme.Name);
            Assert.Equal(row.GetProperty("sortOrder").GetInt32(), theme.SortOrder);
            Assert.Equal(row.GetProperty("styleSha256").GetString(), theme.StyleSha256);
            Assert.Equal(row.GetProperty("styleBytes").GetInt32(), theme.StyleBytes);
            Assert.Equal(row.GetProperty("defaultLightMode").GetBoolean(), theme.DefaultLightMode);
            Assert.Equal(row.GetProperty("defaultDarkMode").GetBoolean(), theme.DefaultDarkMode);
            AssertSameJson(row.GetProperty("chrome"), theme.Chrome);
            AssertSameJson(row.GetProperty("overlay"), theme.Overlay);
        }
    }

    // Each fixture's bytes hash to its row. The six Google bodies are exactly
    // SerializeOpaqueToUtf8Bytes of themselves. route-light and route-dark
    // hold characters the default encoder escapes (the copyright sign, < and
    // >), and their files carry them unescaped, so for those two only the
    // key order and the parsed value are checked against the opaque form.
    [Fact]
    public void Each_style_fixture_hashes_to_its_row()
    {
        foreach (var theme in TrackerThemeSeed.Themes)
        {
            var file = File.ReadAllBytes(Path.Combine(ThemesDir, theme.Key + ".json"));
            Assert.Equal(theme.StyleSha256, CanonicalJson.Sha256Hex(file));
            Assert.Equal(theme.StyleBytes, file.Length);

            using var doc = JsonDocument.Parse(file);
            var opaque = CanonicalJson.SerializeOpaqueToUtf8Bytes(doc.RootElement);
            if (theme.Renderer == "google")
            {
                Assert.Equal(file, opaque);
                Assert.Equal(theme.StyleSha256, CanonicalJson.Sha256Hex(opaque));
            }
            else
            {
                using var reparsed = JsonDocument.Parse(opaque);
                Assert.True(JsonElement.DeepEquals(doc.RootElement, reparsed.RootElement));
                Assert.Equal(PropertyOrder(reparsed.RootElement), PropertyOrder(doc.RootElement));
            }
        }
    }

    // Every object's property names, depth first, in document order.
    private static List<string> PropertyOrder(JsonElement value)
    {
        var names = new List<string>();
        void Walk(JsonElement e)
        {
            if (e.ValueKind == JsonValueKind.Object)
                foreach (var p in e.EnumerateObject()) { names.Add(p.Name); Walk(p.Value); }
            else if (e.ValueKind == JsonValueKind.Array)
                foreach (var c in e.EnumerateArray()) Walk(c);
        }
        Walk(value);
        return names;
    }

    [Fact]
    public void Valley_package_key_is_the_hash_of_the_canonical_identity()
    {
        var bytes = CanonicalJson.SerializeToUtf8Bytes(TrackerThemeSeed.ValleyIdentity);
        Assert.Equal(
            "{\"bbox\":{\"west\":-114.75,\"south\":46.35,\"east\":-113.3,\"north\":47.25},\"minZoom\":0,\"maxZoom\":15,\"terrainMaxZoom\":13}",
            System.Text.Encoding.UTF8.GetString(bytes));
        Assert.Equal(TrackerThemeSeed.ValleyPackageKey, CanonicalJson.Sha256Hex(bytes));
        Assert.Equal(TrackerThemeSeed.ValleyPackageKey, TrackerThemeSeed.PackageKeyOf(TrackerThemeSeed.ValleyIdentity));
    }

    [Fact]
    public void Valley_box_json_holds_the_valley_constants()
    {
        using var doc = JsonDocument.Parse(TrackerThemeSeed.ValleyBboxJson);
        var box = doc.RootElement;
        Assert.Equal(TrackerThemeSeed.ValleyWest, box.GetProperty("west").GetDouble());
        Assert.Equal(TrackerThemeSeed.ValleySouth, box.GetProperty("south").GetDouble());
        Assert.Equal(TrackerThemeSeed.ValleyEast, box.GetProperty("east").GetDouble());
        Assert.Equal(TrackerThemeSeed.ValleyNorth, box.GetProperty("north").GetDouble());
    }

    private static void AssertSameJson(JsonElement expected, string actual)
    {
        using var doc = JsonDocument.Parse(actual);
        Assert.Equal(
            CanonicalJson.SerializeOpaqueToUtf8Bytes(expected),
            CanonicalJson.SerializeOpaqueToUtf8Bytes(doc.RootElement));
    }
}
