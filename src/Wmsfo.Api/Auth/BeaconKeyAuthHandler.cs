using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Auth;

// api.md 6.1: the X-Beacon-Key scheme. Header absent => NoResult (the endpoint's
// authorization policy answers 401 unauthenticated). Present but malformed or
// unknown => Fail with unauthenticated (401). On success the principal carries
// beacon_id, beacon_active, key_version claims. Beacons carry no role.
public sealed class BeaconAuthenticationOptions : AuthenticationSchemeOptions { }

public sealed partial class BeaconAuthenticationHandler : AuthenticationHandler<BeaconAuthenticationOptions>
{
    public const string HeaderName = "X-Beacon-Key";

    [GeneratedRegex("^wbk_[A-Za-z0-9_-]{43}$")]
    private static partial Regex KeyPattern();

    private readonly IBeaconKeyLookup _lookup;

    public BeaconAuthenticationHandler(
        IOptionsMonitor<BeaconAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IBeaconKeyLookup lookup)
        : base(options, logger, encoder)
    {
        _lookup = lookup;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var header) || StringValuesHelper.IsEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        var key = header.ToString();
        if (!KeyPattern().IsMatch(key))
        {
            return AuthenticateResult.Fail("beacon key format");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var row = await _lookup.FindByHashAsync(hash, Context.RequestAborted);
        if (row is null || row.RevokedAt is not null)
        {
            return AuthenticateResult.Fail("beacon key not found or revoked");
        }

        var identity = new ClaimsIdentity(AuthSchemes.BeaconKey);
        identity.AddClaim(new Claim(BeaconClaims.BeaconId, row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(BeaconClaims.BeaconActive, row.IsActive ? "true" : "false"));
        identity.AddClaim(new Claim(BeaconClaims.KeyVersion, row.KeyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthSchemes.BeaconKey);
        return AuthenticateResult.Success(ticket);
    }

    // api.md 6.1 step 2: fail-with-401 must produce the unified error shape.
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }

    public static long? TryGetBeaconId(ClaimsPrincipal? principal)
    {
        var claim = principal?.FindFirst(BeaconClaims.BeaconId)?.Value;
        return long.TryParse(claim, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}

// Small StringValues helper to keep the handler independent of Microsoft.Extensions.Primitives.
internal static class StringValuesHelper
{
    public static bool IsEmpty(Microsoft.Extensions.Primitives.StringValues values)
    {
        if (values.Count == 0) return true;
        for (var i = 0; i < values.Count; i++)
        {
            if (!string.IsNullOrEmpty(values[i])) return false;
        }
        return true;
    }
}

// Abstracts the SELECT by hash used by the handler; the real implementation
// runs the one-line query of api.md 6.1 step 3, and tests can inject fakes.
public interface IBeaconKeyLookup
{
    Task<BeaconAuthRow?> FindByHashAsync(byte[] hash, CancellationToken ct);
}

public sealed record BeaconAuthRow(long Id, bool IsActive, DateTimeOffset? RevokedAt, int KeyVersion);

// Runs on the beacon pool (sql.md 13) because it authenticates every
// `/beacons/*` request and `POST /locations`.
public sealed class DbBeaconKeyLookup : IBeaconKeyLookup
{
    private readonly WmsfoConnectionStrings _connections;

    public DbBeaconKeyLookup(WmsfoConnectionStrings connections)
    {
        _connections = connections;
    }

    public async Task<BeaconAuthRow?> FindByHashAsync(byte[] hash, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.Beacon);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select id, is_active, revoked_at, key_version from beacon where key_hash = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = hash });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new BeaconAuthRow(
            reader.GetInt64(0),
            reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetInt32(3));
    }
}
