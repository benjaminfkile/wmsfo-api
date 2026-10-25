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
        yield return ("beacon-state", Build(typeof(BeaconStateBody)));
        yield return ("realtime-authorize", Build(typeof(RealtimeAuthorizeRequest)));
        yield return ("realtime-message", Build(typeof(RealtimeMessageRequest)));
        yield return ("qr-code", Build(typeof(QrCodeDto)));
        yield return ("qr-code-detail", Build(typeof(QrCodeDetailDto)));
        yield return ("place", Build(typeof(PlaceDto)));
        yield return ("place-pin", Build(typeof(PlacePinDto)));
        yield return ("delete-impact", Build(typeof(DeleteImpactDto)));
        yield return ("contact-inbox", Build(typeof(ContactInboxObject)));
        yield return ("me", Build(typeof(MeResponse)));
        yield return ("me-preferences", Build(typeof(PreferencesDto)));
        yield return ("me-seen", Build(typeof(MeSeenRequest)));
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
                // contracts 4.4: the seen body's id is nullable in the DTO only so a
                // missing value reaches the field-level 400; the schema states the
                // documented rule, required and at least 1.
                if (context.PropertyInfo?.DeclaringType == typeof(MeSeenRequest))
                {
                    return new JsonObject { ["type"] = "integer", ["minimum"] = 1 };
                }
                if (t == typeof(MeSeenRequest) && schema is JsonObject seen)
                {
                    seen["required"] = new JsonArray("seenUpToId");
                }
                // contracts 4.4: the preferences shape. theme is one of two
                // values or null and required with tracker; the tracker's keys
                // are optional and never null, mapType one of two values, theme
                // a tracker_theme.key slug.
                if (context.PropertyInfo?.DeclaringType == typeof(PreferencesDto)
                    && context.PropertyInfo.Name == "theme")
                {
                    return new JsonObject
                    {
                        ["type"] = new JsonArray("string", "null"),
                        ["enum"] = new JsonArray("light", "dark", null),
                    };
                }
                if (context.PropertyInfo?.DeclaringType == typeof(TrackerPreferencesDto))
                {
                    return context.PropertyInfo.Name switch
                    {
                        "mapType" => new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("terrain", "roadmap") },
                        "theme" => new JsonObject { ["type"] = "string", ["pattern"] = "^[a-z][a-z0-9-]{1,39}$" },
                        _ => new JsonObject { ["type"] = "boolean" },
                    };
                }
                // contracts 1.3: the tracker start view as `$defs/TrackerStartView`
                // states it, both keys required and the zoom 3 to 18.
                if (context.PropertyInfo?.DeclaringType == typeof(TrackerStartView)
                    && context.PropertyInfo.Name == "zoom")
                {
                    return new JsonObject { ["type"] = "integer", ["minimum"] = 3, ["maximum"] = 18 };
                }
                if (t == typeof(TrackerStartView) && schema is JsonObject startView)
                {
                    startView["required"] = new JsonArray("center", "zoom");
                }
                if (t == typeof(LatLng) && schema is JsonObject latLng)
                {
                    latLng["required"] = new JsonArray("lat", "lng");
                }
                if (t == typeof(PreferencesDto) && schema is JsonObject prefs)
                {
                    prefs["required"] = new JsonArray("theme", "tracker");
                }
                // contracts 1.3: the theme overlay's heading beam colour,
                // required and `#rrggbb` or `#rrggbbaa`.
                if (context.PropertyInfo?.DeclaringType == typeof(Overlay)
                    && context.PropertyInfo.Name == "headingColor")
                {
                    return new JsonObject { ["type"] = "string", ["pattern"] = "^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$" };
                }
                if (t == typeof(Overlay) && schema is JsonObject overlay)
                {
                    overlay["required"] = new JsonArray("headingColor");
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
