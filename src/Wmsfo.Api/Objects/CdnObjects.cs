using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wmsfo.Api.Objects;

// Live object contracts 1.2. Key order matches the field table exactly.
public sealed class LiveObject
{
    [JsonPropertyOrder(0)]  public int SchemaVersion { get; set; } = 1;
    [JsonPropertyOrder(1)]  public long? EventId { get; set; }
    [JsonPropertyOrder(2)]  public int? EventStatusId { get; set; }
    [JsonPropertyOrder(3)]  public int PollIntervalMs { get; set; }
    [JsonPropertyOrder(4)]  public bool HubEnabled { get; set; } = true;
    [JsonPropertyOrder(5)]  public string SnapshotUrl { get; set; } = "";
    // Keys emitted in ascending numeric order (contracts 1.2). Values are counts.
    // SortedDictionary<long,int> enumerates in numeric key order; STJ writes long keys as decimal strings.
    [JsonPropertyOrder(6)]  public SortedDictionary<long, int> CookieTally { get; set; } = new();
    [JsonPropertyOrder(7)]  public long? Seq { get; set; }
    [JsonPropertyOrder(8)]  public double? Lat { get; set; }
    [JsonPropertyOrder(9)]  public double? Lng { get; set; }
    [JsonPropertyOrder(10)] public double? SpeedMps { get; set; }
    [JsonPropertyOrder(11)] public double? AltitudeM { get; set; }
    [JsonPropertyOrder(12)] public double? HeadingDeg { get; set; }
    [JsonPropertyOrder(13)] public double? AccuracyM { get; set; }
    [JsonPropertyOrder(14)] public DateTimeOffset? RecordedAt { get; set; }
    [JsonPropertyOrder(15)] public DateTimeOffset? ReceivedAt { get; set; }
    [JsonPropertyOrder(16)] public DateTimeOffset PublishedAt { get; set; }
}

// Snapshot contracts 1.3. `qrCodes` is a top-level property (contracts 1.3, 4.5a);
// the doc row lists it interspersed with event fields but the key is not
// prefixed with `event.`, so it sits at the snapshot's root, right after the
// event object.
public sealed class Snapshot
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyOrder(1)] public SnapshotEvent? Event { get; set; }
    // Every active printed code already resolved (contracts 4.5a). Keys are QR
    // tags in ascending string order; absent tags open the home page.
    [JsonPropertyOrder(2)] public SortedDictionary<string, SnapshotQrCode> QrCodes { get; set; } = new(StringComparer.Ordinal);
    [JsonPropertyOrder(3)] public IList<SnapshotSponsor> Sponsors { get; set; } = new List<SnapshotSponsor>();
    [JsonPropertyOrder(4)] public IList<SnapshotCookieType> CookieTypes { get; set; } = new List<SnapshotCookieType>();
    // The published content document (contracts 1.3a) verbatim.
    [JsonPropertyOrder(5)] public ContentDocument Content { get; set; } = new();
    // Keys in ascending string order (contracts 1.3).
    [JsonPropertyOrder(6)] public SortedDictionary<string, MediaEntry> Media { get; set; } = new(StringComparer.Ordinal);
    // Keys in ascending string order (contracts 1.3).
    [JsonPropertyOrder(7)] public SortedDictionary<string, string> Icons { get; set; } = new(StringComparer.Ordinal);
}

// One resolved QR code inside `qrCodes` (contracts 1.3, 4.5a).
public sealed class SnapshotQrCode
{
    [JsonPropertyOrder(0)] public string? PageSlug { get; set; }
    [JsonPropertyOrder(1)] public string? ForwardUrl { get; set; }
}

