using System.Text.Json;
using System.Text.Json.Nodes;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// Contracts 1.3a: `settings.landmarks` is an optional Landmark[] of 0 to 50
// entries, validated like `headerLinks`; the published document omits the key
// when absent. RouteMapConfig carries no landmarks, and the `map` section's
// `controls.landmarks` is an optional boolean.
public class SiteSettingsLandmarksTests
{
    // The schema registry is process wide, so the validator is the one the content schema tests built.
    private static SchemaValidator Validator => ContentSchemasTests.Validator;

    [Fact]
    public void Fifty_landmarks_validate_and_ride_the_published_document_in_contract_order()
    {
        var settings = ValidSiteSettings();
        settings["landmarks"] = Landmarks(50);

        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Draft));
        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish));

        var published = PublishedSettings(settings);
        var landmarks = published["landmarks"]!.AsArray();
        Assert.Equal(50, landmarks.Count);
        Assert.Equal(
            new[] { "name", "lat", "lng", "icon", "description" },
            landmarks[0]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal(new[] { "name", "lat", "lng" }, landmarks[1]!.AsObject().Select(kv => kv.Key).ToArray());
        Assert.Equal("Landmark 1", landmarks[0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Fifty_one_landmarks_fail_publish_at_landmarks()
    {
        var settings = ValidSiteSettings();
        settings["landmarks"] = Landmarks(51);

        var problems = Validator.ValidateSiteSettings(settings, ValidationLevel.Publish);
        Assert.Contains(problems, p => p.Path == "/landmarks");
    }

    [Fact]
    public void A_landmark_without_name_fails_publish()
    {
        var settings = ValidSiteSettings();
        var landmarks = Landmarks(1);
        ((JsonObject)landmarks[0]!).Remove("name");
        settings["landmarks"] = landmarks;

        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Draft));
        var problems = Validator.ValidateSiteSettings(settings, ValidationLevel.Publish);
        Assert.Contains(problems, p => p.Path.StartsWith("/landmarks/0", StringComparison.Ordinal));
    }

    [Fact]
    public void A_landmark_at_lat_91_fails_at_both_levels()
    {
        var settings = ValidSiteSettings();
        var landmarks = Landmarks(1);
        landmarks[0]!["lat"] = 91;
        settings["landmarks"] = landmarks;

        Assert.Contains(Validator.ValidateSiteSettings(settings, ValidationLevel.Draft), p => p.Path == "/landmarks/0/lat");
        Assert.Contains(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish), p => p.Path == "/landmarks/0/lat");
    }

    [Theory]
    [InlineData("{\"name\":\"A\",\"lat\":1,\"lng\":181}", "/landmarks/0/lng")]
    [InlineData("{\"name\":\"A\",\"lat\":1,\"lng\":1,\"description\":\"\"}", "/landmarks/0/description")]
    [InlineData("{\"name\":\"A\",\"lat\":1,\"lng\":1,\"url\":\"x\"}", "/landmarks/0")]
    [InlineData("{\"name\":\"A\",\"lat\":1,\"lng\":1,\"icon\":{\"source\":\"clipart\",\"id\":\"x\"}}", "/landmarks/0/icon")]
    public void A_bad_landmark_fails_publish_on_the_field(string landmark, string path)
    {
        var settings = ValidSiteSettings();
        settings["landmarks"] = new JsonArray(JsonNode.Parse(landmark));

        var problems = Validator.ValidateSiteSettings(settings, ValidationLevel.Publish);
        Assert.Contains(problems, p => p.Path.StartsWith(path, StringComparison.Ordinal));
    }

    [Fact]
    public void Absent_landmarks_publish_and_the_document_carries_no_key()
    {
        var settings = ValidSiteSettings();

        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish));
        Assert.False(PublishedSettings(settings).ContainsKey("landmarks"));
    }

    [Fact]
    public void Starter_content_has_no_landmarks()
    {
        var starter = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, "starter-content.json")))!;
        Assert.False(starter["settings"]!.AsObject().ContainsKey("landmarks"));
        Assert.Null(StarterContentBuilder.Build().Settings.Landmarks);
    }

    [Fact]
    public void Route_map_config_refuses_landmarks()
    {
        var problems = Validator.ValidateRouteMapConfig(JsonNode.Parse(
            "{\"landmarks\":[{\"name\":\"A\",\"lat\":1,\"lng\":1}]}"));
        Assert.Contains(problems, p => p.Path == "/landmarks");
    }

    [Theory]
    [InlineData("false")]
    [InlineData("true")]
    [InlineData(null)]
    public void Map_controls_landmarks_is_an_optional_boolean(string? value)
    {
        var data = MapSectionData(value);
        AssertNoProblems(Validator.ValidateSectionData("map", data, ValidationLevel.Draft));
        AssertNoProblems(Validator.ValidateSectionData("map", data, ValidationLevel.Publish));
    }

    [Fact]
    public void Map_controls_landmarks_must_be_a_boolean()
    {
        var problems = Validator.ValidateSectionData("map", MapSectionData("\"no\""), ValidationLevel.Publish);
        Assert.Contains(problems, p => p.Path == "/controls/landmarks");
    }

    // The starter `map` section's data with `controls.landmarks` set to the given
    // JSON value, or without the key when null.
    private static JsonNode MapSectionData(string? landmarks)
    {
        var starter = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, "starter-content.json")))!;
        var data = starter["pages"]!.AsArray()
            .SelectMany(p => p!["sections"]!.AsArray())
            .Single(s => s!["kind"]!.GetValue<string>() == "map")!["data"]!.DeepClone();
        var controls = data["controls"]!.AsObject();
        Assert.False(controls.ContainsKey("landmarks"));
        if (landmarks is not null) controls["landmarks"] = JsonNode.Parse(landmarks);
        return data;
    }

    // The published document's settings: the stored settings read into the DTO and
    // written back with the canonical options, as the document builder does.
    private static JsonObject PublishedSettings(JsonObject stored)
    {
        var dto = stored.Deserialize<SiteSettings>(CanonicalJson.Options)!;
        return JsonNode.Parse(JsonSerializer.Serialize(dto, CanonicalJson.Options))!.AsObject();
    }

    // `count` landmarks; the first carries an icon and a description.
    private static JsonArray Landmarks(int count)
    {
        var landmarks = new JsonArray();
        for (var i = 1; i <= count; i++)
        {
            var landmark = new JsonObject
            {
                ["name"] = $"Landmark {i}",
                ["lat"] = 46.8 + i / 1000.0,
                ["lng"] = -114.0 - i / 1000.0,
            };
            if (i == 1)
            {
                landmark["icon"] = new JsonObject { ["source"] = "library", ["id"] = "tree" };
                landmark["description"] = "The downtown tree lighting starts here.";
            }
            landmarks.Add(landmark);
        }
        return landmarks;
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
