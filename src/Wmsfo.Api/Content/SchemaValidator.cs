using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Content;

// SchemaValidator (api.md 11a.2): validates section data, item data, presentation, and
// site settings at either level. `Draft` uses the derived lenient schema (`required`,
// `minLength`, `minItems`, `minimum` stripped at every level); unknown properties are still
// rejected. `Publish` uses the full schema. Semantic checks (references, hrefs, anchors,
// map-on-live) live in `ReferenceChecker` (A13); this class does structural checks only.
//
// Both variants register their $ids on SchemaRegistry.Global so cross-file $refs resolve.
// The draft variant uses distinct URIs (a `-draft.schema.json` suffix on each file's $id
// and every $ref) so publish and draft evaluation stay independent.
public sealed class SchemaValidator
{
    private static readonly IReadOnlyList<string> StrippedKeywords = new[]
    {
        "required",
        "minLength",
        "minItems",
        "minimum",
    };

    private const string DraftSuffix = "-draft";

    private readonly KindRegistry _registry;
    private readonly Dictionary<string, JsonSchema> _draftSection = new(StringComparer.Ordinal);
    private readonly Dictionary<string, JsonSchema> _draftItem = new(StringComparer.Ordinal);
    private readonly JsonSchema _draftSiteSettings;
    private readonly JsonSchema _draftContentDocument;
    private readonly JsonSchema _draftPrimitives;
    private readonly JsonSchema _presentationPublish;
    private readonly JsonSchema _presentationDraft;

    public SchemaValidator(KindRegistry registry)
    {
        _registry = registry;

        // Publish schemas: register on the global registry so $refs resolve.
        SchemaRegistry.Global.Register(new Uri(KindRegistry.PrimitivesId), registry.Primitives);
        SchemaRegistry.Global.Register(new Uri(KindRegistry.SiteSettingsId), registry.SiteSettings);
        SchemaRegistry.Global.Register(new Uri(KindRegistry.ContentDocumentId), registry.ContentDocument);
        foreach (var info in registry.Kinds)
        {
            SchemaRegistry.Global.Register(new Uri(SectionId(info.Kind)), info.Schema);
            if (info.HasItems && info.ItemSchema is not null)
                SchemaRegistry.Global.Register(new Uri(ItemId(info.Kind)), info.ItemSchema);
        }

        // Draft schemas: strip the four keywords at every level and rewrite every $ref to
        // point at the draft URIs so the draft primitives are used.
        _draftPrimitives = BuildDraftVariant(registry.PrimitivesNode);
        _draftSiteSettings = BuildDraftVariant(registry.SiteSettingsNode);
        _draftContentDocument = BuildDraftVariant(registry.ContentDocumentNode);
        SchemaRegistry.Global.Register(new Uri(ToDraftUri(KindRegistry.PrimitivesId)), _draftPrimitives);
        SchemaRegistry.Global.Register(new Uri(ToDraftUri(KindRegistry.SiteSettingsId)), _draftSiteSettings);
        SchemaRegistry.Global.Register(new Uri(ToDraftUri(KindRegistry.ContentDocumentId)), _draftContentDocument);
        foreach (var info in registry.Kinds)
        {
            var draft = BuildDraftVariant(info.SchemaNode);
            _draftSection[info.Kind] = draft;
            SchemaRegistry.Global.Register(new Uri(ToDraftUri(SectionId(info.Kind))), draft);

            if (info.HasItems && info.ItemSchemaNode is not null)
            {
                var draftItem = BuildDraftVariant(info.ItemSchemaNode);
                _draftItem[info.Kind] = draftItem;
                SchemaRegistry.Global.Register(new Uri(ToDraftUri(ItemId(info.Kind))), draftItem);
            }
        }

        _presentationPublish = ExtractDef(registry.PrimitivesNode, "Presentation", isDraft: false);
        _presentationDraft = ExtractDef(registry.PrimitivesNode, "Presentation", isDraft: true);
    }

    public IReadOnlyList<ProblemDto> ValidateSectionData(string kind, JsonNode? data, ValidationLevel level)
    {
        var schema = level == ValidationLevel.Draft
            ? (_draftSection.TryGetValue(kind, out var d) ? d : null)
            : (_registry.ByName.TryGetValue(kind, out var info) ? info.Schema : null);
        if (schema is null) return new[] { new ProblemDto { Path = "", Message = $"unknown kind: {kind}" } };
        return Evaluate(schema, data);
    }

    public IReadOnlyList<ProblemDto> ValidateItemData(string kind, JsonNode? data, ValidationLevel level)
    {
        var schema = level == ValidationLevel.Draft
            ? (_draftItem.TryGetValue(kind, out var d) ? d : null)
            : (_registry.ByName.TryGetValue(kind, out var info) ? info.ItemSchema : null);
        if (schema is null)
            return new[] { new ProblemDto { Path = "", Message = $"kind `{kind}` has no items" } };
        return Evaluate(schema, data);
    }

    public IReadOnlyList<ProblemDto> ValidateSiteSettings(JsonNode? data, ValidationLevel level)
    {
        var schema = level == ValidationLevel.Draft ? _draftSiteSettings : _registry.SiteSettings;
        return Evaluate(schema, data);
    }

    public IReadOnlyList<ProblemDto> ValidatePresentation(JsonNode? data, ValidationLevel level)
    {
        var presentation = level == ValidationLevel.Draft ? _presentationDraft : _presentationPublish;
        return Evaluate(presentation, data);
    }

    public IReadOnlyList<ProblemDto> ValidateDocument(JsonNode? data, ValidationLevel level)
    {
        var schema = level == ValidationLevel.Draft ? _draftContentDocument : _registry.ContentDocument;
        return Evaluate(schema, data);
    }

