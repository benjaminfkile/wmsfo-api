using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace Wmsfo.Api.Content;

// KindRegistry (api.md 11a.1): loads contracts/kinds.json plus every hand-written schema
// under contracts/schema/. `Schema` and `ItemSchema` are the compiled publish-level schemas
// (JsonSchema.Net) whose `$ref` values resolve against the primitives, site-settings, and
// content-document schemas registered on `Registry`. The KindInfo the panel receives
// (`GET /admin/content/kinds`) inlines the referenced primitives under `$defs`.
public sealed class KindRegistry
{
    public const string PrimitivesId = "https://wmsfo.dev/schema/primitives.schema.json";
    public const string SiteSettingsId = "https://wmsfo.dev/schema/site-settings.schema.json";
    public const string ContentDocumentId = "https://wmsfo.dev/schema/content-document.schema.json";

    public JsonSchema Primitives { get; }
    public JsonNode PrimitivesNode { get; }
    public JsonSchema SiteSettings { get; }
    public JsonNode SiteSettingsNode { get; }
    public JsonSchema ContentDocument { get; }
    public JsonNode ContentDocumentNode { get; }
    public IReadOnlyList<KindInfo> Kinds { get; }
    public IReadOnlyDictionary<string, KindInfo> ByName { get; }

    private KindRegistry(
        JsonSchema primitives, JsonNode primitivesNode,
        JsonSchema siteSettings, JsonNode siteSettingsNode,
        JsonSchema contentDocument, JsonNode contentDocumentNode,
        IReadOnlyList<KindInfo> kinds)
    {
        Primitives = primitives;
        PrimitivesNode = primitivesNode;
        SiteSettings = siteSettings;
        SiteSettingsNode = siteSettingsNode;
        ContentDocument = contentDocument;
        ContentDocumentNode = contentDocumentNode;
        Kinds = kinds;
        ByName = kinds.ToDictionary(k => k.Kind, StringComparer.Ordinal);
    }

