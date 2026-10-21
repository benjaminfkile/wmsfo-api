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

// GET /me/notifications response (contracts 4.4): the newest open subscriber
// row of the person, or null, and the person's seen marker, or null.
public sealed class MeNotificationsResponse
{
    public SubscriptionDto? Subscription { get; set; }
    public long? SeenUpToId { get; set; }
}

// PUT /me/notifications/seen request (contracts 4.4). seenUpToId is required
// and greater than 0; null here means the field was missing.
public sealed class MeNotificationsSeenRequest
{
    public long? SeenUpToId { get; set; }
}
