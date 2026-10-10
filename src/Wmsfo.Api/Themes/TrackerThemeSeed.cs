using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Themes;

// The seeded tracker rows of sql.md 6: the eight themes of
// contracts/fixtures/themes/seed.json in file order, and the Missoula valley
// map whose tiles the operator placed under `basemap`. The A92TrackerMapsThemes
// migration inserts these values; a unit test holds them equal to the fixture.
public static class TrackerThemeSeed
{
    public sealed record ThemeRow(
        string Renderer,
        string Key,
        string Name,
        int SortOrder,
        string StyleSha256,
        int StyleBytes,
        string Chrome,    // canonical JSON object
        string Overlay,   // canonical JSON object
        bool DefaultLightMode,
        bool DefaultDarkMode);

    public static readonly IReadOnlyList<ThemeRow> Themes =
    [
        new("maplibre", "light", "Light", 10,
            "aa712987ac12773666971239fd2dac5bccb269df30c2bf524c2dd86347cbda1d", 63798,
            """{"accent":"#1a56c4","bg":"#ffffff","fg":"#5f6368","panel":"#ffffffe6","text":"#202124","tile":"#e8f0fe","tileFg":"#1a56c4"}""",
            """{"arrowColor":"#1a56c4","routeColor":"#1a56c4","routeOpacity":0.9,"timeLabelBg":"#ffffff","timeLabelFg":"#202124","timeLabelOpacity":1,"userColor":"#c62828"}""",
            DefaultLightMode: true, DefaultDarkMode: false),
        new("maplibre", "dark", "Dark", 20,
            "a49464e361dbedd95861d2f52c8b45fb0f8f5b437d807c374f614462fa795386", 63797,
            """{"accent":"#33d6ff","bg":"#0f1a2b","fg":"#8fa3c2","panel":"#0b1220e6","text":"#f2f6ff","tile":"#1e2b40","tileFg":"#f2f6ff"}""",
            """{"arrowColor":"#33d6ff","routeColor":"#33d6ff","routeOpacity":0.85,"timeLabelBg":"#0f1a2b","timeLabelFg":"#f2f6ff","timeLabelOpacity":1,"userColor":"#ffb74d"}""",
            DefaultLightMode: false, DefaultDarkMode: true),
        new("google", "standard", "Standard", 10,
            "7cf1ed24d4964827d29f251a64b726e2ce5f3b4ba6213d9f50eb668936576b98", 286,
            """{"accent":"#1a56c4","bg":"#ffffff","fg":"#5f6368","panel":"#ffffffe6","text":"#202124","tile":"#e8f0fe","tileFg":"#1a56c4"}""",
            """{"arrowColor":"#ffffff","routeColor":"#1a56c4","routeOpacity":0.9,"timeLabelBg":"#1c1c1e","timeLabelFg":"#ffffff","timeLabelOpacity":0.8,"userColor":"#c62828"}""",
            DefaultLightMode: true, DefaultDarkMode: false),
        new("google", "expedition", "Expedition", 20,
            "418f6ee97ab765a7d1b20f2bc10a3c1f65693c84d04e99ce365cf45c5b5fb0ac", 2450,
            """{"accent":"#8a4a1a","bg":"#f5f1e6","fg":"#6b5e3f","panel":"#f5f1e6e6","text":"#2c2416","tile":"#e2d5b0","tileFg":"#2c2416"}""",
            """{"arrowColor":"#fff8e6","routeColor":"#b3401a","routeOpacity":0.9,"timeLabelBg":"#3b2f1b","timeLabelFg":"#fff8e6","timeLabelOpacity":0.85,"userColor":"#1f5e3a"}""",
            DefaultLightMode: false, DefaultDarkMode: false),
        new("google", "blizzard", "Blizzard", 30,
            "5758c71a826fa1323612721ccd60b8cceca5a23fbabfd8a4a46d41aec9e7aecd", 1799,
            """{"accent":"#0060b8","bg":"#f7f7f7","fg":"#5c5c5c","panel":"#f7f7f7e6","text":"#1f1f1f","tile":"#dfe6ee","tileFg":"#1f3a5f"}""",
            """{"arrowColor":"#ffffff","routeColor":"#0060b8","routeOpacity":0.9,"timeLabelBg":"#2b2b2b","timeLabelFg":"#ffffff","timeLabelOpacity":0.8,"userColor":"#b0164a"}""",
            DefaultLightMode: false, DefaultDarkMode: false),
        new("google", "charcoal", "Charcoal", 40,
            "41e67d305b612ef9c062a64b4cc6d066cb9a1cc7dd33d73fb7427f65c13b4f18", 2021,
            """{"accent":"#ffb300","bg":"#2a2a2a","fg":"#a0a0a0","panel":"#1c1c1ce6","text":"#f0f0f0","tile":"#3a3a3a","tileFg":"#ffffff"}""",
            """{"arrowColor":"#ffffff","routeColor":"#ffb300","routeOpacity":0.9,"timeLabelBg":"#000000","timeLabelFg":"#ffffff","timeLabelOpacity":0.8,"userColor":"#4fc3f7"}""",
            DefaultLightMode: false, DefaultDarkMode: false),
        new("google", "night", "Night", 50,
            "9597d6f2c73babf7c905981e2c2842f5688c7e6bd448bb16646986d10058f5ed", 1828,
            """{"accent":"#33d6ff","bg":"#0f1a2b","fg":"#8fa3c2","panel":"#0b1220e6","text":"#f2f6ff","tile":"#1e2b40","tileFg":"#f2f6ff"}""",
            """{"arrowColor":"#ffffff","routeColor":"#33d6ff","routeOpacity":0.85,"timeLabelBg":"#0b1220","timeLabelFg":"#ffffff","timeLabelOpacity":0.8,"userColor":"#ffb74d"}""",
            DefaultLightMode: false, DefaultDarkMode: true),
        new("google", "nebula", "Nebula", 60,
            "f2f6d9066ddbdb3b9d8c738f1f13e6f3ed4c9fc765a7ab30f90572f932f85c0f", 2605,
            """{"accent":"#ffd166","bg":"#1d2c4d","fg":"#98a5be","panel":"#141f36e6","text":"#f0f4ff","tile":"#2a3d63","tileFg":"#f0f4ff"}""",
            """{"arrowColor":"#ffffff","routeColor":"#ffd166","routeOpacity":0.9,"timeLabelBg":"#0e1626","timeLabelFg":"#ffffff","timeLabelOpacity":0.8,"userColor":"#ff7eb6"}""",
            DefaultLightMode: false, DefaultDarkMode: false),
    ];

