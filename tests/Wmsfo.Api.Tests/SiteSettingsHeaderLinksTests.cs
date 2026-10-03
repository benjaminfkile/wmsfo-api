using System.Text.Json;
using System.Text.Json.Nodes;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// Contracts 1.3a: `settings.headerLinks` is an optional Link[] of 0 to 3 entries,
// validated like `footerLinks`; the published document omits the key when absent.
public class SiteSettingsHeaderLinksTests
{
    // The schema registry is process wide, so the validator is the one the content schema tests built.
    private static SchemaValidator Validator => ContentSchemasTests.Validator;

    [Fact]
    public void Three_header_links_publish_and_ride_the_published_document()
    {
        var settings = ValidSiteSettings();
        settings["headerLinks"] = Links(3);

        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Draft));
        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish));

        var published = PublishedSettings(settings);
        var links = published["headerLinks"]!.AsArray();
        Assert.Equal(3, links.Count);
        Assert.Equal("Link 1", links[0]!["label"]!.GetValue<string>());
        Assert.Equal("https://example.com/1", links[0]!["href"]!.GetValue<string>());
        Assert.True(links[0]!["newTab"]!.GetValue<bool>());
    }

    [Fact]
    public void Four_header_links_fail_publish_at_headerLinks()
    {
        var settings = ValidSiteSettings();
        settings["headerLinks"] = Links(4);

        var problems = Validator.ValidateSiteSettings(settings, ValidationLevel.Publish);
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Path.StartsWith("/headerLinks", StringComparison.Ordinal));
    }

    [Fact]
    public void A_header_link_without_href_fails_publish()
    {
        var settings = ValidSiteSettings();
        var links = Links(1);
        ((JsonObject)links[0]!).Remove("href");
        settings["headerLinks"] = links;

        var problems = Validator.ValidateSiteSettings(settings, ValidationLevel.Publish);
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Path.StartsWith("/headerLinks", StringComparison.Ordinal));
    }

    [Fact]
    public void Absent_header_links_publish_and_the_document_carries_no_key()
    {
        var settings = ValidSiteSettings();

        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish));
        var published = PublishedSettings(settings);
        Assert.False(published.ContainsKey("headerLinks"));
    }

    [Fact]
    public void Starter_content_carries_the_facebook_link_and_validates_at_publish()
    {
        var starter = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, "starter-content.json")))!;
        var settings = starter["settings"]!;
        AssertNoProblems(Validator.ValidateSiteSettings(settings, ValidationLevel.Publish));

        var links = settings["headerLinks"]!.AsArray();
        var link = Assert.Single(links)!;
        Assert.Equal("Facebook", link["label"]!.GetValue<string>());
        Assert.Equal("https://www.facebook.com/WesternMontanaSantaFlyover", link["href"]!.GetValue<string>());
        Assert.Equal("library", link["icon"]!["source"]!.GetValue<string>());
        Assert.Equal("facebook", link["icon"]!["id"]!.GetValue<string>());
        Assert.True(link["newTab"]!.GetValue<bool>());

        Assert.Single(StarterContentBuilder.Build().Settings.HeaderLinks!);
    }

    // The published document's settings: the stored settings read into the DTO and
    // written back with the canonical options, as the document builder does.
    private static JsonObject PublishedSettings(JsonObject stored)
    {
        var dto = stored.Deserialize<SiteSettings>(CanonicalJson.Options)!;
        return JsonNode.Parse(JsonSerializer.Serialize(dto, CanonicalJson.Options))!.AsObject();
    }

    private static JsonArray Links(int count)
    {
        var links = new JsonArray();
        for (var i = 1; i <= count; i++)
        {
            links.Add(new JsonObject
            {
                ["label"] = $"Link {i}",
                ["href"] = $"https://example.com/{i}",
                ["icon"] = new JsonObject { ["source"] = "library", ["id"] = "facebook" },
                ["newTab"] = true,
            });
        }
        return links;
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