    public static KindRegistry Load(string contractsDir)
    {
        ArgumentNullException.ThrowIfNull(contractsDir);
        if (!Directory.Exists(contractsDir))
            throw new DirectoryNotFoundException($"contracts directory not found: {contractsDir}");

        var schemaDir = Path.Combine(contractsDir, "schema");
        var sectionsDir = Path.Combine(schemaDir, "sections");

        var primitivesText = File.ReadAllText(Path.Combine(schemaDir, "primitives.schema.json"));
        var siteSettingsText = File.ReadAllText(Path.Combine(schemaDir, "site-settings.schema.json"));
        var contentDocumentText = File.ReadAllText(Path.Combine(schemaDir, "content-document.schema.json"));

        var primitivesNode = JsonNode.Parse(primitivesText) ?? throw new InvalidDataException("primitives.schema.json is empty");
        var siteSettingsNode = JsonNode.Parse(siteSettingsText) ?? throw new InvalidDataException("site-settings.schema.json is empty");
        var contentDocumentNode = JsonNode.Parse(contentDocumentText) ?? throw new InvalidDataException("content-document.schema.json is empty");

        var primitives = JsonSchema.FromText(primitivesText);
        var siteSettings = JsonSchema.FromText(siteSettingsText);
        var contentDocument = JsonSchema.FromText(contentDocumentText);

        // Load kinds.json.
        using var kindsDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(contractsDir, "kinds.json")));
        if (!kindsDoc.RootElement.TryGetProperty("kinds", out var kindsArray) || kindsArray.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("kinds.json missing `kinds` array");

        var kinds = new List<KindInfo>(kindsArray.GetArrayLength());
        foreach (var entry in kindsArray.EnumerateArray())
        {
            var kind = entry.GetProperty("kind").GetString()
                ?? throw new InvalidDataException("kinds.json entry missing `kind`");
            var title = entry.GetProperty("title").GetString()
                ?? throw new InvalidDataException($"kinds.json entry `{kind}` missing `title`");
            var description = entry.GetProperty("description").GetString()
                ?? throw new InvalidDataException($"kinds.json entry `{kind}` missing `description`");
            var live = entry.GetProperty("live").GetBoolean();
            var hasItems = entry.GetProperty("hasItems").GetBoolean();

            IReadOnlyList<string>? allowedRoles = null;
            var allowedNode = entry.GetProperty("allowedRoles");
            if (allowedNode.ValueKind == JsonValueKind.Array)
            {
                var roles = new List<string>();
                foreach (var r in allowedNode.EnumerateArray())
                    roles.Add(r.GetString() ?? throw new InvalidDataException($"kinds.json `{kind}` has a null role"));
                allowedRoles = roles;
            }
            else if (allowedNode.ValueKind != JsonValueKind.Null)
            {
                throw new InvalidDataException($"kinds.json `{kind}` allowedRoles must be an array or null");
            }

            var schemaPath = Path.Combine(sectionsDir, kind + ".schema.json");
            if (!File.Exists(schemaPath))
                throw new FileNotFoundException($"section schema missing for `{kind}`: {schemaPath}");
            var schemaText = File.ReadAllText(schemaPath);
            var schema = JsonSchema.FromText(schemaText);
            var schemaNode = JsonNode.Parse(schemaText) ?? throw new InvalidDataException($"section schema empty: {schemaPath}");

            JsonSchema? itemSchema = null;
            JsonNode? itemSchemaNode = null;
            if (hasItems)
            {
                var itemPath = Path.Combine(sectionsDir, kind + ".item.schema.json");
                if (!File.Exists(itemPath))
                    throw new FileNotFoundException($"item schema missing for `{kind}`: {itemPath}");
                var itemText = File.ReadAllText(itemPath);
                itemSchema = JsonSchema.FromText(itemText);
                itemSchemaNode = JsonNode.Parse(itemText) ?? throw new InvalidDataException($"item schema empty: {itemPath}");
            }

            var defaults = entry.GetProperty("defaults").Deserialize<JsonNode>()
                ?? throw new InvalidDataException($"kinds.json `{kind}` defaults must be non-null");
            JsonNode? itemDefaults = null;
            if (entry.TryGetProperty("itemDefaults", out var itemDefaultsElement) && itemDefaultsElement.ValueKind != JsonValueKind.Null)
            {
                itemDefaults = itemDefaultsElement.Deserialize<JsonNode>();
            }

            var info = new KindInfo(
                kind, title, description, live, hasItems, allowedRoles,
                schema, schemaNode, itemSchema, itemSchemaNode,
                defaults, itemDefaults);

            kinds.Add(info);
        }

        return new KindRegistry(
            primitives, primitivesNode,
            siteSettings, siteSettingsNode,
            contentDocument, contentDocumentNode,
            kinds);
    }

    // Build the KindInfo JSON the panel receives: the section schema with primitives inlined
    // under `$defs`, and the item schema (when the kind has items) treated the same way.
    public JsonNode BuildKindInfoJson(string kind)
    {
        if (!ByName.TryGetValue(kind, out var info))
            throw new KeyNotFoundException(kind);
        var payload = new JsonObject
        {
            ["kind"] = info.Kind,
            ["title"] = info.Title,
            ["description"] = info.Description,
            ["live"] = info.Live,
            ["hasItems"] = info.HasItems,
            ["allowedRoles"] = info.AllowedRoles is null
                ? null
                : new JsonArray(info.AllowedRoles.Select(r => (JsonNode?)r).ToArray()),
            ["schema"] = InlinePrimitives(info.SchemaNode),
            ["itemSchema"] = info.ItemSchemaNode is null ? null : InlinePrimitives(info.ItemSchemaNode),
            ["defaults"] = info.Defaults?.DeepClone(),
            ["itemDefaults"] = info.ItemDefaults?.DeepClone(),
        };
        return payload;
    }

    private JsonNode InlinePrimitives(JsonNode section)
    {
        var clone = section.DeepClone();
        var defs = new JsonObject();
        foreach (var kv in PrimitivesDefs())
            defs[kv.Key] = kv.Value.DeepClone();
        if (clone is JsonObject obj)
        {
            obj["$defs"] = defs;
        }
        return clone;
    }

    private IEnumerable<KeyValuePair<string, JsonNode>> PrimitivesDefs()
    {
        if (PrimitivesNode is JsonObject root
            && root.TryGetPropertyValue("$defs", out var defsNode)
            && defsNode is JsonObject defs)
        {
            foreach (var kv in defs)
            {
                if (kv.Value is null) continue;
                yield return new KeyValuePair<string, JsonNode>(kv.Key, kv.Value);
            }
        }
    }
}

public sealed record KindInfo(
    string Kind,
    string Title,
    string Description,
    bool Live,
    bool HasItems,
    IReadOnlyList<string>? AllowedRoles,
    JsonSchema Schema,
    JsonNode SchemaNode,
    JsonSchema? ItemSchema,
    JsonNode? ItemSchemaNode,
    JsonNode Defaults,
    JsonNode? ItemDefaults);