public sealed class SnapshotEvent
{
    [JsonPropertyOrder(0)] public long Id { get; set; }
    [JsonPropertyOrder(1)] public int Year { get; set; }
    [JsonPropertyOrder(2)] public string Name { get; set; } = "";
    [JsonPropertyOrder(3)] public int StatusId { get; set; }
    [JsonPropertyOrder(4)] public DateTimeOffset? ScheduledAt { get; set; }
    [JsonPropertyOrder(5)] public DateTimeOffset? WentLiveAt { get; set; }
    [JsonPropertyOrder(6)] public DateTimeOffset? EndedAt { get; set; }
    [JsonPropertyOrder(7)] public int FundsPercent { get; set; }
    [JsonPropertyOrder(8)] public string? RouteImageMediaId { get; set; }
    [JsonPropertyOrder(9)] public SnapshotFlightHistory? FlightHistory { get; set; }
    [JsonPropertyOrder(10)] public SnapshotLatestMessage? LatestMessage { get; set; }
}

public sealed class SnapshotFlightHistory
{
    [JsonPropertyOrder(0)] public long RouteId { get; set; }
    [JsonPropertyOrder(1)] public string Name { get; set; } = "";
    [JsonPropertyOrder(2)] public IList<SnapshotFlightPoint> Points { get; set; } = new List<SnapshotFlightPoint>();
}

public sealed class SnapshotFlightPoint
{
    [JsonPropertyOrder(0)] public double Lat { get; set; }
    [JsonPropertyOrder(1)] public double Lng { get; set; }
    [JsonPropertyOrder(2)] public DateTimeOffset? RecordedAt { get; set; }
}

public sealed class SnapshotLatestMessage
{
    [JsonPropertyOrder(0)] public long Id { get; set; }
    [JsonPropertyOrder(1)] public string Body { get; set; } = "";
    [JsonPropertyOrder(2)] public DateTimeOffset? EventTime { get; set; }
    [JsonPropertyOrder(3)] public DateTimeOffset CreatedAt { get; set; }
}

public sealed class SnapshotSponsor
{
    [JsonPropertyOrder(0)] public long Id { get; set; }
    [JsonPropertyOrder(1)] public string Name { get; set; } = "";
    [JsonPropertyOrder(2)] public string? WebsiteUrl { get; set; }
    [JsonPropertyOrder(3)] public string? FbUrl { get; set; }
    [JsonPropertyOrder(4)] public string? IgUrl { get; set; }
    [JsonPropertyOrder(5)] public string? LogoMediaId { get; set; }
    [JsonPropertyOrder(6)] public int LatestYear { get; set; }
    [JsonPropertyOrder(7)] public int YearsAsSponsor { get; set; }
    [JsonPropertyOrder(8)] public int LingerMs { get; set; }
}

public sealed class SnapshotCookieType
{
    [JsonPropertyOrder(0)] public long Id { get; set; }
    [JsonPropertyOrder(1)] public string Name { get; set; } = "";
    [JsonPropertyOrder(2)] public IconValue? Icon { get; set; }
    [JsonPropertyOrder(3)] public int Sort { get; set; }
}

// Icon primitive (contracts 1.3a). Discriminator by source ("library" | "media").
// `display` is an optional Display object kept as written; absent when null.
public sealed class IconValue
{
    [JsonPropertyOrder(0)] public string Source { get; set; } = "";
    [JsonPropertyOrder(1)] public string Id { get; set; } = "";
    [JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonElement? Display { get; set; }

    // Reads a stored icon (`{ source, id, display? }`); null when the value is not an icon.
    public static IconValue? FromStored(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        var source = el.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() ?? "" : "";
        var id = el.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(id)) return null;
        var icon = new IconValue { Source = source, Id = id };
        if (el.TryGetProperty("display", out var d) && d.ValueKind == JsonValueKind.Object)
            icon.Display = d.Clone();
        return icon;
    }
}

// Display primitive (contracts 1.3a, `$defs/Display` in primitives.schema.json):
// a bounded display setting on an Icon or a MediaRef. Every key is optional and
// unknown keys are refused. Content is checked by the schema; this check serves
// the typed paths that do not run the content schemas.
public static class DisplayRules
{
    private static readonly Dictionary<string, string[]> Enums = new(StringComparer.Ordinal)
    {
        ["fit"] = new[] { "contain", "cover" },
        ["shape"] = new[] { "none", "circle", "rounded", "square" },
        ["background"] = new[] { "none", "surface", "muted", "accent", "night" },
        ["align"] = new[] { "start", "center", "end" },
    };

