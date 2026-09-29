using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Content;

// An event's route map configuration (contracts 1.3, 4.5 Events): the write
// check against `$defs/RouteMapConfig`, the stored form, and the media ids its
// landmark icons reference.
public static class RouteMapConfigRules
{
    // Checks a written value against the schema and the icon library and
    // returns the typed configuration; field problems go to `v` under `field`
    // (`routeMapConfig.landmarks[0].name`) and the result is then null.
    public static RouteMapConfig? Read(
        JsonElement value, string field, SchemaValidator validator, IconLibrary icons, RequestValidation v)
    {
        var problems = validator.ValidateRouteMapConfig(JsonNode.Parse(value.GetRawText()));
        if (problems.Count > 0)
        {
            foreach (var problem in problems) v.Field(FieldPath(field, problem.Path), problem.Message);
            return null;
        }
        var config = JsonSerializer.Deserialize<RouteMapConfig>(value.GetRawText(), CanonicalJson.Options)!;
        var ok = true;
        for (var i = 0; i < (config.Landmarks?.Count ?? 0); i++)
        {
            var icon = config.Landmarks![i].Icon;
            if (icon is { Source: "library" } && !icons.Contains(icon.Id))
            {
                v.Field($"{field}.landmarks[{i}].icon.id", "is not a known library icon id");
                ok = false;
            }
        }
        return ok ? config : null;
    }

    // The stored and published form: the canonical serialization, keys in
    // contract order and absent when unset.
    public static string Serialize(RouteMapConfig config) =>
        JsonSerializer.Serialize(config, CanonicalJson.Options);

    public static RouteMapConfig? FromStored(string json) =>
        JsonSerializer.Deserialize<RouteMapConfig>(json, CanonicalJson.Options);

    // The media asset ids of the landmark icons whose source is "media".
    public static IEnumerable<Guid> MediaIds(RouteMapConfig config)
    {
        foreach (var landmark in config.Landmarks ?? Array.Empty<RouteMapLandmark>())
        {
            if (landmark.Icon is { Source: "media" } icon && Guid.TryParse(icon.Id, out var id)) yield return id;
        }
    }

    // A schema instance location (`/landmarks/0/name`) as a request field path.
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
