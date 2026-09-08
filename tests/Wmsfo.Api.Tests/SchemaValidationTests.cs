using System.Text.Json.Nodes;
using Json.Schema;
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
