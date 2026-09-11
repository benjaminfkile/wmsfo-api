using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts;

// Fixed DTO instances used to serialize the canonical fixture files (contracts 13).
// The values match the examples in contracts 1.2, 1.3, 1.4 with concrete 64-hex keys
// and `https://cdn.example` as the CDN base.
public static class FixtureData
{
    public const string CdnBase = "https://cdn.example";
    public const string SnapshotSha = "3f9a1c8c1d5e2a7b3f4c9e9a1d2f6e8b4c0a113b7e9c102d4f4a8bb6c19e0b2e1";
    public const string RouteSha    = "9c0e5b7d3f4a2c1e8b6a9d0f5e2c4b7a1d8f3c9e6b4a7d2f5c8e1b0a9f4d77ab";
    public const string CookieIconSha    = "bb2200000000000000000000000000000000000000000000000000000000ffff";
    public const string CandyCaneIconSha = "aa1100000000000000000000000000000000000000000000000000000000ffff";
    public const string MediaAssetIdRaster = "8c1d5e2a-7b3f-4c9e-9a1d-2f6e8b4c0a11";
    public const string RoutePosterMediaId = "5f2a7c9e-1b4d-4e8a-9c3f-7d6e2a1b0c44";

    public static string SnapshotUrl => $"{CdnBase}/snapshots/{SnapshotSha}.json";
    public static string RouteUrl    => $"{CdnBase}/routes/{RouteSha}.json";
    public static string CookieIconUrl    => $"{CdnBase}/icons/{CookieIconSha}.svg";
    public static string CandyCaneIconUrl => $"{CdnBase}/icons/{CandyCaneIconSha}.svg";

    public static LiveObject BuildLiveObject() => new()
    {
        SchemaVersion = 1,
        EventId = 7,
        EventStatusId = 3,
        PollIntervalMs = 5000,
        SnapshotUrl = SnapshotUrl,
        CookieTally = new SortedDictionary<long, int> { { 1, 412 }, { 3, 90 } },
        Seq = 1832,
        Lat = 46.87,
        Lng = -114.0,
        SpeedMps = 31.2,
        AltitudeM = 1210,
        HeadingDeg = 84,
        AccuracyM = 6,
        RecordedAt = DateTimeOffset.Parse("2026-12-22T01:31:07.000Z"),
        ReceivedAt = DateTimeOffset.Parse("2026-12-22T01:31:07.412Z"),
        PublishedAt = DateTimeOffset.Parse("2026-12-22T01:31:07.430Z"),
    };

    public static ContentDocument BuildContentDocument() => StarterContentBuilder.Build();

