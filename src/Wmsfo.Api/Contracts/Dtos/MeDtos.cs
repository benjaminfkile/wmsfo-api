using System.Text.Json.Serialization;

namespace Wmsfo.Api.Contracts.Dtos;

// POST /me/subscriptions request (contracts 4.4). Strict.
public sealed class CreateSubscriptionRequest
{
    public string Channel { get; set; } = "";
    public string Address { get; set; } = "";
}

// GET /me/cookies response (contracts 4.4).
public sealed class MyCookiesResponse
{
    public long? EventId { get; set; }
    public int? EventStatusId { get; set; }
    public int Limit { get; set; }
    public int Used { get; set; }
    public int Remaining { get; set; }
    public IList<MyCookieItem> Items { get; set; } = new List<MyCookieItem>();
}

public sealed class MyCookieItem
{
    public long Id { get; set; }
    public long CookieTypeId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset LeftAt { get; set; }
}

// POST /cookies (contracts 4.4): the whole pick in one request, one entry per
// cookie type with a count, and one note for every cookie of the run.
public sealed class CreateCookieRequest
{
    public List<CookiePick> Items { get; set; } = new();
    public string? Note { get; set; }
}

public sealed class CookiePick
{
    public long CookieTypeId { get; set; }
    public int Count { get; set; }
}

public sealed class CreateCookieResponse
{
    public long EventId { get; set; }
    public int Left { get; set; }
    public int Remaining { get; set; }
    public List<CookieLeft> Cookies { get; set; } = new();
}

public sealed class CookieLeft
{
    public long Id { get; set; }
    public long CookieTypeId { get; set; }
    public DateTimeOffset LeftAt { get; set; }
}

// GET /me response (contracts 4.4), also answered by the GET /me/notifications
// alias: the newest open subscriber row of the person, or null, the person's
// seen marker, or null, and the stored preferences, or null.
public sealed class MeResponse
{
    public SubscriptionDto? Subscription { get; set; }
    public long? SeenUpToId { get; set; }
    public PreferencesDto? Preferences { get; set; }
}

// PUT /me/preferences body and the stored person.preferences document
// (contracts 4.4). Both keys are required; a null theme means follow the
// system. MePreferencesRules validates the raw body before this is built.
public sealed class PreferencesDto
{
    public string? Theme { get; set; }
    public TrackerPreferencesDto Tracker { get; set; } = new();
}

// The tracker menu choices (contracts 4.4). Every key is optional; an absent
// key is omitted on write, never written as null.
public sealed class TrackerPreferencesDto
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FlightHistory { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? TimeLabels { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Landmarks { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? FlightDock { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? CookieTally { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public bool? Snow { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MapType { get; set; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Theme { get; set; }
}

// PUT /me/seen request (contracts 4.4), also taken by the
// PUT /me/notifications/seen alias. seenUpToId is required and greater than 0;
// null here means the field was missing.
public sealed class MeSeenRequest
{
    public long? SeenUpToId { get; set; }
}
