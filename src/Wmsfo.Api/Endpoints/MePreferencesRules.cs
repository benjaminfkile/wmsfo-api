using System.Text.Json;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Endpoints;

// Contracts 4.4 `PUT /me/preferences` body rules. The body is one JSON object
// with exactly two keys, both required: `theme`, "light", "dark", or null; and
// `tracker`, an object whose keys are all optional: the six toggles
// (`flightHistory`, `timeLabels`, `landmarks`, `flightDock`, `cookieTally`,
// `snow`), each a boolean, and `mapType`, "terrain" or "roadmap". Any other key
// at either level, a repeated key, or a value outside these is
// `400 validation_failed` with the field path (`theme`, `tracker.snow`,
// `tracker.<key>`). The answer is the validated object, which the endpoint
// re-serializes, so no unknown key is ever stored.
public static class MePreferencesRules
{
    public static readonly string[] Themes = ["light", "dark"];
    public static readonly string[] MapTypes = ["terrain", "roadmap"];

    public static PreferencesDto Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            RequestValidation.Throw("body", "must be an object");
        }

        var v = new RequestValidation();
        JsonElement? theme = null;
        JsonElement? tracker = null;
        foreach (var prop in root.EnumerateObject())
        {
            switch (prop.Name)
            {
                case "theme":
                    if (theme is not null) v.Field("theme", "appears more than once");
                    theme = prop.Value;
                    break;
                case "tracker":
                    if (tracker is not null) v.Field("tracker", "appears more than once");
                    tracker = prop.Value;
                    break;
                default:
                    v.Field(prop.Name, "is not allowed");
                    break;
            }
        }

        var dto = new PreferencesDto();
        if (theme is not JsonElement t)
        {
            v.Field("theme", "required");
        }
        else if (t.ValueKind == JsonValueKind.Null)
        {
            dto.Theme = null;
        }
        else if (t.ValueKind != JsonValueKind.String || !Themes.Contains(t.GetString(), StringComparer.Ordinal))
        {
            v.Field("theme", "must be \"light\", \"dark\", or null");
        }
        else
        {
            dto.Theme = t.GetString();
        }

        if (tracker is not JsonElement tr)
        {
            v.Field("tracker", "required");
        }
        else if (tr.ValueKind != JsonValueKind.Object)
        {
            v.Field("tracker", "must be an object");
        }
        else
        {
            ParseTracker(tr, dto.Tracker, v);
        }

        v.ThrowIfInvalid();
        return dto;
    }

    private static void ParseTracker(JsonElement tracker, TrackerPreferencesDto dto, RequestValidation v)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var prop in tracker.EnumerateObject())
        {
            var field = "tracker." + prop.Name;
            if (!seen.Add(prop.Name))
            {
                v.Field(field, "appears more than once");
                continue;
            }
            switch (prop.Name)
            {
                case "flightHistory": dto.FlightHistory = Toggle(prop.Value, field, v); break;
                case "timeLabels": dto.TimeLabels = Toggle(prop.Value, field, v); break;
                case "landmarks": dto.Landmarks = Toggle(prop.Value, field, v); break;
                case "flightDock": dto.FlightDock = Toggle(prop.Value, field, v); break;
                case "cookieTally": dto.CookieTally = Toggle(prop.Value, field, v); break;
                case "snow": dto.Snow = Toggle(prop.Value, field, v); break;
                case "mapType":
                    if (prop.Value.ValueKind == JsonValueKind.String
                        && MapTypes.Contains(prop.Value.GetString(), StringComparer.Ordinal))
                    {
                        dto.MapType = prop.Value.GetString();
                    }
                    else
                    {
                        v.Field(field, "must be \"terrain\" or \"roadmap\"");
                    }
                    break;
                default:
                    v.Field(field, "is not allowed");
                    break;
            }
        }
    }

    private static bool? Toggle(JsonElement value, string field, RequestValidation v)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True: return true;
            case JsonValueKind.False: return false;
            default:
                v.Field(field, "must be a boolean");
                return null;
        }
    }
}