    // The tile package identity hashed into package_key (contracts 1.1).
    public sealed record PackageBbox(double West, double South, double East, double North);
    public sealed record PackageIdentity(PackageBbox Bbox, int MinZoom, int MaxZoom, int? TerrainMaxZoom);

    public const string ValleyName = "Missoula valley";
    public const string ValleyPrefix = "basemap";
    public const double ValleyWest = -114.75;
    public const double ValleySouth = 46.35;
    public const double ValleyEast = -113.30;
    public const double ValleyNorth = 47.25;
    public const int ValleyMinZoom = 0;
    public const int ValleyMaxZoom = 15;
    public const int ValleyTerrainMaxZoom = 13;

    // The box as the event.tracker_bbox column default writes it.
    public const string ValleyBboxJson = """{"west":-114.75,"south":46.35,"east":-113.30,"north":47.25}""";

    // Lowercase hex SHA-256 of the canonical JSON of ValleyIdentity.
    public const string ValleyPackageKey = "660ecae3f635b40d8b88525e15012ec4992db199774c055539abe7a541cbd4ec";

    public static PackageIdentity ValleyIdentity =>
        new(new PackageBbox(ValleyWest, ValleySouth, ValleyEast, ValleyNorth),
            ValleyMinZoom, ValleyMaxZoom, ValleyTerrainMaxZoom);

    public static string PackageKeyOf(PackageIdentity identity) =>
        CanonicalJson.Sha256Hex(CanonicalJson.SerializeToUtf8Bytes(identity));
}
