using System.Text.Json;

namespace Wmsfo.Api.Contracts;

// The panel-facing constants named in contracts 1.11 and 13. Kept next to the
// exporter so `contracts/admin-thresholds.json` regenerates from one source.
public static class AdminThresholds
{
    public const int BatteryLowPercent = 20;
    public const int NoFixAgeS = 30;
    public const int NoLocationAgeS = 30;

    public static string ToJson()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        return JsonSerializer.Serialize(new
        {
            batteryLowPercent = BatteryLowPercent,
            noFixAgeS = NoFixAgeS,
            noLocationAgeS = NoLocationAgeS,
        }, options);
    }
}
