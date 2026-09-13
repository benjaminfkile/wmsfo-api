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
}

// PATCH /admin/events/{id} - any of the listed fields.
public sealed class PatchEventRequest
{
    public string? Name { get; set; }
    public int? Year { get; set; }
    public DateTimeOffset? ScheduledAt { get; set; }
    public DateTimeOffset? WentLiveAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public int? FundsPercent { get; set; }
    public long? RouteId { get; set; }
    // A string value sets the link (uuid) or clears it (empty string).
    // Null / absent leaves the current value unchanged.
    public string? RouteImageMediaId { get; set; }
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
