using System.Text.Json;
using System.Text.Json.Serialization;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts.Dtos;

// POST /admin/events (contracts 4.5 Events).
public sealed class CreateEventRequest
{
    public int Year { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset? ScheduledAt { get; set; }
    public int FundsPercent { get; set; }
    public long? RouteId { get; set; }
    public bool InheritRoute { get; set; }
    // IANA zone id (e.g. America/Denver) the scheduled time was entered in.
    public string? ScheduleTimeZone { get; set; }
}

// PATCH /admin/events/{id} - any of the listed fields; any other field is
// 400 validation_failed.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PatchEventRequest
{
    public string? Name { get; set; }
    public int? Year { get; set; }
    // The three datetimes: an RFC 3339 timestamp sets the value, null
    // clears it, absent leaves it unchanged. Deserialized as bare
    // JsonElements so JSON `null` yields `ValueKind == Null` and absent
    // yields `Undefined` (the ScheduleTimeZone convention below).
    public JsonElement ScheduledAt { get; set; }
    public JsonElement WentLiveAt { get; set; }
    public JsonElement EndedAt { get; set; }
    public int? FundsPercent { get; set; }
    public long? RouteId { get; set; }
    // A string value sets the link (uuid) or clears it (empty string).
    // Null / absent leaves the current value unchanged.
    public string? RouteImageMediaId { get; set; }
    // An IANA zone id sets the zone, null clears it, absent leaves the row
    // unchanged. Deserialized as a bare JsonElement so JSON `null` yields
    // `ValueKind == Null` and absent yields `Undefined`.
    public JsonElement ScheduleTimeZone { get; set; }
    // A RouteMapConfig object sets the event's route map configuration, null
    // clears it, absent leaves the row unchanged (the same bare JsonElement
    // convention).
    public JsonElement RouteMapConfig { get; set; }
}

// POST /admin/events/{id}/status.
public sealed class ChangeEventStatusRequest
{
    public int StatusId { get; set; }
    public bool Notify { get; set; }
    // Optional 1..1000 custom paragraph riding in the alert email. Ignored when
    // notify is false; the outbox row and history row still carry it.
    public string? Message { get; set; }
}

// POST /admin/events/{id}/notify - re-announce the current status now.
public sealed class NotifyStatusRequest
{
    public string? Message { get; set; }
}

// POST /admin/events/{id}/clone.
public sealed class CloneEventRequest
{
    public int Year { get; set; }
    public string Name { get; set; } = "";
    public CloneEventCopy? Copy { get; set; }
}

public sealed class CloneEventCopy
{
    public bool Sponsors { get; set; }
    public bool Route { get; set; }
    public bool Poster { get; set; }
    public bool RouteMapConfig { get; set; }
}

// POST /admin/events/{id}/messages.
public sealed class CreateEventMessageRequest
{
    public string Body { get; set; } = "";
    public DateTimeOffset? EventTime { get; set; }
    public bool Notify { get; set; }
}

// PATCH /admin/events/{id}/messages/{messageId}.
public sealed class PatchEventMessageRequest
{
    public string? Body { get; set; }
    public DateTimeOffset? EventTime { get; set; }
}

// GET /admin/events/{id}/route-map and GET /admin/routes/{id}/route-map: the
// route map (contracts 1.3) of the event's linked recording or of the route
// itself; null when the event has no recording linked.
public sealed class RouteMapResponse
{
    public RouteMap? RouteMap { get; set; }
}
