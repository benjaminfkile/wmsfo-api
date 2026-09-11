using System.Text.Json.Serialization;

namespace Wmsfo.Api.Objects;

// Live object contracts 1.2. Key order matches the field table exactly.
public sealed class LiveObject
{
    [JsonPropertyOrder(0)]  public int SchemaVersion { get; set; } = 1;
    [JsonPropertyOrder(1)]  public long? EventId { get; set; }
    [JsonPropertyOrder(2)]  public int? EventStatusId { get; set; }
    [JsonPropertyOrder(3)]  public int PollIntervalMs { get; set; }
    [JsonPropertyOrder(4)]  public string SnapshotUrl { get; set; } = "";
    // Keys emitted in ascending numeric order (contracts 1.2). Values are counts.
    // SortedDictionary<long,int> enumerates in numeric key order; STJ writes long keys as decimal strings.
    [JsonPropertyOrder(5)]  public SortedDictionary<long, int> CookieTally { get; set; } = new();
    [JsonPropertyOrder(6)]  public long? Seq { get; set; }
    [JsonPropertyOrder(7)]  public double? Lat { get; set; }
    [JsonPropertyOrder(8)]  public double? Lng { get; set; }
    [JsonPropertyOrder(9)]  public double? SpeedMps { get; set; }
    [JsonPropertyOrder(10)] public double? AltitudeM { get; set; }
    [JsonPropertyOrder(11)] public double? HeadingDeg { get; set; }
    [JsonPropertyOrder(12)] public double? AccuracyM { get; set; }
    [JsonPropertyOrder(13)] public DateTimeOffset? RecordedAt { get; set; }
    [JsonPropertyOrder(14)] public DateTimeOffset? ReceivedAt { get; set; }
    [JsonPropertyOrder(15)] public DateTimeOffset PublishedAt { get; set; }
}

// Snapshot contracts 1.3.
public sealed class Snapshot
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyOrder(1)] public SnapshotEvent? Event { get; set; }
    [JsonPropertyOrder(2)] public IList<SnapshotSponsor> Sponsors { get; set; } = new List<SnapshotSponsor>();
    [JsonPropertyOrder(3)] public IList<SnapshotCookieType> CookieTypes { get; set; } = new List<SnapshotCookieType>();
    // The published content document (contracts 1.3a) verbatim.
    [JsonPropertyOrder(4)] public ContentDocument Content { get; set; } = new();
    // Keys in ascending string order (contracts 1.3).
    [JsonPropertyOrder(5)] public SortedDictionary<string, MediaEntry> Media { get; set; } = new(StringComparer.Ordinal);
    // Keys in ascending string order (contracts 1.3).
    [JsonPropertyOrder(6)] public SortedDictionary<string, string> Icons { get; set; } = new(StringComparer.Ordinal);
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
public sealed class IconValue
{
    [JsonPropertyOrder(0)] public string Source { get; set; } = "";
    [JsonPropertyOrder(1)] public string Id { get; set; } = "";
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
