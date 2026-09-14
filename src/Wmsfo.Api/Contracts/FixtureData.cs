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
    public const string HangarMediaId      = "8c1d5e2a-7b3f-4c9e-9a1d-2f6e8b4c0a11";
    public const string GingerbreadMediaId = "3b7e9c10-2d4f-4a8b-b6c1-9e0f7d5a2c33";
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
                Dzi = $"{CdnBase}/media/{RoutePosterMediaId}/dzi/poster.dzi",
            },
            [HangarMediaId] = new MediaEntry
            {
                Url = $"{CdnBase}/media/{HangarMediaId}/hangar.jpg",
                Kind = "raster",
                Width = 2400,
                Height = 1600,
                Alt = "The helicopter in its hangar",
                Variants = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["1600"] = $"{CdnBase}/media/{HangarMediaId}/w1600.webp",
                    ["480"]  = $"{CdnBase}/media/{HangarMediaId}/w480.webp",
                    ["960"]  = $"{CdnBase}/media/{HangarMediaId}/w960.webp",
                },
                Dzi = $"{CdnBase}/media/{HangarMediaId}/dzi/poster.dzi",
            },
            [GingerbreadMediaId] = new MediaEntry
            {
                Url = $"{CdnBase}/media/{GingerbreadMediaId}/gingerbread.svg",
                Kind = "svg",
                Width = null,
                Height = null,
                Alt = "Gingerbread cookie",
                Variants = new SortedDictionary<string, string>(StringComparer.Ordinal),
                Dzi = null,
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
                    LogoMediaId = HangarMediaId,
                    LatestYear = 2026,
                    YearsAsSponsor = 3,
                    LingerMs = 12000,
                },
            },
            CookieTypes = new List<SnapshotCookieType>
            {
                new() { Id = 1, Name = "Chocolate chip", Icon = new IconValue { Source = "library", Id = "cookie" }, Sort = 10 },
                new() { Id = 3, Name = "Gingerbread",    Icon = new IconValue { Source = "media", Id = GingerbreadMediaId }, Sort = 20 },
            },
            Content = content,
            Media = media,
            Icons = icons,
            QrCodes = new SortedDictionary<string, SnapshotQrCode>(StringComparer.Ordinal)
            {
                // The example code resolves through an ancestor place that opens
                // the sponsors page; the snapshot carries the resolved slug.
                ["qr-001"] = new SnapshotQrCode { PageSlug = "sponsors", ForwardUrl = null },
                // A home-resolving code (contracts 1.3): pageSlug "/" for both
                // the home role page and codes with no code- or place-set opens.
                ["qr-002"] = new SnapshotQrCode { PageSlug = "/", ForwardUrl = null },
            },
        };
    }

    public static QrCodeDto BuildQrCode() => new()
    {
        Id = 1,
        Tag = "qr-001",
        BatchNo = 1,
        PrintedAt = DateTimeOffset.Parse("2026-12-20T18:00:00.000Z"),
        Active = true,
        Note = "",
        OpensPageId = null,
        ForwardUrl = null,
        Opens = new Contracts.Dtos.OpensDto { Kind = "page", PageId = 4, Slug = "sponsors" },
        OpensSource = "place",
        Attachment = new Contracts.Dtos.QrAttachmentSummaryDto
        {
            Id = 3,
            PlaceId = 5,
            PlacePath = new List<string> { "Southgate Mall", "West wing" },
            Since = DateTimeOffset.Parse("2026-12-20T20:00:00.000Z"),
        },
        Scans = new Contracts.Dtos.QrScanSummaryDto
        {
            People = 42,
            Flagged = 3,
            LastScanAt = DateTimeOffset.Parse("2026-12-22T01:00:00.000Z"),
        },
        CreatedBy = "canvasser@wmsfo.test",
        CreatedAt = DateTimeOffset.Parse("2026-12-20T18:00:00.000Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-12-20T20:00:00.000Z"),
        Audit = new Contracts.Dtos.AuditStampDto
        {
            Action = "attach",
            By = "person:canvasser@wmsfo.test",
            At = DateTimeOffset.Parse("2026-12-20T20:00:00.000Z"),
        },
    };

    public static QrCodeDetailDto BuildQrCodeDetail()
    {
        var basic = BuildQrCode();
        return new QrCodeDetailDto
        {
            Id = basic.Id,
            Tag = basic.Tag,
            BatchNo = basic.BatchNo,
            PrintedAt = basic.PrintedAt,
            Active = basic.Active,
            Note = basic.Note,
            OpensPageId = basic.OpensPageId,
            ForwardUrl = basic.ForwardUrl,
            Opens = basic.Opens,
            OpensSource = basic.OpensSource,
            Attachment = basic.Attachment,
            Scans = basic.Scans,
            CreatedBy = basic.CreatedBy,
            CreatedAt = basic.CreatedAt,
            UpdatedAt = basic.UpdatedAt,
            Audit = basic.Audit,
            History = new List<Contracts.Dtos.QrHistoryEntryDto>
            {
                new()
                {
                    AttachmentId = 3,
                    PlaceId = 5,
                    PlacePath = new List<string> { "Southgate Mall", "West wing" },
                    FromAt = DateTimeOffset.Parse("2026-12-20T20:00:00.000Z"),
                    ToAt = null,
                    People = 42,
                    EarlyScans = 2,
                },
            },
            Daily = new List<Contracts.Dtos.QrDailyDto>
            {
                new() { Day = "2026-12-20", People = 12 },
                new() { Day = "2026-12-21", People = 18 },
                new() { Day = "2026-12-22", People = 12 },
            },
        };
    }

    public static Contracts.Dtos.PlaceDto BuildPlace() => new()
    {
        Id = 5,
        ParentId = 2,
        Name = "West wing",
        Description = "Wing near the food court",
        Path = new List<string> { "Southgate Mall", "West wing" },
        OpensPageId = null,
        ForwardUrl = null,
        Opens = new Contracts.Dtos.OpensDto { Kind = "page", PageId = 4, Slug = "sponsors" },
        OpensSource = "ancestor",
        Location = new Contracts.Dtos.PlaceLocationDto
        {
            Lat = 46.916,
            Lng = -114.039,
            AccuracyM = 140,
            Source = "phone",
            PinnedBy = "canvasser@wmsfo.test",
            PinnedAt = DateTimeOffset.Parse("2026-12-20T19:00:00.000Z"),
        },
        Pin = new Contracts.Dtos.PlacePinResolvedDto { Lat = 46.916, Lng = -114.039, FromPlaceId = 5 },
        Codes = new List<Contracts.Dtos.PlaceCodeRefDto>
        {
            new() { Id = 1, Tag = "qr-001" },
        },
        Scans = new Contracts.Dtos.PlaceScansDto { People = 42 },
        CreatedBy = "canvasser@wmsfo.test",
        CreatedAt = DateTimeOffset.Parse("2026-12-20T18:00:00.000Z"),
        UpdatedAt = DateTimeOffset.Parse("2026-12-20T19:00:00.000Z"),
        Audit = new Contracts.Dtos.AuditStampDto
        {
            Action = "location",
            By = "person:canvasser@wmsfo.test",
            At = DateTimeOffset.Parse("2026-12-20T19:00:00.000Z"),
        },
    };

    // contracts 4.5 Delete impact: representative preview of the walk event
    // (its messages, cookies, status history, and one location) with no block
    // (the walk is not live). Warnings are empty; the panel would surface them
    // for a role page delete or a cookie type live-event drop.
    public static Contracts.Dtos.DeleteImpactDto BuildDeleteImpact() => new()
    {
        Blocked = null,
        Deletes = new List<Contracts.Dtos.ImpactGroupDto>
        {
            new()
            {
                Entity = "event_message", Count = 2,
                Names = new List<string> { "Santa is airborne over the valley.", "Watch the roof to the west." },
            },
            new() { Entity = "event_status_history", Count = 3, Names = new List<string>() },
            new() { Entity = "cookie", Count = 41, Names = new List<string>() },
            new() { Entity = "location", Count = 1832, Names = new List<string>() },
        },
        Unlinks = new List<Contracts.Dtos.ImpactGroupDto>(),
        Warnings = new List<string>(),
    };

    public static Contracts.Dtos.PlacePinDto BuildPlacePin() => new()
    {
        PlaceId = 5,
        Name = "West wing",
        Path = new List<string> { "Southgate Mall", "West wing" },
        Lat = 46.916,
        Lng = -114.039,
        People = 42,
        Codes = new List<Contracts.Dtos.PlacePinCodeRefDto>
        {
            new() { Tag = "qr-001", PlaceName = "West wing", People = 42 },
        },
    };

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

    public static HeartbeatBody BuildHeartbeat()
    {
        // contracts 4.2: sentAt + typed health + free-form debug. The debug
        // object here is representative Red-Nose telemetry as a JSON tree; the
        // API never interprets it.
        const string debugJson = """
{
  "power": { "charging": true, "batteryTempC": 31.5, "thermalStatus": "none" },
  "radio": { "networkType": "LTE", "signalDbm": -95, "signalLevel": 3, "airplaneMode": false, "connected": true },
  "gps": {
    "provider": "fused",
    "satellitesUsed": 9,
    "satellitesInView": 14,
    "lastFixAccuracyM": 6,
    "fixesLastMinute": 58,
    "permission": { "foreground": true, "background": true, "precise": true }
  },
  "transport": {
    "reconnectCount": 2,
    "httpFallbackSeconds": 0,
    "lastReceiptLatencyMs": 120,
    "sendsFailedSinceBoot": 3
  },
  "process": {
    "deviceUptimeS": 90000,
    "serviceUptimeS": 3000,
    "serviceRestartCount": 1,
    "memoryPressure": "normal",
    "batteryOptimizationExempt": true,
    "notificationPermission": true,
    "systemApp": true,
    "rootAvailable": true
  },
  "identity": {
    "deviceModel": "Pixel 6a",
    "androidVersion": "14",
    "appVersion": "1.0.3",
    "clockSkewMs": -120
  }
}
""";
        using var doc = System.Text.Json.JsonDocument.Parse(debugJson);
        return new HeartbeatBody
        {
            SentAt = DateTimeOffset.Parse("2026-12-22T01:31:07.000Z"),
            Health = new HeartbeatHealth
            {
                BatteryPercent = 87,
                LastFixAgeS = 1,
                SocketState = "connected",
            },
            Debug = doc.RootElement.Clone(),
        };
    }
}
