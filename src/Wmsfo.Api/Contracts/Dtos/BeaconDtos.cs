using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wmsfo.Api.Contracts.Dtos;

// POST /beacons/enroll (contracts 4.2). Strict body.
public sealed class EnrollRequest
{
    public string Token { get; set; } = "";
}

public sealed class EnrollResponse
{
    public long BeaconId { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public string Key { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "";
    public string HubUrl { get; set; } = "";
    public string IngestChannel { get; set; } = "";
    public DateTimeOffset ServerTime { get; set; }
}

// GET /beacons/me (contracts 4.2).
public sealed class BeaconMeResponse
{
    public long BeaconId { get; set; }
    public string Name { get; set; } = "";
    public string Role { get; set; } = "";
    public bool IsActive { get; set; }
    public string ApiBaseUrl { get; set; } = "";
    public string HubUrl { get; set; } = "";
    public string IngestChannel { get; set; } = "";
    public long? LiveEventId { get; set; }
    public DateTimeOffset ServerTime { get; set; }
}

// POST /locations body (contracts 4.2).
public sealed class LocationBody
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public double? SpeedMps { get; set; }
    public double? AltitudeM { get; set; }
    public double? HeadingDeg { get; set; }
    public double? AccuracyM { get; set; }
}

public sealed class LocationResponse
{
    public long Seq { get; set; }
    public bool Published { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset ServerTime { get; set; }
}

// POST /beacons/heartbeat body (contracts 4.2).
// Top-level keys strict (7 exact); each nested group tolerates unknown keys and stores them (JsonExtensionData).
public sealed class HeartbeatBody
{
    public DateTimeOffset SentAt { get; set; }
    public HeartbeatPower? Power { get; set; }
    public HeartbeatRadio? Radio { get; set; }
    public HeartbeatGps? Gps { get; set; }
    public HeartbeatTransport? Transport { get; set; }
    public HeartbeatProcess? Process { get; set; }
    public HeartbeatIdentity? Identity { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class HeartbeatPower
{
    public int? BatteryPercent { get; set; }
    public bool? Charging { get; set; }
    public double? BatteryTempC { get; set; }
    public string? ThermalStatus { get; set; }
    [JsonExtensionData] public IDictionary<string, JsonElement>? Extras { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class HeartbeatRadio
{
    public string? NetworkType { get; set; }
    public int? SignalDbm { get; set; }
    public int? SignalLevel { get; set; }
    public bool? AirplaneMode { get; set; }
    public bool? Connected { get; set; }
    [JsonExtensionData] public IDictionary<string, JsonElement>? Extras { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class HeartbeatGps
{
    public string? Provider { get; set; }
    public int? SatellitesUsed { get; set; }
    public int? SatellitesInView { get; set; }
    public double? LastFixAccuracyM { get; set; }
    public int? LastFixAgeS { get; set; }
    public int? FixesLastMinute { get; set; }
    public HeartbeatGpsPermission? Permission { get; set; }
    [JsonExtensionData] public IDictionary<string, JsonElement>? Extras { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class HeartbeatGpsPermission
{
    public bool? Foreground { get; set; }
    public bool? Background { get; set; }
    public bool? Precise { get; set; }
    [JsonExtensionData] public IDictionary<string, JsonElement>? Extras { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class HeartbeatTransport
{
    public string? SocketState { get; set; }
    public int? ReconnectCount { get; set; }
    public int? HttpFallbackSeconds { get; set; }
    public int? LastReceiptLatencyMs { get; set; }
    public int? SendsFailedSinceBoot { get; set; }
    [JsonExtensionData] public IDictionary<string, JsonElement>? Extras { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class HeartbeatProcess
{
    public long? DeviceUptimeS { get; set; }
    public long? ServiceUptimeS { get; set; }
    public int? ServiceRestartCount { get; set; }
    public string? MemoryPressure { get; set; }
    public bool? BatteryOptimizationExempt { get; set; }
    public bool? NotificationPermission { get; set; }
    public bool? SystemApp { get; set; }
    public bool? RootAvailable { get; set; }
    [JsonExtensionData] public IDictionary<string, JsonElement>? Extras { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed class HeartbeatIdentity
{
    public string? DeviceModel { get; set; }
    public string? AndroidVersion { get; set; }
    public string? AppVersion { get; set; }
    public long? ClockSkewMs { get; set; }
    [JsonExtensionData] public IDictionary<string, JsonElement>? Extras { get; set; }
}

public sealed class HeartbeatResponse
{
    public DateTimeOffset ReceivedAt { get; set; }
    public long? LiveEventId { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset ServerTime { get; set; }
}

public sealed class BeaconLogResponse
{
    public long Id { get; set; }
    public int SizeBytes { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset ServerTime { get; set; }
}

public sealed class HealthResponse
{
    public string Status { get; set; } = "";
}
