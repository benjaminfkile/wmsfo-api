namespace Wmsfo.Api.Contracts.Dtos;

// GET /me (contracts 4.4).
public sealed class MeResponse
{
    public PersonDto Person { get; set; } = new();
    public bool IsAdmin { get; set; }
}

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
    public DateTimeOffset? HiddenAt { get; set; }
}

// POST /cookies (contracts 4.4).
public sealed class CreateCookieRequest
{
    public long CookieTypeId { get; set; }
    public string? Note { get; set; }
}

public sealed class CreateCookieResponse
{
    public long Id { get; set; }
    public long EventId { get; set; }
    public long CookieTypeId { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset LeftAt { get; set; }
    public int Remaining { get; set; }
}
