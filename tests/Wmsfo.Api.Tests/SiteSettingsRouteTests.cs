using System.Text.Json;
using System.Text.Json.Nodes;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// Contracts 1.3a: `settings.route` is the optional site route
// `{ routeId, mapConfig? }`: `routeId` an id or null, `mapConfig` a
// RouteMapConfig or null; draft-lenient and publish-strict like the other
// keys, and the published document carries it as written.
public class SiteSettingsRouteTests
{
    private static SchemaValidator Validator => ContentSchemasTests.Validator;

    [Fact]
    public void A_route_with_a_map_config_publishes_and_rides_the_published_document()
    {
        var settings = ValidSiteSettings();
        settings["route"] = new JsonObject
        {
            ["routeId"] = 12,
            ["mapConfig"] = new JsonObject
            {
                ["display"] = new JsonObject { ["arrows"] = false, ["labelSize"] = "large" },
                ["controls"] = new JsonObject { ["terrain"] = true },
            },
        };

        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Draft));
        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish));

        var published = PublishedSettings(settings);
        Assert.True(JsonNode.DeepEquals(settings["route"], published["route"]));
        Assert.Equal(12, SiteRoute.RouteId(published["route"]));
        var config = SiteRoute.MapConfig(published["route"])!;
        Assert.False(config.Display!.Arrows);
        Assert.Equal("large", config.Display.LabelSize);
        Assert.True(config.Controls!.Terrain);
    }

    [Fact]
    public void No_route_a_null_route_id_and_a_null_map_config_all_publish()
    {
        var absent = ValidSiteSettings();
        AssertNoProblems(Validator.ValidateSiteSettings(absent, ValidationLevel.Publish));
        Assert.False(PublishedSettings(absent).ContainsKey("route"));

        var none = ValidSiteSettings();
        none["route"] = new JsonObject { ["routeId"] = null, ["mapConfig"] = null };
        AssertNoProblems(Validator.ValidateSiteSettings(none, ValidationLevel.Publish));
        Assert.Null(SiteRoute.RouteId(none["route"]));
        Assert.Null(SiteRoute.MapConfig(none["route"]));
    }

    [Fact]
    public void A_route_without_route_id_is_a_draft_but_not_a_publish()
    {
        var settings = ValidSiteSettings();
        settings["route"] = new JsonObject { ["mapConfig"] = new JsonObject() };

        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Draft));
        Assert.Contains(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish),
            p => p.Path.StartsWith("/route", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{\"routeId\":\"12\"}")]
    [InlineData("{\"routeId\":1.5}")]
    [InlineData("{\"routeId\":12,\"color\":\"red\"}")]
    [InlineData("{\"routeId\":12,\"mapConfig\":{\"display\":{\"arrowSize\":\"huge\"}}}")]
    [InlineData("{\"routeId\":12,\"mapConfig\":{\"landmarks\":[]}}")]
    [InlineData("[12]")]
    public void A_misshapen_route_is_refused_at_both_levels(string route)
    {
        var settings = ValidSiteSettings();
        settings["route"] = JsonNode.Parse(route);

        Assert.Contains(Validator.ValidateSiteSettings(settings, ValidationLevel.Draft),
            p => p.Path.StartsWith("/route", StringComparison.Ordinal));
        Assert.Contains(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish),
            p => p.Path.StartsWith("/route", StringComparison.Ordinal));
    }

    [Fact]
    public void A_zero_route_id_is_refused_at_publish()
    {
        var settings = ValidSiteSettings();
        settings["route"] = new JsonObject { ["routeId"] = 0 };
        Assert.Contains(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish),
            p => p.Path.StartsWith("/route/routeId", StringComparison.Ordinal));
    }

    [Fact]
    public void Starter_content_carries_no_route_and_no_route_page()
    {
        var starter = StarterContentBuilder.Build();
        Assert.Null(starter.Settings.Route);
        Assert.DoesNotContain(starter.Pages, p => p.Slug == "route");
        // The status pages keep their route previews.
        Assert.Contains(starter.Pages, p => p.Sections.Any(s => s.Kind == "route_preview"));
    }

    private static JsonObject PublishedSettings(JsonObject stored)
    {
        var dto = stored.Deserialize<SiteSettings>(CanonicalJson.Options)!;
        return JsonNode.Parse(JsonSerializer.Serialize(dto, CanonicalJson.Options))!.AsObject();
    }

    private static void AssertNoProblems(IReadOnlyList<ProblemDto> problems) =>
        Assert.True(problems.Count == 0, string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}")));

    private static JsonObject ValidSiteSettings() => new()
    {
        ["siteName"] = "Test site",
        ["tagline"] = null,
        ["homeNavLabel"] = "Home",
        ["logo"] = null,
        ["favicon"] = null,
        ["theme"] = new JsonObject
        {
            ["snowDefault"] = true,
            ["lightsDefault"] = true,
            ["ornaments"] = true,
        },
        ["navExtraLinks"] = new JsonArray(),
        ["footerLinks"] = new JsonArray(),
        ["footerText"] = null,
        ["contactEmail"] = null,
        ["donateUrl"] = null,
        ["analyticsEnabled"] = false,
    };
}