    private static IReadOnlyList<ProblemDto> Evaluate(JsonSchema schema, JsonNode? data)
    {
        var options = new EvaluationOptions { OutputFormat = OutputFormat.List };
        var element = ToElement(data);
        var results = schema.Evaluate(element, options);
        if (results.IsValid) return Array.Empty<ProblemDto>();
        var problems = new List<ProblemDto>();
        AppendProblems(results, problems);
        return problems;
    }

    private static JsonElement ToElement(JsonNode? node)
    {
        if (node is null)
        {
            using var doc = JsonDocument.Parse("null");
            return doc.RootElement.Clone();
        }
        using var parsed = JsonDocument.Parse(node.ToJsonString());
        return parsed.RootElement.Clone();
    }

    private static void AppendProblems(EvaluationResults results, List<ProblemDto> into)
    {
        if (results.Errors is { Count: > 0 } errors)
        {
            foreach (var err in errors)
            {
                into.Add(new ProblemDto
                {
                    Path = results.InstanceLocation.ToString(),
                    Message = $"{err.Key}: {err.Value}",
                });
            }
        }
        var details = results.Details;
        if (details is not null)
        {
            foreach (var sub in details) AppendProblems(sub, into);
        }
    }

    // Derives the draft-level variant by stripping the four keywords and rewriting every $id
    // and $ref that names one of the contract schema files with the `-draft` suffix.
    public static JsonSchema BuildDraftVariant(JsonNode source)
    {
        var clone = source.DeepClone();
        StripDraftKeywords(clone);
        RewriteRefsToDraft(clone);
        return JsonSchema.FromText(clone.ToJsonString());
    }

    public static void StripDraftKeywords(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var keyword in StrippedKeywords)
                {
                    obj.Remove(keyword);
                }
                foreach (var kv in obj.ToArray())
                {
                    if (kv.Value is not null) StripDraftKeywords(kv.Value);
                }
                break;
            case JsonArray arr:
                foreach (var el in arr)
                {
                    if (el is not null) StripDraftKeywords(el);
                }
                break;
        }
    }

    private static void RewriteRefsToDraft(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.TryGetPropertyValue("$id", out var idNode) && idNode is JsonValue idValue && idValue.TryGetValue<string>(out var idString))
                {
                    obj["$id"] = ToDraftUri(idString);
                }
                if (obj.TryGetPropertyValue("$ref", out var refNode) && refNode is JsonValue refValue && refValue.TryGetValue<string>(out var refString))
                {
                    obj["$ref"] = RewriteRef(refString);
                }
                foreach (var kv in obj.ToArray())
                {
                    if (kv.Key == "$ref" || kv.Key == "$id") continue;
                    if (kv.Value is not null) RewriteRefsToDraft(kv.Value);
                }
                break;
            case JsonArray arr:
                foreach (var el in arr)
                {
                    if (el is not null) RewriteRefsToDraft(el);
                }
                break;
        }
    }

    private static string RewriteRef(string reference)
    {
        // Internal `#/$defs/...` refs stay local to the current (already-draft) schema.
        if (reference.StartsWith("#", StringComparison.Ordinal)) return reference;
        var hashIdx = reference.IndexOf('#');
        var baseUri = hashIdx < 0 ? reference : reference[..hashIdx];
        var fragment = hashIdx < 0 ? "" : reference[hashIdx..];
        return ToDraftUri(baseUri) + fragment;
    }

    private static string ToDraftUri(string absoluteUri)
    {
        const string schemaExt = ".schema.json";
        if (absoluteUri.EndsWith(schemaExt, StringComparison.Ordinal))
            return absoluteUri[..^schemaExt.Length] + DraftSuffix + schemaExt;
        return absoluteUri + DraftSuffix;
    }

    // Pulls a $def out of the primitives schema and builds a standalone JsonSchema for it.
    // Refs inside the def still point at the primitives $defs; when isDraft is true the
    // extracted subschema is stripped and re-rooted at the draft primitives URI.
    private static JsonSchema ExtractDef(JsonNode primitivesNode, string defName, bool isDraft)
    {
        var clone = primitivesNode.DeepClone();
        if (isDraft)
        {
            StripDraftKeywords(clone);
            RewriteRefsToDraft(clone);
        }
        if (clone is not JsonObject obj
            || !obj.TryGetPropertyValue("$defs", out var defs)
            || defs is not JsonObject defsObj
            || !defsObj.TryGetPropertyValue(defName, out var def)
            || def is null)
        {
            throw new KeyNotFoundException($"$defs/{defName} not found in primitives");
        }
        // Wrap in a document with a distinct $id so it does not collide with the primitives
        // schema in the global registry. Local $defs are copied along so internal
        // `#/$defs/...` refs still resolve inside the wrapper.
        var baseId = isDraft ? ToDraftUri(KindRegistry.PrimitivesId) : KindRegistry.PrimitivesId;
        var wrapperId = baseId.Replace(".schema.json", "-extract-" + defName + ".schema.json", StringComparison.Ordinal);
        var wrapper = new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["$id"] = wrapperId,
            ["$defs"] = defs.DeepClone(),
        };
        foreach (var kv in ((JsonObject)def).ToArray())
        {
            wrapper[kv.Key] = kv.Value?.DeepClone();
        }
        return JsonSchema.FromText(wrapper.ToJsonString());
    }

    private static string SectionId(string kind) => $"https://wmsfo.dev/schema/sections/{kind}.schema.json";
    private static string ItemId(string kind) => $"https://wmsfo.dev/schema/sections/{kind}.item.schema.json";

    public static IReadOnlyList<string> StrippedKeywordsList => StrippedKeywords;
}

public enum ValidationLevel
{
    Draft,
    Publish,
}
