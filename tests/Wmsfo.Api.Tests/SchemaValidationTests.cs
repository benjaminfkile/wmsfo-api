using System.Text.Json.Nodes;
using Json.Schema;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts;

namespace Wmsfo.Api.Tests;

// Acceptance criterion 685: every schema validates its fixture.
public class SchemaValidationTests
{
    public static IEnumerable<object[]> FixtureSchemaPairs()
    {
        // Only schemas that have a matching fixture:
        yield return new object[] { "live-object" };
        yield return new object[] { "snapshot" };
        yield return new object[] { "route" };
        yield return new object[] { "location" };
        yield return new object[] { "heartbeat" };
        yield return new object[] { "beacon-state" };
        yield return new object[] { "contact-inbox" };
        yield return new object[] { "me" };
        yield return new object[] { "me-preferences" };
        yield return new object[] { "me-seen" };
    }

    [Theory]
    [MemberData(nameof(FixtureSchemaPairs))]
    public void Fixture_validates_against_its_schema(string name)
    {
        var schemaPath = Path.Combine(ContractsPaths.SchemaDir, name + ".schema.json");
        var fixturePath = Path.Combine(ContractsPaths.FixturesDir, name + ".json");
        Assert.True(File.Exists(schemaPath), $"missing schema: {schemaPath}");
        Assert.True(File.Exists(fixturePath), $"missing fixture: {fixturePath}");

        var schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixturePath));

        var results = schema.Evaluate(doc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (!results.IsValid)
        {
            var details = results.Details ?? Enumerable.Empty<EvaluationResults>();
            var messages = string.Join("\n", details
                .Where(d => d.Errors is { Count: > 0 })
                .SelectMany(d => (d.Errors ?? new Dictionary<string, string>()).Select(e => $"{d.InstanceLocation}: {e.Key}={e.Value}")));
            Assert.Fail($"schema {name} did not accept fixture:\n{messages}");
        }
    }

    // The snapshot schema takes an event without a map.
    [Fact]
    public void Snapshot_with_a_null_tracker_map_validates()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(ContractsPaths.SchemaDir, "snapshot.schema.json")));
        var snapshot = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.FixturesDir, "snapshot.json")))!;
        snapshot["event"]!["trackerMap"] = null;
        using var doc = System.Text.Json.JsonDocument.Parse(snapshot.ToJsonString());
        Assert.True(schema.Evaluate(doc.RootElement).IsValid);

        // Each tracker key, the alerts, the cookie limit, and the two switches are required.
        foreach (var (parent, key) in new[]
        {
            ("event", "trackerBbox"), ("event", "trackerStartView"), ("event", "trackerMap"), ("", "trackerThemes"),
            ("event", "alerts"), ("", "cookieLimitPerPerson"), ("", "signUpsOpen"), ("", "subscriptionsOpen"),
        })
        {
            var without = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.FixturesDir, "snapshot.json")))!;
            (parent == "" ? without.AsObject() : without[parent]!.AsObject()).Remove(key);
            using var missing = System.Text.Json.JsonDocument.Parse(without.ToJsonString());
            Assert.False(schema.Evaluate(missing.RootElement).IsValid, $"snapshot schema accepted a snapshot without {key}");
        }
    }

    // A148: every trackerThemes[].overlay carries headingColor, a colour,
    // last in the key order, and no other key beside the eight.
    [Fact]
    public void Snapshot_theme_overlay_requires_a_heading_colour()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(ContractsPaths.SchemaDir, "snapshot.schema.json")));
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.FixturesDir, "snapshot.json")))!;
        foreach (var theme in fixture["trackerThemes"]!.AsArray())
        {
            var overlay = theme!["overlay"]!.AsObject();
            Assert.Equal("headingColor", overlay.Last().Key);
            Assert.Equal("#ffb300", overlay["headingColor"]!.GetValue<string>());
        }

        bool Valid(Action<JsonObject> change)
        {
            var copy = fixture.DeepClone();
            change(copy["trackerThemes"]![0]!["overlay"]!.AsObject());
            using var doc = System.Text.Json.JsonDocument.Parse(copy.ToJsonString());
            return schema.Evaluate(doc.RootElement).IsValid;
        }
        Assert.True(Valid(o => o["headingColor"] = "#69f0aecc"));
        Assert.False(Valid(o => o.Remove("headingColor")));
        Assert.False(Valid(o => o["headingColor"] = "amber"));
        Assert.False(Valid(o => o["headingColor"] = "#ffb30"));
        Assert.False(Valid(o => o["beamColor"] = "#ffb300"));
    }

    // A142: the snapshot's event.trackerStartView is a TrackerStartView or
    // null; the fixture's centre lies inside the fixture's box.
    [Fact]
    public void Snapshot_tracker_start_view_is_nullable_bounded_and_inside_the_fixture_box()
    {
        var schema = JsonSchema.FromText(File.ReadAllText(Path.Combine(ContractsPaths.SchemaDir, "snapshot.schema.json")));
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.FixturesDir, "snapshot.json")))!;
        var box = fixture["event"]!["trackerBbox"]!;
        var center = fixture["event"]!["trackerStartView"]!["center"]!;
        var lat = center["lat"]!.GetValue<double>();
        var lng = center["lng"]!.GetValue<double>();
        Assert.InRange(lng, box["west"]!.GetValue<double>(), box["east"]!.GetValue<double>());
        Assert.InRange(lat, box["south"]!.GetValue<double>(), box["north"]!.GetValue<double>());

        bool Valid(JsonNode? view)
        {
            var copy = fixture.DeepClone();
            copy["event"]!["trackerStartView"] = view;
            using var doc = System.Text.Json.JsonDocument.Parse(copy.ToJsonString());
            return schema.Evaluate(doc.RootElement).IsValid;
        }
        Assert.True(Valid(null));
        Assert.True(Valid(JsonNode.Parse("""{"center":{"lat":46.9,"lng":-114.0},"zoom":3}""")));
        Assert.True(Valid(JsonNode.Parse("""{"center":{"lat":46.9,"lng":-114.0},"zoom":18}""")));
        Assert.False(Valid(JsonNode.Parse("""{"center":{"lat":46.9,"lng":-114.0},"zoom":2}""")));
        Assert.False(Valid(JsonNode.Parse("""{"center":{"lat":46.9,"lng":-114.0},"zoom":19}""")));
        Assert.False(Valid(JsonNode.Parse("""{"center":{"lat":46.9,"lng":-114.0}}""")));
        Assert.False(Valid(JsonNode.Parse("""{"center":{"lat":46.9},"zoom":11}""")));
        Assert.False(Valid(JsonNode.Parse("""{"center":{"lat":46.9,"lng":-114.0},"zoom":11,"pitch":0}""")));

        // `$defs/TrackerStartView`, the shape the event writes are checked against.
        var validator = ContentSchemasTests.Validator;
        Assert.Empty(validator.ValidateTrackerStartView(JsonNode.Parse("""{"center":{"lat":46.9,"lng":-114.0},"zoom":11}""")));
        foreach (var bad in new[]
        {
            """{"center":{"lat":46.9,"lng":-114.0},"zoom":2}""",
            """{"center":{"lat":46.9,"lng":-114.0},"zoom":19}""",
            """{"center":{"lat":46.9,"lng":-114.0},"zoom":11.5}""",
            """{"center":{"lat":46.9,"lng":-114.0}}""",
            """{"center":{"lng":-114.0},"zoom":11}""",
            """{"center":{"lat":46.9,"lng":-114.0,"alt":0},"zoom":11}""",
        })
        {
            Assert.NotEmpty(validator.ValidateTrackerStartView(JsonNode.Parse(bad)));
        }
    }

    // The themes a tracker offers are the event's: a `map` section carrying
    // `themes` fails at the key, one without passes.
    [Fact]
    public void Map_section_with_themes_fails_at_themes_and_without_passes()
    {
        var validator = ContentSchemasTests.Validator;
        var data = MapDefaults();
        foreach (var level in new[] { ValidationLevel.Draft, ValidationLevel.Publish })
        {
            Assert.Empty(validator.ValidateSectionData("map", data, level));

            var withThemes = (JsonObject)data.DeepClone();
            withThemes["themes"] = new JsonArray("standard");
            Assert.Contains(validator.ValidateSectionData("map", withThemes, level), p => p.Path == "/themes");

            var withDefault = (JsonObject)data.DeepClone();
            withDefault["defaultTheme"] = "night";
            Assert.Contains(validator.ValidateSectionData("map", withDefault, level), p => p.Path == "/defaultTheme");
        }
    }

    // settings.tracker.defaultBbox keeps the box rules at both levels. Settings
    // paths are relative to the settings object; publish prefixes `/settings`.
    [Theory]
    [InlineData(ValidationLevel.Draft)]
    [InlineData(ValidationLevel.Publish)]
    public void Settings_tracker_default_bbox_validates_at_both_levels(ValidationLevel level)
    {
        var validator = ContentSchemasTests.Validator;
        Assert.Empty(validator.ValidateSiteSettings(
            WithTracker("""{"defaultBbox":{"west":-114.75,"south":46.35,"east":-113.30,"north":47.25}}"""), level));
        Assert.Empty(validator.ValidateSiteSettings(StarterSettings(), level));

        foreach (var bad in new[]
        {
            """{"defaultBbox":{"west":-120,"south":40,"east":-95,"north":45}}""",        // 25 degrees wide
            """{"defaultBbox":{"west":-113.30,"south":46.35,"east":-114.75,"north":47.25}}""", // west > east
            """{"defaultBbox":{"west":-114.75,"south":47.25,"east":-113.30,"north":46.35}}""", // south > north
            """{"defaultBbox":{"west":-114.00,"south":46.35,"east":-113.99,"north":47.25}}""", // under 0.05 wide
        })
        {
            var problems = validator.ValidateSiteSettings(WithTracker(bad), level);
            Assert.Contains(problems, p => p.Path == "/tracker/defaultBbox");
        }

        // No other key under `tracker` or in the box.
        Assert.NotEmpty(validator.ValidateSiteSettings(WithTracker(
            """{"defaultBbox":{"west":-114.75,"south":46.35,"east":-113.30,"north":47.25},"zoom":10}"""), level));
        Assert.NotEmpty(validator.ValidateSiteSettings(WithTracker(
            """{"defaultBbox":{"west":-114.75,"south":46.35,"east":-113.30,"north":47.25,"zoom":10}}"""), level));
        Assert.NotEmpty(validator.ValidateSiteSettings(WithTracker(
            """{"defaultBbox":{"west":"a","south":46.35,"east":-113.30,"north":47.25}}"""), level));
    }

    [Fact]
    public void Settings_tracker_requires_default_bbox_at_publish()
    {
        Assert.NotEmpty(ContentSchemasTests.Validator.ValidateSiteSettings(WithTracker("{}"), ValidationLevel.Publish));
    }

    private static JsonObject MapDefaults() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, "kinds.json")))!["kinds"]!
            .AsArray().Single(k => k!["kind"]!.GetValue<string>() == "map")!["defaults"]!.DeepClone();

    private static JsonObject StarterSettings() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, "starter-content.json")))!["settings"]!.DeepClone();

    private static JsonObject WithTracker(string tracker)
    {
        var settings = StarterSettings();
        settings["tracker"] = JsonNode.Parse(tracker);
        return settings;
    }

    [Theory]
    [InlineData("realtime-authorize")]
    [InlineData("realtime-message")]
    public void Schemas_without_fixtures_still_parse(string name)
    {
        var schemaPath = Path.Combine(ContractsPaths.SchemaDir, name + ".schema.json");
        Assert.True(File.Exists(schemaPath), $"missing schema: {schemaPath}");
        var schema = JsonSchema.FromText(File.ReadAllText(schemaPath));
        Assert.NotNull(schema);
    }
}
