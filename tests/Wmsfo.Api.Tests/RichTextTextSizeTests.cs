using System.Text.Json.Nodes;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Tests;

// Contracts 1.3a: `rich_text` data carries an optional `textSize` of "normal", "large",
// or null; and an inline link's `{newtab}` marker leaves its href checks unchanged.
public class RichTextTextSizeTests
{
    // The schema registry is process wide, so the validator is the one the content schema tests built.
    private static SchemaValidator Validator => ContentSchemasTests.Validator;

    private static JsonObject Data(JsonNode? textSize, bool include = true)
    {
        var data = new JsonObject
        {
            ["blocks"] = new JsonArray(new JsonObject { ["kind"] = "paragraph", ["text"] = "Hello." }),
        };
        if (include) data["textSize"] = textSize;
        return data;
    }

    public static IEnumerable<object[]> Levels() =>
        new[] { new object[] { ValidationLevel.Draft }, new object[] { ValidationLevel.Publish } };

    [Theory]
    [MemberData(nameof(Levels))]
    public void TextSize_large_normal_null_and_absent_validate(ValidationLevel level)
    {
        foreach (var data in new[] { Data("large"), Data("normal"), Data(null), Data(null, include: false) })
        {
            var problems = Validator.ValidateSectionData("rich_text", data, level);
            Assert.True(problems.Count == 0, string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}")));
        }
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public void TextSize_huge_is_refused_at_the_field(ValidationLevel level)
    {
        var problems = Validator.ValidateSectionData("rich_text", Data("huge"), level);
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Path.StartsWith("/textSize", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_invalid_inline_href_with_the_newtab_marker_reports_the_same_problem()
    {
        var plain = await InlineHrefProblems("Go [there](ftp://example.com/x) now.");
        var marked = await InlineHrefProblems("Go [there](ftp://example.com/x){newtab} now.");

        var expected = Assert.Single(plain);
        var actual = Assert.Single(marked);
        Assert.Equal(expected.Path, actual.Path);
        Assert.Equal(expected.Message, actual.Message);
        Assert.Contains("ftp://example.com/x", actual.Message);
    }

    // No media is referenced, so the checker never opens the connection.
    private static async Task<IReadOnlyList<ProblemRefDto>> InlineHrefProblems(string text)
    {
        var data = new JsonObject
        {
            ["blocks"] = new JsonArray(new JsonObject { ["kind"] = "paragraph", ["text"] = text }),
        };
        var section = new ReferenceChecker.SectionInput(1, "rich_text", data, null, Array.Empty<ReferenceChecker.ItemInput>());
        var page = new ReferenceChecker.PageInput(1, "home", "home", new[] { section });
        var checker = new ReferenceChecker(null);
        return await checker.CheckAsync(new ReferenceChecker.WorkingSet(new[] { page }, null), null!, null, CancellationToken.None);
    }
}
