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
    // A37: 'stored', 'carried', or 'dropped' (contracts 4.2, 7.2).
    public string Outcome { get; set; } = "";
}

// POST /beacons/heartbeat body (contracts 4.2). Exactly three keys:
// `sentAt` (required rfc3339), `health` (optional typed object or null), and
// `debug` (optional free-form object or null). Any other top-level key is
// `400 validation_failed`; any key inside `health` that is not one of the three
// typed leaves is also `400 validation_failed`. The stored jsonb is the exact
// bytes the beacon sent.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class HeartbeatBody
{
    public DateTimeOffset SentAt { get; set; }
    public HeartbeatHealth? Health { get; set; }
    public JsonElement? Debug { get; set; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class HeartbeatHealth
{
    public int? BatteryPercent { get; set; }
    public double? LastFixAgeS { get; set; }
    public string? SocketState { get; set; }
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
