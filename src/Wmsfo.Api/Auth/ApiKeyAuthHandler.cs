using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.Auth;

// api.md 6.4: the wak_ bearer scheme. Composite CognitoOrApiKey picks by prefix
// on the Authorization header; a bearer starting with wak_ is handled here,
// anything else goes to Cognito. Missing or non-Bearer: NoResult (the endpoint's
// authorization policy answers 401). Present, malformed, or unknown: Fail with
// unauthenticated (401). On success the principal carries api_key_id,
// api_key_name, api_key_all, and one api_key_capability claim per capability.
public sealed class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions { }

public sealed partial class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    [GeneratedRegex("^wak_[A-Za-z0-9_-]{43}$")]
    private static partial Regex KeyPattern();

    private readonly IApiKeyLookup _lookup;
    private readonly IApiKeyLastUsedStamp _stamp;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IApiKeyLookup lookup,
        IApiKeyLastUsedStamp stamp)
        : base(options, logger, encoder)
    {
        _lookup = lookup;
        _stamp = stamp;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header) || StringValuesHelper.IsEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }
        var raw = header.ToString();
        if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }
        var token = raw["Bearer ".Length..].Trim();
        // Only step in for wak_ prefixed tokens. Anything else is not our scheme,
        // regardless of format (the composite dispatcher already routed us here).
        if (!token.StartsWith(Keys.ApiKeyPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.NoResult();
        }
        if (!KeyPattern().IsMatch(token))
        {
            return AuthenticateResult.Fail("api key format");
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var row = await _lookup.FindByHashAsync(hash, Context.RequestAborted);
        if (row is null || row.RevokedAt is not null)
        {
            return AuthenticateResult.Fail("api key not found or revoked");
        }
        if (row.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            return AuthenticateResult.Fail("api key expired");
        }

        var identity = new ClaimsIdentity(AuthSchemes.ApiKey);
        identity.AddClaim(new Claim(ApiKeyClaims.ApiKeyId,
            row.Id.ToString(CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(ApiKeyClaims.ApiKeyName, row.Name));
        identity.AddClaim(new Claim(ApiKeyClaims.ApiKeyAll, row.AllCapabilities ? "true" : "false"));
        if (!row.AllCapabilities)
        {
            foreach (var cap in row.Capabilities)
            {
                identity.AddClaim(new Claim(ApiKeyClaims.ApiKeyCapability, cap));
            }
        }

        // Fire-and-forget last_used_at stamp; at most once per minute per key.
        _stamp.MaybeStamp(row.Id);

        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, AuthSchemes.ApiKey);
        return AuthenticateResult.Success(ticket);
    }

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

    public static long? TryGetApiKeyId(ClaimsPrincipal? principal)
    {
        var claim = principal?.FindFirst(ApiKeyClaims.ApiKeyId)?.Value;
        return long.TryParse(claim, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    public static string? TryGetApiKeyName(ClaimsPrincipal? principal) =>
        principal?.FindFirst(ApiKeyClaims.ApiKeyName)?.Value;

    public static bool IsApiKeyPrincipal(ClaimsPrincipal? principal) =>
        principal?.FindFirst(ApiKeyClaims.ApiKeyId) is not null;
}

// Abstracted so tests can seed keys without a database (mirrors IBeaconKeyLookup).
public interface IApiKeyLookup
{
    Task<ApiKeyAuthRow?> FindByHashAsync(byte[] hash, CancellationToken ct);
}

public sealed record ApiKeyAuthRow(
    long Id, string Name, bool AllCapabilities, IReadOnlyList<string> Capabilities,
    DateTimeOffset? ExpiresAt, DateTimeOffset? RevokedAt);

public sealed class DbApiKeyLookup : IApiKeyLookup
{
    private readonly IDbContextFactory<WmsfoDbContext> _factory;

    public DbApiKeyLookup(IDbContextFactory<WmsfoDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<ApiKeyAuthRow?> FindByHashAsync(byte[] hash, CancellationToken ct)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var row = await db.ApiKey.AsNoTracking()
            .Where(k => k.KeyHash == hash)
            .Select(k => new
            {
                k.Id, k.Name, k.AllCapabilities, k.Capabilities, k.ExpiresAt, k.RevokedAt,
            })
            .SingleOrDefaultAsync(ct);
        return row is null
            ? null
            : new ApiKeyAuthRow(row.Id, row.Name, row.AllCapabilities, row.Capabilities, row.ExpiresAt, row.RevokedAt);
    }
}

// api.md 6.4 step 3: last_used_at stamped fire-and-forget at most once a minute
// per key. The gate is process-local and best-effort; if two nodes stamp inside
// the same minute the row just gets stamped twice.
public interface IApiKeyLastUsedStamp
{
    void MaybeStamp(long apiKeyId);
}

public sealed class DbApiKeyLastUsedStamp : IApiKeyLastUsedStamp
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);

    private readonly WmsfoConnectionStrings _connections;
    private readonly ILogger<DbApiKeyLastUsedStamp> _logger;
    private readonly ConcurrentDictionary<long, DateTimeOffset> _lastStamped = new();

    public DbApiKeyLastUsedStamp(WmsfoConnectionStrings connections, ILogger<DbApiKeyLastUsedStamp> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    public void MaybeStamp(long apiKeyId)
    {
        var now = DateTimeOffset.UtcNow;
        if (_lastStamped.TryGetValue(apiKeyId, out var last) && now - last < MinimumInterval)
        {
            return;
        }
        _lastStamped[apiKeyId] = now;
        _ = Task.Run(async () =>
        {
            try
            {
                await using var conn = new NpgsqlConnection(_connections.App);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand(
                    "update api_key set last_used_at = now() where id = $1;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = apiKeyId });
                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "wmsfo_api_key_last_used_failed key={KeyId}", apiKeyId);
            }
        });
    }
}

// Test hook: never stamp.
public sealed class NoOpApiKeyLastUsedStamp : IApiKeyLastUsedStamp
{
    public void MaybeStamp(long apiKeyId) { }
}
