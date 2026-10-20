namespace Wmsfo.Api.Contracts.Dtos;

// POST /contact (contracts 4.3).
public sealed class ContactRequest
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Message { get; set; } = "";
}

// 202: the time the inbox object was written; the row does not exist yet.
public sealed class ContactResponse
{
    public DateTimeOffset ReceivedAt { get; set; }
}

// POST /subscriptions/verify (contracts 4.3).
public sealed class SubscriptionVerifyRequest
{
    public string Token { get; set; } = "";
}

public sealed class SubscriptionVerifyResponse
{
    public DateTimeOffset VerifiedAt { get; set; }
}

// POST /subscriptions/unsubscribe JSON body (contracts 4.3).
public sealed class SubscriptionUnsubscribeRequest
{
    public string Token { get; set; } = "";
}
