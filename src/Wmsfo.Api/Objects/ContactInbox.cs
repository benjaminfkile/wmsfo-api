using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wmsfo.Api.Objects;

// contracts 1.1 and 4.3: one contact form submission as `POST /contact` writes
// it to the bucket, the canonical JSON of this type, keys in this order. The
// leader's ContactInboxImporter reads it back into contact_message.
public sealed class ContactInboxObject
{
    [JsonPropertyOrder(0)] public string Name { get; set; } = "";
    [JsonPropertyOrder(1)] public string Email { get; set; } = "";
    [JsonPropertyOrder(2)] public string Message { get; set; } = "";
    [JsonPropertyOrder(3)] public string ClientIp { get; set; } = "";
    [JsonPropertyOrder(4)] public DateTimeOffset ReceivedAt { get; set; }
}

// The prefix, the key, the headers, and the field rules of the inbox objects,
// shared by the request that writes one and the chore that imports it.
public static class ContactInbox
{
    public const string Prefix = "contact-inbox/";
    public const string ContentType = "application/json";
    public const string CacheControl = "private, no-store";
    public const string WriteFailedCode = "contact_write_failed";

    // `contact-inbox/{receivedAt as yyyyMMddTHHmmssfffZ}-{16 random hex}.json`:
    // the time first, so the keys list in arrival order.
    public static string KeyFor(DateTimeOffset receivedAt)
    {
        var stamp = receivedAt.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);
        return Prefix + stamp + "-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) + ".json";
    }

    // The arrival time at the millisecond the canonical JSON carries, so the
    // value written and the value read back are equal.
    public static DateTimeOffset Now(DateTimeOffset utcNow)
    {
        var ticks = utcNow.UtcTicks - utcNow.UtcTicks % TimeSpan.TicksPerMillisecond;
        return new DateTimeOffset(ticks, TimeSpan.Zero);
    }

    public static bool ValidName(string name) => name.Length is >= 1 and <= 100;

    public static bool ValidEmail(string email) =>
        email.Length is >= 3 and <= 254 && LooksLikeEmail(email);

    public static bool ValidMessage(string message) => message.Length is >= 1 and <= 2000;

    // Parses an inbox object and checks the rules the request applied; null
    // when the bytes are not such an object.
    public static ContactInboxObject? TryParse(byte[] bytes)
    {
        ContactInboxObject? parsed;
        try
        {
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!TryString(root, "name", out var name)
                || !TryString(root, "email", out var email)
                || !TryString(root, "message", out var message)
                || !TryString(root, "clientIp", out var clientIp)
                || !TryString(root, "receivedAt", out var receivedAtText))
                return null;
            if (!DateTimeOffset.TryParse(receivedAtText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var receivedAt))
                return null;
            parsed = new ContactInboxObject
            {
                Name = name,
                Email = email,
                Message = message,
                ClientIp = clientIp,
                ReceivedAt = receivedAt,
            };
        }
        catch (JsonException)
        {
            return null;
        }
        if (!ValidName(parsed.Name) || !ValidEmail(parsed.Email) || !ValidMessage(parsed.Message)
            || parsed.ClientIp.Length > 100)
            return null;
        return parsed;
    }

    private static bool TryString(JsonElement root, string property, out string value)
    {
        value = "";
        if (!root.TryGetProperty(property, out var element) || element.ValueKind != JsonValueKind.String)
            return false;
        value = element.GetString() ?? "";
        return true;
    }

    private static bool LooksLikeEmail(string address)
    {
        var at = address.IndexOf('@');
        if (at <= 0 || at != address.LastIndexOf('@') || at == address.Length - 1) return false;
        var domain = address.AsSpan(at + 1);
        if (domain.IsEmpty || domain.IndexOf('.') < 0) return false;
        foreach (var c in address)
        {
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == ',' || c == ';') return false;
        }
        return true;
    }
}
