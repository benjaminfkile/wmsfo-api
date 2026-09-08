using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts;

// Generates draft 2020-12 JSON Schemas from the DTO types (contracts 13).
// Written next to the fixtures in contracts/schema/.
public static class SchemaExport
{
    private const string Draft2020_12 = "https://json-schema.org/draft/2020-12/schema";

    // A1g: System.Text.Json's WriteIndented would otherwise use the platform newline,
    // rewriting the LF-pinned checked-in files with CRLF on Windows. Utf8NoBom keeps
    // the byte stream free of a BOM as well.
    private static readonly JsonSerializerOptions IndentedLfOptions = new()
    {
        WriteIndented = true,
        NewLine = "\n",
    };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static IEnumerable<(string Name, string Json)> BuildAll()
    {
        yield return ("live-object", Build(typeof(LiveObject)));
        yield return ("snapshot", Build(typeof(Snapshot)));
        yield return ("route", Build(typeof(RouteObject)));
        yield return ("location", Build(typeof(LocationBody)));
        yield return ("heartbeat", Build(typeof(HeartbeatBody)));
        yield return ("realtime-authorize", Build(typeof(RealtimeAuthorizeRequest)));
        yield return ("realtime-message", Build(typeof(RealtimeMessageRequest)));
    }

    public static void WriteAll(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        foreach (var (name, json) in BuildAll())
        {
            File.WriteAllBytes(Path.Combine(outputDir, name + ".schema.json"), Utf8NoBom.GetBytes(json));
        }
    }

    private static string Build(Type type)
    {
        var exporterOptions = new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = static (context, schema) =>
            {
                // The RFC 3339 converter erases the type from the schema; re-attach it here.
                var t = context.TypeInfo.Type;
                if (t == typeof(ContentDocument))
                {
                    // The content document has its own hand-written schemas (contracts 1.3a);
                    // the snapshot schema keeps `content` as `true` (accept any valid document).
                    return JsonValue.Create(true);
                }
                if (t == typeof(DateTimeOffset))
                {
                    return new JsonObject { ["type"] = "string", ["format"] = "date-time" };
                }
                if (t == typeof(DateTimeOffset?))
                {
                    return new JsonObject
                    {
                        ["type"] = new JsonArray("string", "null"),
                        ["format"] = "date-time",
                    };
                }
                if (t == typeof(decimal))
                {
                    return new JsonObject { ["type"] = "number" };
                }
                if (t == typeof(decimal?))
                {
                    return new JsonObject { ["type"] = new JsonArray("number", "null") };
                }
                return schema;
            },
        };
        var node = CanonicalJson.Options.GetJsonSchemaAsNode(type, exporterOptions);
        JsonObject root = node as JsonObject ?? new JsonObject { ["$comment"] = node?.ToString() };
        // Prepend $schema in canonical order.
        var ordered = new JsonObject
        {
            ["$schema"] = Draft2020_12,
        };
        foreach (var kv in root.ToArray())
        {
            root.Remove(kv.Key);
            ordered[kv.Key] = kv.Value;
        }
        // Pretty-print for a stable checked-in file.
        return ordered.ToJsonString(IndentedLfOptions);
    }
}
