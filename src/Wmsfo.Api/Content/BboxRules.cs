using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wmsfo.Api.Content;

// The writer's limits on a tracker box (contracts 1.3 event.trackerBbox,
// 1.3a settings.tracker.defaultBbox): west < east, south < north, and each
// side at least 0.05 and at most 20 degrees. The shape itself (four numbers,
// no other key) is `$defs/Bbox`.
public static class BboxRules
{
    public const double MinSideDegrees = 0.05;
    public const double MaxSideDegrees = 20;

    // The problem with a box whose four keys are numbers, or null when the box
    // keeps the limits or its shape is the schema's to refuse.
    public static string? Problem(JsonNode? box)
    {
        if (box is not JsonObject o) return null;
        if (!TryNumber(o, "west", out var west) || !TryNumber(o, "south", out var south)
            || !TryNumber(o, "east", out var east) || !TryNumber(o, "north", out var north))
            return null;
        if (!(west < east)) return "west must be less than east";
        if (!(south < north)) return "south must be less than north";
        var width = east - west;
        var height = north - south;
        if (width < MinSideDegrees || height < MinSideDegrees)
            return "each side must be at least 0.05 degrees";
        if (width > MaxSideDegrees || height > MaxSideDegrees)
            return "each side must be at most 20 degrees";
        return null;
    }

    private static bool TryNumber(JsonObject o, string key, out double value)
    {
        value = 0;
        return o[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number
            && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
