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
}

public sealed class SiteTheme
{
    [JsonPropertyOrder(0)] public bool SnowDefault { get; set; }
    [JsonPropertyOrder(1)] public bool LightsDefault { get; set; }
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

    public static JsonNode BackgroundNone() => new JsonObject { ["kind"] = "none" };
}