    public static Snapshot BuildSnapshot()
    {
        var content = BuildContentDocument();

        var media = new SortedDictionary<string, MediaEntry>(StringComparer.Ordinal)
        {
            [RoutePosterMediaId] = new MediaEntry
            {
                Url = $"{CdnBase}/media/{RoutePosterMediaId}/route-2026.jpg",
                Kind = "raster",
                Width = 1600,
                Height = 2400,
                Alt = "The 2026 route poster",
                Variants = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["1600"] = $"{CdnBase}/media/{RoutePosterMediaId}/w1600.webp",
                    ["480"]  = $"{CdnBase}/media/{RoutePosterMediaId}/w480.webp",
                    ["960"]  = $"{CdnBase}/media/{RoutePosterMediaId}/w960.webp",
                },
            },
            [MediaAssetIdRaster] = new MediaEntry
            {
                Url = $"{CdnBase}/media/{MediaAssetIdRaster}/hangar.jpg",
                Kind = "raster",
                Width = 2400,
                Height = 1600,
                Alt = "The helicopter in its hangar",
                Variants = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["1600"] = $"{CdnBase}/media/{MediaAssetIdRaster}/w1600.webp",
                    ["480"]  = $"{CdnBase}/media/{MediaAssetIdRaster}/w480.webp",
                    ["960"]  = $"{CdnBase}/media/{MediaAssetIdRaster}/w960.webp",
                },
            },
        };

        var icons = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["candy-cane"] = CandyCaneIconUrl,
            ["cookie"]     = CookieIconUrl,
        };

        return new Snapshot
        {
            SchemaVersion = 1,
            Event = new SnapshotEvent
            {
                Id = 7,
                Year = 2026,
                Name = "Santa Flyover 2026",
                StatusId = 3,
                ScheduledAt = DateTimeOffset.Parse("2026-12-22T01:00:00.000Z"),
                WentLiveAt  = DateTimeOffset.Parse("2026-12-22T01:02:11.000Z"),
                EndedAt     = null,
                FundsPercent = 63,
                RouteImageMediaId = RoutePosterMediaId,
                FlightHistory = new SnapshotFlightHistory
                {
                    RouteId = 3,
                    Name = "2025 flight",
                    Points = new List<SnapshotFlightPoint>
                    {
                        new() { Lat = 46.8721, Lng = -114.0012, RecordedAt = DateTimeOffset.Parse("2025-12-22T01:31:07.000Z") },
                        new() { Lat = 46.8730, Lng = -114.0030, RecordedAt = DateTimeOffset.Parse("2025-12-22T01:31:37.000Z") },
                    },
                },
                LatestMessage = new SnapshotLatestMessage
                {
                    Id = 12,
                    Body = "Santa is airborne over the valley.",
                    EventTime = DateTimeOffset.Parse("2026-12-22T01:02:00.000Z"),
                    CreatedAt = DateTimeOffset.Parse("2026-12-22T01:02:30.000Z"),
                },
            },
            Sponsors = new List<SnapshotSponsor>
            {
                new()
                {
                    Id = 4,
                    Name = "Example Bakery",
                    WebsiteUrl = "https://example.com",
                    FbUrl = null,
                    IgUrl = "https://instagram.com/example",
                    LogoMediaId = MediaAssetIdRaster,
                    LatestYear = 2026,
                    YearsAsSponsor = 3,
                    LingerMs = 12000,
                },
            },
            CookieTypes = new List<SnapshotCookieType>
            {
                new() { Id = 1, Name = "Chocolate chip", Icon = new IconValue { Source = "library", Id = "cookie" }, Sort = 10 },
                new() { Id = 3, Name = "Gingerbread",    Icon = new IconValue { Source = "library", Id = "candy-cane" }, Sort = 20 },
            },
            Content = content,
            Media = media,
            Icons = icons,
        };
    }

    public static RouteObject BuildRoute() => new()
    {
        SchemaVersion = 1,
        Name = "2025 flight",
        Points = new List<RoutePoint>
        {
            new() { Lat = 46.8721, Lng = -114.0012, RecordedAt = DateTimeOffset.Parse("2025-12-22T01:31:07.000Z") },
            new() { Lat = 46.8730, Lng = -114.0030, RecordedAt = null },
        },
    };

    public static LocationBody BuildLocation() => new()
    {
        Lat = 46.87,
        Lng = -114.0,
        RecordedAt = DateTimeOffset.Parse("2026-12-22T01:31:07.000Z"),
        SpeedMps = 31.2,
        AltitudeM = 1210,
        HeadingDeg = 84,
        AccuracyM = 6,
    };

    public static HeartbeatBody BuildHeartbeat() => new()
    {
        SentAt = DateTimeOffset.Parse("2026-12-22T01:31:07.000Z"),
        Power = new HeartbeatPower { BatteryPercent = 87, Charging = true, BatteryTempC = 31.5, ThermalStatus = "none" },
        Radio = new HeartbeatRadio { NetworkType = "LTE", SignalDbm = -95, SignalLevel = 3, AirplaneMode = false, Connected = true },
        Gps = new HeartbeatGps
        {
            Provider = "fused",
            SatellitesUsed = 9,
            SatellitesInView = 14,
            LastFixAccuracyM = 6,
            LastFixAgeS = 1,
            FixesLastMinute = 58,
            Permission = new HeartbeatGpsPermission { Foreground = true, Background = true, Precise = true },
        },
        Transport = new HeartbeatTransport
        {
            SocketState = "connected", ReconnectCount = 2, HttpFallbackSeconds = 0,
            LastReceiptLatencyMs = 120, SendsFailedSinceBoot = 3,
        },
        Process = new HeartbeatProcess
        {
            DeviceUptimeS = 90000, ServiceUptimeS = 3000, ServiceRestartCount = 1, MemoryPressure = "normal",
            BatteryOptimizationExempt = true, NotificationPermission = true, SystemApp = true, RootAvailable = true,
        },
        Identity = new HeartbeatIdentity
        {
            DeviceModel = "Pixel 6a", AndroidVersion = "14", AppVersion = "1.0.3", ClockSkewMs = -120,
        },
    };
}
