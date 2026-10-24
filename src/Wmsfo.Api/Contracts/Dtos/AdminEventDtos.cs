using System.Text.Json;
using System.Text.Json.Serialization;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts.Dtos;

// POST /admin/events (contracts 4.5 Events). Any other field is 400
// validation_failed.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateEventRequest
{
    public int Year { get; set; }
    public string Name { get; set; } = "";
    public DateTimeOffset? ScheduledAt { get; set; }
    public int FundsPercent { get; set; }
    // IANA zone id (e.g. America/Denver) the scheduled time was entered in.
    public string? ScheduleTimeZone { get; set; }
    // A Bbox sets the event's box; null or absent takes the published
    // settings.tracker.defaultBbox (or the Missoula valley box). A bare
    // JsonElement so the shape is checked against `$defs/Bbox`. With a map
    // named, absent takes the map's area and a given box must lie inside it.
    public JsonElement TrackerBbox { get; set; }
    // A map id names a ready map for the event, null means no map (Google
    // only), absent takes the map of the event with the greatest year.
    public JsonElement TrackerMapId { get; set; }
    // A `$defs/TrackerStartView` whose centre lies inside the event's box;
    // null or absent stores none (the tracker fits the box).
    public JsonElement TrackerStartView { get; set; }
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
    // An IANA zone id sets the zone, null clears it, absent leaves the row
    // unchanged. Deserialized as a bare JsonElement so JSON `null` yields
    // `ValueKind == Null` and absent yields `Undefined`.
    public JsonElement ScheduleTimeZone { get; set; }
    // A Bbox sets the event's box; null is refused (the box is required);
    // absent leaves it unchanged.
    public JsonElement TrackerBbox { get; set; }
    // A map id sets the event's map, null clears it, absent leaves it unchanged.
    public JsonElement TrackerMapId { get; set; }
    // A `$defs/TrackerStartView` inside the box after this write sets it, null
    // clears it, absent leaves it unchanged unless a new box leaves its
    // centre outside, which clears it.
    public JsonElement TrackerStartView { get; set; }
    // An array of theme ids replaces the enabled set whole; absent leaves it
    // unchanged.
    public JsonElement TrackerThemeIds { get; set; }
}

// POST /admin/events/{id}/status.
public sealed class ChangeEventStatusRequest
{
    public int StatusId { get; set; }
    public bool Notify { get; set; }
    // Optional 1..1000 text, trimmed, posted as an event message with notify
    // on or off; the alert email carries it in place of the stock paragraph.
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
    // A map id names a ready map for the clone, null means no map; absent
    // leaves the map to copy.tracker or the create rule.
    public JsonElement TrackerMapId { get; set; }
    // A Bbox inside the clone's map; absent takes the source's box, or the
    // named map's area when the source's box leaves it.
    public JsonElement TrackerBbox { get; set; }
    // A `$defs/TrackerStartView` inside the clone's box, or null for none;
    // absent copies the source's view while its centre lies inside the
    // clone's box, else none.
    public JsonElement TrackerStartView { get; set; }
}

// `copy` lists `sponsors` and `tracker`; any other key is 400
// validation_failed.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CloneEventCopy
{
    public bool Sponsors { get; set; }
    // The source's enabled theme set, and its map when the body names no
    // trackerMapId; without it they follow the create rule.
    public bool Tracker { get; set; }
}

// POST /admin/events/{id}/messages: `body` and `notify`; any other field
// is 400 validation_failed.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CreateEventMessageRequest
{
    public string Body { get; set; } = "";
    public bool Notify { get; set; }
}

// POST /admin/events/{id}/cookies: 1 to 50 entries, each type once, count 1 to 100.
public sealed class SeedCookiesRequest
{
    public List<CookiePick> Items { get; set; } = new();
}

// POST /admin/events/{id}/cookies answer: the event's whole tally after the
// insert, keys ascending, zero-count types absent.
public sealed class SeedCookiesResponse
{
    public long EventId { get; set; }
    public int Seeded { get; set; }
    public SortedDictionary<long, int> CookieTally { get; set; } = new();
}

// PATCH /admin/events/{id}/messages/{messageId}: `body` only; any other
// field is 400 validation_failed.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class PatchEventMessageRequest
{
    public string? Body { get; set; }
}

// GET /admin/routes/{id}/route-map: the route map (contracts 1.3) of the
// route; null when its object cannot be read.
public sealed class RouteMapResponse
{
    public RouteMap? RouteMap { get; set; }
}
