using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wmsfo.Api.Help;

namespace Wmsfo.Api.Tests;

// api.md 11a.9: the help topic seed loader accepts the committed
// help/topics.json and refuses each invalid shape with the entry named.
public class HelpTopicSeedTests
{
    private const string EmDash = "\u2014";
    private const string EnDash = "\u2013";

    [Fact]
    public void Loader_accepts_the_committed_file()
    {
        var seed = HelpTopicSeed.Load(ContractsPaths.RepoRoot);
        Assert.Equal(120, seed.Entries.Count);
        Assert.Equal("dashboard", seed.Entries[0].Key);
        Assert.All(seed.Entries, e => Assert.True(HelpTopicSeed.IsValidKey(e.Key)));
    }

    [Fact]
    public void ResolveRoot_finds_the_repo_root_from_below()
    {
        var below = Path.Combine(ContractsPaths.RepoRoot, "src", "Wmsfo.Api");
        Assert.Equal(Path.GetFullPath(ContractsPaths.RepoRoot), HelpTopicSeed.ResolveRoot(below));
    }

    [Fact]
    public void Help_keys_contract_regenerates_byte_identically()
    {
        var fresh = HelpTopicSeed.Load(ContractsPaths.RepoRoot).KeysToCanonicalJson();
        var onDisk = File.ReadAllBytes(Path.Combine(ContractsPaths.ContractsDir, "help-keys.json"));
        Assert.True(fresh.AsSpan().SequenceEqual(onDisk),
            "help-keys.json is out of date; run `dotnet run --project src/Wmsfo.Api -- export-contracts contracts`");
        using var doc = JsonDocument.Parse(onDisk);
        var first = doc.RootElement.EnumerateArray().First();
        Assert.Equal(new[] { "key", "page", "label" }, first.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void A_minimal_valid_seed_parses_and_trims()
    {
        var seed = HelpTopicSeed.Parse(Bytes(new JsonArray(Entry("events.list", title: "  Events  "))));
        var entry = Assert.Single(seed.Entries);
        Assert.Equal("Events", entry.Title);
        Assert.Equal("/events", Assert.Single(entry.Links).To);
    }

    [Fact]
    public void A_duplicate_key_is_refused()
    {
        var ex = Refused(new JsonArray(Entry("events"), Entry("events")));
        Assert.Contains("'events'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate key", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Events")]
    [InlineData("events..list")]
    [InlineData("events.")]
    [InlineData(".events")]
    [InlineData("events_list")]
    [InlineData("events list")]
    [InlineData("")]
    public void A_key_off_the_pattern_is_refused(string key)
    {
        var ex = Refused(new JsonArray(Entry(key)));
        Assert.Contains($"'{key}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("key must match", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("body")]
    public void An_empty_title_or_body_is_refused(string field)
    {
        var entry = Entry("events.empty");
        entry[field] = "   ";
        var ex = Refused(new JsonArray(entry));
        Assert.Contains("'events.empty'", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"{field} is empty", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ftp://x")]
    [InlineData("http://example.com")]
    [InlineData("events")]
    [InlineData("//example.com")]
    [InlineData("https://")]
    [InlineData("")]
    public void A_link_to_that_is_neither_a_path_nor_https_is_refused(string to)
    {
        var entry = Entry("events.link");
        entry["links"] = new JsonArray(Link("Go", to));
        var ex = Refused(new JsonArray(entry));
        Assert.Contains("'events.link'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("links[0].to", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_https_url_and_a_path_are_accepted()
    {
        var entry = Entry("events.link");
        entry["links"] = new JsonArray(Link("Site", "https://example.com/a?b=c"), Link("Events", "/events/1"));
        var seed = HelpTopicSeed.Parse(Bytes(new JsonArray(entry)));
        Assert.Equal(2, seed.Entries[0].Links.Count);
    }

    [Fact]
    public void More_than_six_links_is_refused()
    {
        var entry = Entry("events.many");
        entry["links"] = new JsonArray(Enumerable.Range(0, 7).Select(i => (JsonNode?)Link($"L{i}", $"/p{i}")).ToArray());
        var ex = Refused(new JsonArray(entry));
        Assert.Contains("'events.many'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("more than 6 links", ex.Message, StringComparison.Ordinal);

        var six = Entry("events.many");
        six["links"] = new JsonArray(Enumerable.Range(0, 6).Select(i => (JsonNode?)Link($"L{i}", $"/p{i}")).ToArray());
        Assert.Equal(6, HelpTopicSeed.Parse(Bytes(new JsonArray(six))).Entries[0].Links.Count);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("label")]
    [InlineData("title")]
    [InlineData("body")]
    [InlineData("linkLabel")]
    public void An_em_or_en_dash_anywhere_is_refused(string field)
    {
        foreach (var dash in new[] { EmDash, EnDash })
        {
            var entry = Entry("events.dash");
            if (field == "linkLabel") entry["links"] = new JsonArray(Link("Go " + dash + " now", "/events"));
            else entry[field] = "Text " + dash + " more";
            var ex = Refused(new JsonArray(entry));
            Assert.Contains("'events.dash'", ex.Message, StringComparison.Ordinal);
            Assert.Contains("em dash or an en dash", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Not_an_array_or_an_unknown_field_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => HelpTopicSeed.Parse(Encoding.UTF8.GetBytes("{}")));
        var entry = Entry("events.extra");
        entry["extra"] = 1;
        Assert.Throws<InvalidDataException>(() => HelpTopicSeed.Parse(Bytes(new JsonArray(entry))));
    }

    private static InvalidDataException Refused(JsonArray seed) =>
        Assert.Throws<InvalidDataException>(() => HelpTopicSeed.Parse(Bytes(seed)));

    private static byte[] Bytes(JsonArray seed) => Encoding.UTF8.GetBytes(seed.ToJsonString());

    private static JsonObject Entry(string key, string title = "Events") => new()
    {
        ["key"] = key,
        ["page"] = "Events",
        ["label"] = "Events list",
        ["title"] = title,
        ["body"] = "The list of events.",
        ["links"] = new JsonArray(Link("Events", "/events")),
    };

    private static JsonObject Link(string label, string to) => new() { ["label"] = label, ["to"] = to };
}
