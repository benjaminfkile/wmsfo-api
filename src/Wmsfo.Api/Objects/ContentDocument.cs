using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Wmsfo.Api.Objects;

// Content document (contracts 1.3a). One canonical DTO used by the serializer,
// the fixtures, and the snapshot's `content`. Key order matches the contract.
public sealed class ContentDocument
{
    [JsonPropertyOrder(0)] public int SchemaVersion { get; set; } = 1;
    [JsonPropertyOrder(1)] public SiteSettings Settings { get; set; } = new();
    [JsonPropertyOrder(2)] public IList<ContentPage> Pages { get; set; } = new List<ContentPage>();
}

public sealed class SiteSettings
{
    [JsonPropertyOrder(0)] public string SiteName { get; set; } = "";
    [JsonPropertyOrder(1)] public string? Tagline { get; set; }
    [JsonPropertyOrder(2)] public string HomeNavLabel { get; set; } = "";
    [JsonPropertyOrder(3)] public IconValue? Logo { get; set; }
    [JsonPropertyOrder(4)] public IconValue? Favicon { get; set; }
    [JsonPropertyOrder(5)] public SiteTheme Theme { get; set; } = new();
    [JsonPropertyOrder(6)] public IList<LinkValue> NavExtraLinks { get; set; } = new List<LinkValue>();
    [JsonPropertyOrder(7)] public IList<LinkValue> FooterLinks { get; set; } = new List<LinkValue>();
    [JsonPropertyOrder(8)] public string? FooterText { get; set; }
    [JsonPropertyOrder(9)] public string? ContactEmail { get; set; }
    [JsonPropertyOrder(10)] public string? DonateUrl { get; set; }
    [JsonPropertyOrder(11)] public bool AnalyticsEnabled { get; set; }
    // Optional keys: absent when null (no logoMedia keeps the built-in mark,
    // headerShowsSiteName defaults to true). LogoMedia is a MediaRef `{ mediaId, alt }`.
    [JsonPropertyOrder(12), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public JsonNode? LogoMedia { get; set; }
    [JsonPropertyOrder(13), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? HeaderShowsSiteName { get; set; }
    // Optional key: absent when null (absent means the built-in route map defaults).
    [JsonPropertyOrder(14), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public RouteMapDisplay? RouteMap { get; set; }
}

// The route map display knobs (contracts 1.3a), sitewide in `settings.routeMap` and per
// section in route_preview `display`. Every key is optional and absent when null; an
// absent key means the next level: the section, then the sitewide value, then the
// built-in default (15, true, "medium", "normal").
public sealed class RouteMapDisplay
{
    [JsonPropertyOrder(0), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? TimeLabelIntervalMinutes { get; set; }
    [JsonPropertyOrder(1), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Arrows { get; set; }
    [JsonPropertyOrder(2), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ArrowSize { get; set; }
    [JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? RouteWidth { get; set; }
}

public sealed class SiteTheme
{
    [JsonPropertyOrder(0)] public bool SnowDefault { get; set; }
    [JsonPropertyOrder(1)] public bool LightsDefault { get; set; }
    // Default true so a published document without `ornaments` still means ornaments on
    // (contracts 1.3a; introduced in contracts 15).
    [JsonPropertyOrder(2)] public bool Ornaments { get; set; } = true;
    // Optional keys: absent when null (absent means 100, a fully opaque card fill).
    [JsonPropertyOrder(3), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CardOpacityLight { get; set; }
    [JsonPropertyOrder(4), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CardOpacityDark { get; set; }
}

public sealed class LinkValue
{
    [JsonPropertyOrder(0)] public string Label { get; set; } = "";
    [JsonPropertyOrder(1)] public string Href { get; set; } = "";
    [JsonPropertyOrder(2)] public IconValue? Icon { get; set; }
    [JsonPropertyOrder(3)] public bool NewTab { get; set; }
}

public sealed class ContentPage
{
    [JsonPropertyOrder(0)] public long Id { get; set; }
    [JsonPropertyOrder(1)] public string Slug { get; set; } = "";
    [JsonPropertyOrder(2)] public string Title { get; set; } = "";
    [JsonPropertyOrder(3)] public string? NavLabel { get; set; }
    [JsonPropertyOrder(4)] public int NavPosition { get; set; }
    [JsonPropertyOrder(5)] public string Role { get; set; } = "none";
    [JsonPropertyOrder(6)] public IList<ContentSection> Sections { get; set; } = new List<ContentSection>();
}

public sealed class ContentSection
{
    [JsonPropertyOrder(0)] public long Id { get; set; }
    [JsonPropertyOrder(1)] public string Kind { get; set; } = "";
    [JsonPropertyOrder(2)] public Presentation Presentation { get; set; } = new();
    // Data shape varies per kind; JsonNode preserves the writer's insertion order under CanonicalJson.
    [JsonPropertyOrder(3)] public JsonNode Data { get; set; } = new JsonObject();
    [JsonPropertyOrder(4)] public IList<ContentItem> Items { get; set; } = new List<ContentItem>();
}

public sealed class ContentItem
{
    [JsonPropertyOrder(0)] public long Id { get; set; }
    [JsonPropertyOrder(1)] public JsonNode Data { get; set; } = new JsonObject();
}

// Presentation (contracts 1.3a): key order fixed.
public sealed class Presentation
{
    [JsonPropertyOrder(0)] public string Width { get; set; } = "wide";
    [JsonPropertyOrder(1)] public string Align { get; set; } = "start";
    [JsonPropertyOrder(2)] public JsonNode Background { get; set; } = BackgroundNone();
    [JsonPropertyOrder(3)] public string Spacing { get; set; } = "normal";
    [JsonPropertyOrder(4)] public IconValue? IconBefore { get; set; }
    [JsonPropertyOrder(5)] public IconValue? IconAfter { get; set; }
    [JsonPropertyOrder(6)] public string? Anchor { get; set; }
    // Optional keys: absent when null (card defaults to true, iconSize to "sm").
    [JsonPropertyOrder(7), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Card { get; set; }
    [JsonPropertyOrder(8), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? IconSize { get; set; }
    // Optional keys: absent when null (absent falls back to the sitewide value for the theme).
    [JsonPropertyOrder(9), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CardOpacityLight { get; set; }
    [JsonPropertyOrder(10), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public int? CardOpacityDark { get; set; }

    public static JsonNode BackgroundNone() => new JsonObject { ["kind"] = "none" };
}
