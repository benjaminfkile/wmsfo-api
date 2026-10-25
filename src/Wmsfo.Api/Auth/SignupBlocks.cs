using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Npgsql;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Auth;

// Contracts 3.1 and 5, sql.md 3.12a: a `signup_block` row's pattern is an
// address (`jane@example.com`) or a domain (`*@example.com`), stored trimmed
// and lowercased, 3 to 254 characters. An address pattern matches by
// equality; a domain pattern matches when the part after the address's last
// `@` equals the domain. Nothing else is a wildcard: no `?`, no other `*`.
public static partial class SignupBlocks
{
    public const string KindAddress = "address";
    public const string KindDomain = "domain";
    public const int PatternMin = 3;
    public const int PatternMax = 254;
    public const int NoteMax = 300;

    public const string BlockedMessage = "this address cannot use the site";

    [GeneratedRegex(@"^[^\s@*]+@[^\s@*]+\.[^\s@*]+$", RegexOptions.CultureInvariant)]
    private static partial Regex AddressPattern();

    [GeneratedRegex(@"^\*@[^\s@*]+\.[^\s@*]+$", RegexOptions.CultureInvariant)]
    private static partial Regex DomainPattern();

    // The pattern trimmed and lowercased, the form every write stores.
    public static string Normalize(string? pattern) => (pattern ?? "").Trim().ToLowerInvariant();

    // "address" or "domain" for a normalized pattern inside the rules, else null.
    public static string? Kind(string normalized)
    {
        if (normalized.Length < PatternMin || normalized.Length > PatternMax) return null;
        if (DomainPattern().IsMatch(normalized)) return KindDomain;
        if (AddressPattern().IsMatch(normalized)) return KindAddress;
        return null;
    }

    // True when the trimmed, lowercased address matches any of the patterns. A
    // pattern outside the rules matches nothing.
    public static bool Matches(string email, IEnumerable<string> patterns)
    {
        var address = Normalize(email);
        if (address.Length == 0) return false;
        var at = address.LastIndexOf('@');
        var domain = at >= 0 ? address[(at + 1)..] : null;
        foreach (var raw in patterns)
        {
            var pattern = Normalize(raw);
            switch (Kind(pattern))
            {
                case KindAddress when string.Equals(pattern, address, StringComparison.Ordinal):
                    return true;
                case KindDomain when domain is not null
                    && string.Equals(pattern[2..], domain, StringComparison.Ordinal):
                    return true;
            }
        }
        return false;
    }

    // The SQL condition, over a person alias `p` and a pattern expression, that
    // holds when the person's address matches the pattern the way Matches does:
    // equality for an address, and for `*@d` the address ending in `@d` (the
    // domain holds no `@`, so that is the part after the last `@`).
    public static string PersonMatchesSql(string pattern) =>
        "(case when left(" + pattern + ", 2) = '*@' "
        + "then right(lower(p.email), char_length(" + pattern + ") - 1) = substr(" + pattern + ", 2) "
        + "else lower(p.email) = " + pattern + " end)";

    // Every stored pattern, one statement (tens of rows; no cache).
    public static async Task<List<string>> ReadPatternsAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var patterns = new List<string>();
        await using var cmd = new NpgsqlCommand("select pattern from signup_block;", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) patterns.Add(reader.GetString(0));
        return patterns;
    }

    // Contracts 4.4: a subscription address that matches a block is
    // `409 blocked`, checked after validation and before any write.
    public static async Task RequireSubscriptionAddressAllowedAsync(NpgsqlConnection conn, string address, CancellationToken ct)
    {
        if (Matches(address, await ReadPatternsAsync(conn, null, ct)))
        {
            throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.Blocked, BlockedMessage);
        }
    }
}
