using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Content;

// An event's route map configuration (contracts 1.3, 4.5 Events): the write
// check against `$defs/RouteMapConfig` and the stored form.
public static class RouteMapConfigRules
{
    // Checks a written value against the schema and returns the typed
    // configuration; field problems go to `v` under `field`
    // (`routeMapConfig.display.arrowSize`) and the result is then null.
    public static RouteMapConfig? Read(
        JsonElement value, string field, SchemaValidator validator, RequestValidation v)
    {
        var problems = validator.ValidateRouteMapConfig(JsonNode.Parse(value.GetRawText()));
        if (problems.Count > 0)
        {
            foreach (var problem in problems) v.Field(FieldPath(field, problem.Path), problem.Message);
            return null;
        }
        return JsonSerializer.Deserialize<RouteMapConfig>(value.GetRawText(), CanonicalJson.Options)!;
    }

    // The stored and published form: the canonical serialization, keys in
    // contract order and absent when unset.
    public static string Serialize(RouteMapConfig config) =>
        JsonSerializer.Serialize(config, CanonicalJson.Options);

    public static RouteMapConfig? FromStored(string json) =>
        JsonSerializer.Deserialize<RouteMapConfig>(json, CanonicalJson.Options);

    // A schema instance location (`/pois/kinds/1`) as a request field path.
    private static string FieldPath(string field, string pointer)
    {
        var path = new StringBuilder(field);
        foreach (var segment in pointer.TrimStart('#').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                path.Append('[').Append(index.ToString(CultureInfo.InvariantCulture)).Append(']');
            else
                path.Append('.').Append(name);
        }
        return path.ToString();
    }
}
