using System.Text.Json.Nodes;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Icons;

namespace Wmsfo.Api.Tests;

// Contracts 1.3a: `alerts_signup` data carries an optional `subscribedHeading`, Inline or
// null, which the site shows in place of `heading` to a subscribed viewer.
public class AlertsSignupSubscribedHeadingTests
{
    // The schema registry is process wide, so the validator is the one the content schema tests built.
    private static SchemaValidator Validator => ContentSchemasTests.Validator;

    private static JsonObject Data(JsonNode? subscribedHeading, bool include = true)
    {
        var data = new JsonObject
        {
            ["heading"] = "Get alerts",
            ["copy"] = null,
            ["signedOutCopy"] = "Sign in to sign up for alerts.",
        };
        if (include) data["subscribedHeading"] = subscribedHeading;
        return data;
    }

    public static IEnumerable<object[]> Levels() =>
        new[] { new object[] { ValidationLevel.Draft }, new object[] { ValidationLevel.Publish } };

    private static string Describe(IReadOnlyList<ProblemDto> problems) =>
        string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}"));

    [Theory]
    [MemberData(nameof(Levels))]
    public void Without_the_key_passes(ValidationLevel level)
    {
        var problems = Validator.ValidateSectionData("alerts_signup", Data(null, include: false), level);
        Assert.True(problems.Count == 0, Describe(problems));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void With_Alerts_passes(ValidationLevel level)
    {
        var problems = Validator.ValidateSectionData("alerts_signup", Data("Alerts"), level);
        Assert.True(problems.Count == 0, Describe(problems));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void With_null_passes(ValidationLevel level)
    {
        var problems = Validator.ValidateSectionData("alerts_signup", Data(null), level);
        Assert.True(problems.Count == 0, Describe(problems));
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void With_a_number_fails_at_the_field(ValidationLevel level)
    {
        var problems = Validator.ValidateSectionData("alerts_signup", Data(7), level);
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Path.StartsWith("/subscribedHeading", StringComparison.Ordinal));
    }

    [Fact]
    public void Kind_default_carries_Alerts_and_validates_at_publish()
    {
        var defaults = ContentSchemasTests.Registry.ByName["alerts_signup"].Defaults;
        Assert.Equal("Alerts", (string?)defaults!["subscribedHeading"]);
        var problems = Validator.ValidateSectionData("alerts_signup", defaults, ValidationLevel.Publish);
        Assert.True(problems.Count == 0, Describe(problems));
    }

    // The starter content and both fixtures carry "Alerts" on every `alerts_signup`
    // section, and each such section validates at the publish level.
    [Theory]
    [InlineData("starter-content.json", null)]
    [InlineData("fixtures/content-document.json", null)]
    [InlineData("fixtures/snapshot.json", "content")]
    public void Starter_content_and_fixtures_carry_Alerts_and_validate(string file, string? contentKey)
    {
        var root = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, file)))!;
        var document = contentKey is null ? root : root[contentKey]!;
        var sections = document["pages"]!.AsArray()
            .SelectMany(p => p!["sections"]!.AsArray())
            .Where(s => (string?)s!["kind"] == "alerts_signup")
            .ToList();
        Assert.NotEmpty(sections);
        foreach (var section in sections)
        {
            var data = section!["data"]!;
            Assert.Equal("Alerts", (string?)data["subscribedHeading"]);
            var problems = Validator.ValidateSectionData("alerts_signup", data, ValidationLevel.Publish);
            Assert.True(problems.Count == 0, Describe(problems));
        }
    }

    // An unknown library icon inside `subscribedHeading` is reported like one inside `copy`.
    [Fact]
    public async Task An_unknown_inline_icon_in_subscribedHeading_is_reported_like_one_in_copy()
    {
        var inCopy = Data("Alerts");
        inCopy["copy"] = "Ring {icon:no-such-icon} now.";
        var inHeading = Data("Ring {icon:no-such-icon} now.");

        var copyProblem = Assert.Single(await Problems(inCopy));
        var headingProblem = Assert.Single(await Problems(inHeading));
        Assert.EndsWith("/copy", copyProblem.Path, StringComparison.Ordinal);
        Assert.Equal(copyProblem.Path[..^"copy".Length] + "subscribedHeading", headingProblem.Path);
        Assert.Equal(copyProblem.Message, headingProblem.Message);
        Assert.Contains("no-such-icon", headingProblem.Message);
    }

    // No media is referenced, so the checker never opens the connection.
    private static async Task<IReadOnlyList<ProblemRefDto>> Problems(JsonObject data)
    {
        var section = new ReferenceChecker.SectionInput(1, "alerts_signup", data, null, Array.Empty<ReferenceChecker.ItemInput>());
        var page = new ReferenceChecker.PageInput(1, "home", "home", new[] { section });
        var checker = new ReferenceChecker(IconLibrary.Load(IconsPaths.IconsDir, "https://cdn.example"));
        return await checker.CheckAsync(new ReferenceChecker.WorkingSet(new[] { page }, null), null!, null, CancellationToken.None);
    }
}
