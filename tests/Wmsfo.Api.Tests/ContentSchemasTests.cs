using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts;

namespace Wmsfo.Api.Tests;

// A2 acceptance criteria (task 247):
//  - every kind in kinds.json has a schema file that compiles with JsonSchema.Net
//  - every `defaults`/`itemDefaults` is draft-valid
//  - the starter content and the content-document fixture validate at the publish level
//  - the draft derivation strips exactly required, minLength, minItems, minimum
//  - a `map` section's schema rejects an unknown theme key
public class ContentSchemasTests
{
    private static readonly KindRegistry Registry = KindRegistry.Load(ContractsPaths.ContractsDir);
    private static readonly SchemaValidator Validator = new(Registry);

    public static IEnumerable<object[]> AllKinds() =>
        Registry.Kinds.Select(k => new object[] { k.Kind });

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Kind_has_schema_file_and_compiles(string kind)
    {
        var info = Registry.ByName[kind];
        Assert.NotNull(info.Schema);
        if (info.HasItems)
        {
            Assert.NotNull(info.ItemSchema);
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Kind_defaults_are_draft_valid(string kind)
    {
        var info = Registry.ByName[kind];
        var problems = Validator.ValidateSectionData(kind, info.Defaults, ValidationLevel.Draft);
        Assert.True(problems.Count == 0,
            $"`{kind}` defaults are not draft-valid: " + string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}")));
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Kind_itemDefaults_are_draft_valid(string kind)
    {
        var info = Registry.ByName[kind];
        if (!info.HasItems)
        {
            Assert.Null(info.ItemDefaults);
            return;
        }
        Assert.NotNull(info.ItemDefaults);
        var problems = Validator.ValidateItemData(kind, info.ItemDefaults, ValidationLevel.Draft);
        Assert.True(problems.Count == 0,
            $"`{kind}` itemDefaults are not draft-valid: " + string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}")));
    }

    [Fact]
    public void Starter_content_validates_at_publish_level()
    {
        var starter = File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, "starter-content.json"));
        var node = JsonNode.Parse(starter);
        var docProblems = Validator.ValidateDocument(node, ValidationLevel.Publish);
        Assert.True(docProblems.Count == 0,
            "starter-content.json failed publish document schema: " + string.Join("; ", docProblems.Select(p => $"{p.Path}: {p.Message}")));

        var settings = node!["settings"]!;
        var settingsProblems = Validator.ValidateSiteSettings(settings, ValidationLevel.Publish);
        Assert.True(settingsProblems.Count == 0,
            "starter-content.json settings failed publish schema: " + string.Join("; ", settingsProblems.Select(p => $"{p.Path}: {p.Message}")));

        foreach (var page in node!["pages"]!.AsArray())
        {
            foreach (var section in page!["sections"]!.AsArray())
            {
                var kind = section!["kind"]!.GetValue<string>();
                var data = section["data"];
                var presentation = section["presentation"];

                var sectionProblems = Validator.ValidateSectionData(kind, data, ValidationLevel.Publish);
                Assert.True(sectionProblems.Count == 0,
                    $"starter section `{kind}` on `{page["slug"]}` failed publish validation: " +
                    string.Join("; ", sectionProblems.Select(p => $"{p.Path}: {p.Message}")));

                var presentationProblems = Validator.ValidatePresentation(presentation, ValidationLevel.Publish);
                Assert.True(presentationProblems.Count == 0,
                    $"starter section `{kind}` on `{page["slug"]}` presentation failed publish validation: " +
                    string.Join("; ", presentationProblems.Select(p => $"{p.Path}: {p.Message}")));
            }
        }
    }

    [Fact]
    public void Content_document_fixture_validates_at_publish_level()
    {
        var fixture = File.ReadAllText(Path.Combine(ContractsPaths.FixturesDir, "content-document.json"));
        var node = JsonNode.Parse(fixture);
        var problems = Validator.ValidateDocument(node, ValidationLevel.Publish);
        Assert.True(problems.Count == 0,
            "content-document.json failed publish schema: " + string.Join("; ", problems.Select(p => $"{p.Path}: {p.Message}")));
    }

    [Fact]
    public void Draft_derivation_strips_exactly_the_four_keywords()
    {
        var expected = new[] { "required", "minLength", "minItems", "minimum" };
        Assert.Equal(expected, SchemaValidator.StrippedKeywordsList.ToArray());

        // Prove that every one of these keywords disappears from the draft variant and no
        // other structural keyword does. The comparison is a keyword-set diff walked over the
        // publish and draft variants.
        var publishSchemas = new[]
        {
            (Name: "primitives", Node: Registry.PrimitivesNode),
            (Name: "site-settings", Node: Registry.SiteSettingsNode),
            (Name: "content-document", Node: Registry.ContentDocumentNode),
        }.Concat(Registry.Kinds.Select(k => (Name: k.Kind, Node: k.SchemaNode))).ToArray();

        foreach (var (name, publishNode) in publishSchemas)
        {
            var draftNode = publishNode.DeepClone();
            SchemaValidator.StripDraftKeywords(draftNode);

            var publishKeywords = CollectKeywords(publishNode);
            var draftKeywords = CollectKeywords(draftNode);
            var missing = publishKeywords.Except(draftKeywords).ToHashSet();

            foreach (var stripped in expected)
            {
                if (publishKeywords.Contains(stripped))
                {
                    Assert.DoesNotContain(stripped, draftKeywords);
                }
            }

            var otherRemoved = missing.Except(expected).ToArray();
            Assert.True(otherRemoved.Length == 0,
                $"draft derivation of `{name}` removed keywords other than the four: {string.Join(", ", otherRemoved)}");
        }
    }

    [Fact]
    public void Map_section_rejects_an_unknown_theme_key()
    {
        var mapInfo = Registry.ByName["map"];
        var badData = (JsonObject)mapInfo.Defaults.DeepClone();
        badData["themes"] = new JsonArray("standard", "quantum");
        var problems = Validator.ValidateSectionData("map", badData, ValidationLevel.Publish);
        Assert.True(problems.Count > 0, "map schema accepted an unknown theme key");

        var draftProblems = Validator.ValidateSectionData("map", badData, ValidationLevel.Draft);
        Assert.True(draftProblems.Count > 0, "draft-level map schema accepted an unknown theme key");
    }

    [Fact]
    public void Kinds_json_has_every_field_for_every_entry()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, "kinds.json")));
        var kinds = doc.RootElement.GetProperty("kinds");
        foreach (var entry in kinds.EnumerateArray())
        {
            Assert.True(entry.TryGetProperty("kind", out _), "missing kind");
            Assert.True(entry.TryGetProperty("title", out _), "missing title");
            Assert.True(entry.TryGetProperty("description", out _), "missing description");
            Assert.True(entry.TryGetProperty("live", out _), "missing live");
            Assert.True(entry.TryGetProperty("hasItems", out _), "missing hasItems");
            Assert.True(entry.TryGetProperty("allowedRoles", out _), "missing allowedRoles");
            Assert.True(entry.TryGetProperty("defaults", out _), "missing defaults");
            Assert.True(entry.TryGetProperty("itemDefaults", out _), "missing itemDefaults");
        }
    }

    [Fact]
    public void Starter_content_references_only_library_icon_ids()
    {
        using var lib = JsonDocument.Parse(File.ReadAllText(Path.Combine(ContractsPaths.RepoRoot, "icons", "library.json")));
        var libraryIds = new HashSet<string>(lib.RootElement.GetProperty("icons").EnumerateArray()
            .Select(e => e.GetProperty("id").GetString()!));

        var starter = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, "starter-content.json")))!;
        var seen = new List<string>();
        Collect(starter, seen);

        foreach (var id in seen)
        {
            Assert.True(libraryIds.Contains(id), $"starter content references library icon `{id}` which is not in icons/library.json");
        }
        Assert.NotEmpty(seen);

        static void Collect(JsonNode? node, List<string> into)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj.TryGetPropertyValue("source", out var s)
                        && s is JsonValue sv && sv.TryGetValue<string>(out var sourceValue)
                        && sourceValue == "library"
                        && obj.TryGetPropertyValue("id", out var id)
                        && id is JsonValue iv && iv.TryGetValue<string>(out var idValue))
                    {
                        into.Add(idValue);
                    }
                    else
                    {
                        foreach (var kv in obj) Collect(kv.Value, into);
                    }
                    break;
                case JsonArray arr:
                    foreach (var el in arr) Collect(el, into);
                    break;
            }
        }
    }

    [Fact]
    public void Starter_content_references_no_media_assets()
    {
        var starter = JsonNode.Parse(File.ReadAllText(Path.Combine(ContractsPaths.ContractsDir, "starter-content.json")))!;
        var mediaSources = new List<string>();
        Collect(starter, mediaSources);
        Assert.Empty(mediaSources);

        static void Collect(JsonNode? node, List<string> into)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj.TryGetPropertyValue("mediaId", out var mediaId) && mediaId is not null)
                    {
                        into.Add(mediaId.ToString());
                    }
                    if (obj.TryGetPropertyValue("source", out var src)
                        && src is JsonValue sv && sv.TryGetValue<string>(out var sourceValue)
                        && sourceValue == "media")
                    {
                        into.Add("media-icon");
                    }
                    foreach (var kv in obj) Collect(kv.Value, into);
                    break;
                case JsonArray arr:
                    foreach (var el in arr) Collect(el, into);
                    break;
            }
        }
    }

    [Fact]
    public void Snapshot_fixture_content_matches_starter_content_bytes()
    {
        var starter = File.ReadAllBytes(Path.Combine(ContractsPaths.ContractsDir, "starter-content.json"));
        var contentFixture = File.ReadAllBytes(Path.Combine(ContractsPaths.FixturesDir, "content-document.json"));
        Assert.Equal(starter, contentFixture);

        // The snapshot fixture's `content` key must carry the same bytes.
        using var snapshotDoc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContractsPaths.FixturesDir, "snapshot.json")));
        var content = snapshotDoc.RootElement.GetProperty("content").GetRawText();
        var expected = System.Text.Encoding.UTF8.GetString(starter);
        Assert.Equal(expected, content);
    }

    [Fact]
    public void Only_map_has_a_non_null_allowedRoles()
    {
        foreach (var info in Registry.Kinds)
        {
            if (info.Kind == "map")
            {
                Assert.NotNull(info.AllowedRoles);
                Assert.Equal(new[] { "live" }, info.AllowedRoles!.ToArray());
            }
            else
            {
                Assert.Null(info.AllowedRoles);
            }
        }
    }

    private static HashSet<string> CollectKeywords(JsonNode? node)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        Walk(node, result);
        return result;

        static void Walk(JsonNode? n, HashSet<string> into)
        {
            switch (n)
            {
                case JsonObject obj:
                    foreach (var kv in obj)
                    {
                        into.Add(kv.Key);
                        Walk(kv.Value, into);
                    }
                    break;
                case JsonArray arr:
                    foreach (var el in arr) Walk(el, into);
                    break;
            }
        }
    }
}