    private static readonly Dictionary<string, (int Min, int Max)> Integers = new(StringComparer.Ordinal)
    {
        ["sizePx"] = (12, 600),
        ["paddingPx"] = (0, 48),
    };

    // Field problems as (key, message) pairs; empty when the value is null or a valid Display.
    public static IEnumerable<(string Key, string Message)> Problems(JsonElement? display)
    {
        if (display is not JsonElement el || el.ValueKind == JsonValueKind.Null) yield break;
        if (el.ValueKind != JsonValueKind.Object)
        {
            yield return ("", "must be an object or null");
            yield break;
        }
        foreach (var prop in el.EnumerateObject())
        {
            if (Enums.TryGetValue(prop.Name, out var allowed))
            {
                if (prop.Value.ValueKind != JsonValueKind.String || Array.IndexOf(allowed, prop.Value.GetString()) < 0)
                    yield return (prop.Name, "must be one of " + string.Join(", ", allowed));
            }
            else if (Integers.TryGetValue(prop.Name, out var range))
            {
                if (prop.Value.ValueKind != JsonValueKind.Number || !prop.Value.TryGetInt32(out var n)
                    || n < range.Min || n > range.Max)
                    yield return (prop.Name, $"must be an integer from {range.Min} to {range.Max}");
            }
            else if (prop.Name == "shadow")
            {
                if (prop.Value.ValueKind != JsonValueKind.True && prop.Value.ValueKind != JsonValueKind.False)
                    yield return (prop.Name, "must be a boolean");
            }
            else
            {
                yield return (prop.Name, "is not a display setting");
            }
        }
    }
}

// MediaEntry (contracts 1.3b).
public sealed class MediaEntry
{
    [JsonPropertyOrder(0)] public string Url { get; set; } = "";
    [JsonPropertyOrder(1)] public string Kind { get; set; } = "";
    [JsonPropertyOrder(2)] public int? Width { get; set; }
    [JsonPropertyOrder(3)] public int? Height { get; set; }
    [JsonPropertyOrder(4)] public string Alt { get; set; } = "";
    // {} for svg and gif; deterministic (empty or fixed keys) for raster.
    [JsonPropertyOrder(5)] public SortedDictionary<string, string> Variants { get; set; } = new(StringComparer.Ordinal);
    // Absolute CDN URL of the Deep Zoom descriptor when a tile pyramid exists
    // (contracts 1.3b); null on smaller rasters, svg, and gif.
    [JsonPropertyOrder(6)] public string? Dzi { get; set; }
    // The dark mode version the site draws in this asset's place in dark
    // mode, or null when the asset has none.
    [JsonPropertyOrder(7)] public MediaDarkEntry? Dark { get; set; }
    // When true the site inverts the asset's colors in dark mode.
    [JsonPropertyOrder(8)] public bool InvertInDark { get; set; }
}

// MediaEntry.dark (contracts 1.3b): the dark version's own url and variants,
// the same shape rules as the entry's.
public sealed class MediaDarkEntry
{
    [JsonPropertyOrder(0)] public string Url { get; set; } = "";
    [JsonPropertyOrder(1)] public SortedDictionary<string, string> Variants { get; set; } = new(StringComparer.Ordinal);
}

// Route object contracts 1.4.
public sealed class RouteObject
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyOrder(1)] public string Name { get; set; } = "";
    [JsonPropertyOrder(2)] public IList<RoutePoint> Points { get; set; } = new List<RoutePoint>();
}

public sealed class RoutePoint
{
    [JsonPropertyOrder(0)] public double Lat { get; set; }
    [JsonPropertyOrder(1)] public double Lng { get; set; }
    [JsonPropertyOrder(2)] public DateTimeOffset? RecordedAt { get; set; }
}
