using System.Text;
using System.Text.Json;

namespace Wmsfo.Api.Contracts;

// The panel-facing constants named in contracts 1.11 and 13. Kept next to the
// exporter so `contracts/admin-thresholds.json` regenerates from one source.
public static class AdminThresholds
{
    public const int BatteryLowPercent = 20;
    public const int NoFixAgeS = 30;
    public const int NoLocationAgeS = 30;

    // A1g: pinning NewLine to "\n" keeps WriteIndented from switching to CRLF on Windows,
    // which would rewrite the LF-pinned checked-in file and fail `git diff --exit-code`.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static string ToJson() => JsonSerializer.Serialize(new
    {
        batteryLowPercent = BatteryLowPercent,
        noFixAgeS = NoFixAgeS,
        noLocationAgeS = NoLocationAgeS,
    }, Options);

    public static byte[] ToUtf8Bytes() => Utf8NoBom.GetBytes(ToJson());

    public static void WriteTo(string path) => File.WriteAllBytes(path, ToUtf8Bytes());
}
