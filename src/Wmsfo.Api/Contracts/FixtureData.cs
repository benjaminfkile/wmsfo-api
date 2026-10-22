using System.Text.Json;
using System.Text.Json.Nodes;
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
    public const string HangarDarkMediaId  = "9d4b2e6f-3c8a-4f1d-8e7b-1a5c0f9d2b55";
    public const string FlyoverVideoMediaId  = "4e8c1a7d-5b2f-4d9e-8a3c-6f1b0d7e2a66";
    public const string FlyoverPosterMediaId = "6a2d9f4b-1e7c-4b3a-9d5e-0c8f2a6b1d77";
    public const string StandardStyleSha    = "7cf1ed24d4964827d29f251a64b726e2ce5f3b4ba6213d9f50eb668936576b98";
    public const string RouteLightStyleSha  = "587558ba6ff20a68d01cccca65d956b0260f74739cc534878c1fdd9230ca2999";
    public const string RouteLightSpriteSha = "ee5500000000000000000000000000000000000000000000000000000000ffff";

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
        OnlineCount = 214,
    };

    // The starter content with one block of each kind that takes an optional `align`
    // appended to the about page's rich text section, each with `align` set, so the
    // fixture exercises the field (contracts 1.3a).
    public static ContentDocument BuildContentDocument()
    {
        var doc = StarterContentBuilder.Build();
        var about = doc.Pages.Single(p => p.Slug == "about").Sections.First(s => s.Kind == "rich_text");
        var blocks = about.Data["blocks"]!.AsArray();
        blocks.Add(new JsonObject { ["kind"] = "heading", ["level"] = 2, ["text"] = "How it works", ["icon"] = null, ["align"] = "center" });
        blocks.Add(new JsonObject { ["kind"] = "paragraph", ["text"] = "The crew posts the route before the flight.", ["align"] = "start" });
        blocks.Add(new JsonObject
        {
            ["kind"] = "list",
            ["style"] = "bullet",
            ["icon"] = null,
            ["items"] = new JsonArray { "Check the route.", "Watch the map." },
            ["align"] = "start",
        });
        blocks.Add(new JsonObject { ["kind"] = "quote", ["text"] = "Best night of the year.", ["attribution"] = null, ["align"] = "center" });
        blocks.Add(new JsonObject
        {
            ["kind"] = "media",
            ["media"] = new JsonObject { ["mediaId"] = HangarMediaId, ["alt"] = "The helicopter in its hangar" },
            ["caption"] = null,
            ["size"] = "medium",
            ["align"] = "center",
        });
        blocks.Add(new JsonObject
        {
            ["kind"] = "links",
            ["links"] = new JsonArray
            {
                new JsonObject { ["label"] = "Donate", ["href"] = "/donate", ["icon"] = null, ["newTab"] = false },
            },
            ["style"] = "buttons",
            ["align"] = "start",
        });
        return doc;
    }

    public static Snapshot BuildSnapshot()
    {
        // The published settings name the example route (id 3) as the site
        // route with the example map configuration; the starter content
        // itself carries no route.
        var content = BuildContentDocument();
        content.Settings.Route = new JsonObject
        {
            ["routeId"] = 3,
            ["mapConfig"] = new JsonObject
            {
                ["display"] = new JsonObject { ["timeLabelIntervalMinutes"] = 10, ["routeWidth"] = "thick" },
                ["controls"] = new JsonObject { ["terrain"] = false },
            },
        };

        var media = new SortedDictionary<string, MediaEntry>(StringComparer.Ordinal)
        {
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
                Dark = new MediaDarkEntry
                {
                    Url = $"{CdnBase}/media/{HangarDarkMediaId}/hangar-night.jpg",
                    Variants = new SortedDictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["1600"] = $"{CdnBase}/media/{HangarDarkMediaId}/w1600.webp",
                        ["480"]  = $"{CdnBase}/media/{HangarDarkMediaId}/w480.webp",
                        ["960"]  = $"{CdnBase}/media/{HangarDarkMediaId}/w960.webp",
                    },
                },
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
            // A video (contracts 1.3b): no variants, no pyramid, no dark or
            // small version; its poster rides in the map as its own entry.
            [FlyoverVideoMediaId] = new MediaEntry
            {
                Url = $"{CdnBase}/media/{FlyoverVideoMediaId}/flyover.mp4",
                Kind = "video",
                Width = 1920,
                Height = 1080,
                Alt = "Santa's helicopter lifting off at dusk",
                Variants = new SortedDictionary<string, string>(StringComparer.Ordinal),
                Dzi = null,
                Credit = "Flyover volunteers",
                DurationMs = 42500,
                PosterMediaId = FlyoverPosterMediaId,
            },
            [FlyoverPosterMediaId] = new MediaEntry
            {
                Url = $"{CdnBase}/media/{FlyoverPosterMediaId}/flyover-poster.jpg",
                Kind = "raster",
                Width = 1280,
                Height = 720,
                Alt = "The helicopter on the pad before liftoff",
                Variants = new SortedDictionary<string, string>(StringComparer.Ordinal)
                {
                    ["480"] = $"{CdnBase}/media/{FlyoverPosterMediaId}/w480.webp",
                    ["960"] = $"{CdnBase}/media/{FlyoverPosterMediaId}/w960.webp",
                },
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
                    CreatedAt = DateTimeOffset.Parse("2026-12-22T01:02:30.000Z"),
                },
                // Newest first: a posted message, then the status alert of
                // lift-off with the message typed on it.
                Alerts = new List<SnapshotAlert>
                {
                    new()
                    {
                        Id = 41,
                        Kind = "event_message",
                        StatusId = null,
                        MessageId = 12,
                        Subject = "Santa update: Santa is airborne over the valley.",
                        Message = "Santa is airborne over the valley.",
                        CreatedAt = DateTimeOffset.Parse("2026-12-22T01:02:30.000Z"),
                    },
                    new()
                    {
                        Id = 40,
                        Kind = "event_status",
                        StatusId = 3,
                        MessageId = 11,
                        Subject = "Santa just lifted off",
                        Message = "Santa is in the air. Wave from the porch!",
                        CreatedAt = DateTimeOffset.Parse("2026-12-22T01:02:11.000Z"),
                    },
                },
                TrackerBbox = new Bbox { West = -114.30, South = 46.75, East = -113.80, North = 47.05 },
                TrackerMap = new SnapshotTrackerMap
                {
                    Id = 3,
                    Name = "Missoula valley",
                    Bbox = new Bbox { West = -114.75, South = 46.35, East = -113.30, North = 47.25 },
                    MinZoom = 0,
                    MaxZoom = 15,
                    TerrainMaxZoom = 15,
                    TilesUrl = $"{CdnBase}/basemap/tiles.pmtiles",
                    TerrainUrl = $"{CdnBase}/basemap/terrain.pmtiles",
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
            SponsorsYear = 2026,
            CookieTypes = new List<SnapshotCookieType>
            {
                new() { Id = 1, Name = "Chocolate chip", Icon = new IconValue { Source = "library", Id = "cookie" }, Sort = 10 },
                new() { Id = 3, Name = "Gingerbread",    Icon = new IconValue { Source = "media", Id = GingerbreadMediaId, Display = JsonSerializer.SerializeToElement(new { sizePx = 48, shape = "circle" }) }, Sort = 20 },
            },
            Content = content,
            Media = media,
            Icons = icons,
            RouteMap = new RouteMap
            {
                Path = new List<RouteMapPoint>
                {
                    new() { Lat = 46.8721, Lng = -114.0012 },
                    new() { Lat = 46.874032, Lng = -114.008311 },
                    new() { Lat = 46.879514, Lng = -114.015027 },
                    new() { Lat = 46.886203, Lng = -114.017446 },
                },
                Timeline = new List<RouteMapTimelineEntry>
                {
                    new() { Minutes = 0,  Lat = 46.8721,   Lng = -114.0012 },
                    new() { Minutes = 5,  Lat = 46.874818, Lng = -114.009274 },
                    new() { Minutes = 10, Lat = 46.881297, Lng = -114.015672 },
                    new() { Minutes = 12, Lat = 46.886203, Lng = -114.017446 },
                },
                DurationMinutes = 12,
                Timed = true,
            },
            RouteMapConfig = new RouteMapConfig
            {
                Display = new RouteMapDisplay { TimeLabelIntervalMinutes = 10, RouteWidth = "thick" },
                Controls = new RouteMapControls { Terrain = false },
            },
            RouteView = new SnapshotRouteView
            {
                Bbox = new Bbox { West = -114.30, South = 46.75, East = -113.80, North = 47.05 },
                Map = new SnapshotTrackerMap
                {
                    Id = 3,
                    Name = "Missoula valley",
                    Bbox = new Bbox { West = -114.75, South = 46.35, East = -113.30, North = 47.25 },
                    MinZoom = 0,
                    MaxZoom = 15,
                    TerrainMaxZoom = 15,
                    TilesUrl = $"{CdnBase}/basemap/tiles.pmtiles",
                    TerrainUrl = $"{CdnBase}/basemap/terrain.pmtiles",
                },
            },
            // The two example themes; neither has a thumbnail, so `media` holds
            // only the entries above.
            TrackerThemes = new List<SnapshotTrackerTheme>
            {
                new()
                {
                    Id = 1,
                    Renderer = "google",
                    Key = "standard",
                    Name = "Standard",
                    StyleUrl = $"{CdnBase}/themes/{StandardStyleSha}.json",
                    SpriteUrl = null,
                    ThumbnailMediaId = null,
                    Chrome = new Chrome { Bg = "#ffffff", Fg = "#5f6368", Text = "#202124", Tile = "#e8f0fe", TileFg = "#1a56c4", Panel = "#ffffffe6", Accent = "#1a56c4" },
                    Overlay = new Overlay { RouteColor = "#1a56c4", RouteOpacity = 0.9, ArrowColor = "#ffffff", TimeLabelBg = "#1c1c1e", TimeLabelFg = "#ffffff", TimeLabelOpacity = 0.8, UserColor = "#c62828" },
                    DefaultLightMode = true,
                    DefaultDarkMode = false,
                },
                new()
                {
                    Id = 7,
                    Renderer = "maplibre",
                    Key = "light",
                    Name = "Light",
                    StyleUrl = $"{CdnBase}/themes/{RouteLightStyleSha}.json",
                    SpriteUrl = $"{CdnBase}/themes/7/sprites/{RouteLightSpriteSha}/sprite",
                    ThumbnailMediaId = null,
                    Chrome = new Chrome { Bg = "#fbfaf7", Fg = "#5c5a55", Text = "#1e1d1a", Tile = "#e8efe6", TileFg = "#1f5f3a", Panel = "#fbfaf7e6", Accent = "#1f5f3a" },
                    Overlay = new Overlay { RouteColor = "#c62828", RouteOpacity = 0.9, ArrowColor = "#ffffff", TimeLabelBg = "#1e1d1a", TimeLabelFg = "#ffffff", TimeLabelOpacity = 0.8, UserColor = "#1a56c4" },
                    DefaultLightMode = true,
                    DefaultDarkMode = false,
                },
            },
            QrCodes = new SortedDictionary<string, SnapshotQrCode>(StringComparer.Ordinal)
            {
                // The example code resolves through an ancestor place that opens
                // the sponsors page; the snapshot carries the resolved slug.
                ["qr-001"] = new SnapshotQrCode { PageSlug = "sponsors", ForwardUrl = null },
                // A home-resolving code (contracts 1.3): pageSlug "/" for both
                // the home role page and codes with no code- or place-set opens.
                ["qr-002"] = new SnapshotQrCode { PageSlug = "/", ForwardUrl = null },
            },
            CookieLimitPerPerson = 10,
            SignUpsOpen = true,
            SubscriptionsOpen = true,
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

    public static BeaconStateBody BuildBeaconState()
    {
        // contracts 4.2: the beacon's own document plus the version it read.
        // The document here is what a replaying service might keep so the
        // next leader resumes where the last one stopped; the API never
        // interprets it.
        const string stateJson = """
{
  "mode": "replay",
  "eventId": 7,
  "speed": 4,
  "cursor": { "index": 1832, "recordedAt": "2026-12-22T01:31:07.000Z" },
  "startedAt": "2026-12-22T01:00:00.000Z"
}
""";
        using var doc = System.Text.Json.JsonDocument.Parse(stateJson);
        return new BeaconStateBody
        {
            State = doc.RootElement.Clone(),
            ExpectedVersion = 3,
        };
    }

    public static ContactInboxObject BuildContactInbox()
    {
        // contracts 4.3: the object `POST /contact` writes under contact-inbox/.
        return new ContactInboxObject
        {
            Name = "Jane Doe",
            Email = "jane@example.com",
            Message = "Hello",
            ClientIp = "203.0.113.7",
            ReceivedAt = DateTimeOffset.Parse("2026-12-22T01:31:07.123Z"),
        };
    }

    public static MeNotificationsResponse BuildMeNotifications()
    {
        // contracts 4.4: GET /me/notifications for a verified subscriber who
        // has seen the alerts up to outbox id 1042.
        return new MeNotificationsResponse
        {
            Subscription = new SubscriptionDto
            {
                Id = 17,
                Channel = "email",
                Address = "jane@example.com",
                VerifiedAt = DateTimeOffset.Parse("2026-11-30T18:04:11.000Z"),
                UnsubscribedAt = null,
                CreatedAt = DateTimeOffset.Parse("2026-11-30T18:02:40.000Z"),
            },
            SeenUpToId = 1042,
        };
    }

    public static MeNotificationsSeenRequest BuildMeNotificationsSeen()
    {
        // contracts 4.4: the PUT /me/notifications/seen body.
        return new MeNotificationsSeenRequest { SeenUpToId = 1043 };
    }
}
