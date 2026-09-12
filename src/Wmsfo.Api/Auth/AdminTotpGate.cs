using System.Collections.Concurrent;
using Amazon.CognitoIdentityProvider;
using Amazon.CognitoIdentityProvider.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Auth;

// api.md 6.3: endpoint filter on the Editor and Admin policies. Calls Cognito
// AdminGetUser for the token's sub, cached 5 minutes per user; answers
// 403 mfa_required unless SOFTWARE_TOKEN_MFA is enabled; an enrolled answer is
// cached 5 minutes, a not-enrolled answer never. A Cognito failure is treated
// as not enabled and logged at Warning.
public sealed class AdminTotpGate
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    public const string SoftwareTokenMfa = "SOFTWARE_TOKEN_MFA";

    private readonly IAdminTotpChecker _checker;
    private readonly ILogger<AdminTotpGate> _logger;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public AdminTotpGate(IAdminTotpChecker checker, ILogger<AdminTotpGate> logger, TimeProvider? time = null)
    {
        _checker = checker;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public async ValueTask<object?> Filter(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        // api.md 6.4: the TOTP gate does not apply to API key principals.
        if (ApiKeyAuthenticationHandler.IsApiKeyPrincipal(ctx.HttpContext.User))
        {
            return await next(ctx);
        }
        var sub = ctx.HttpContext.User.FindFirst(PersonClaims.Sub)?.Value;
        if (string.IsNullOrEmpty(sub))
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.MfaRequired, "TOTP not enabled");
        }
        var enabled = await IsMfaEnabledAsync(sub, ctx.HttpContext.RequestAborted);
        if (!enabled)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.MfaRequired, "TOTP not enabled");
        }
        return await next(ctx);
    }

    private async Task<bool> IsMfaEnabledAsync(string sub, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        if (_cache.TryGetValue(sub, out var entry) && entry.ExpiresAt > now)
        {
            return entry.Enabled;
        }
        bool enabled;
        try
        {
            enabled = await _checker.HasSoftwareTokenMfaAsync(sub, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "wmsfo_admin_totp_gate_check_failed sub={Sub}", sub);
            enabled = false;
        }
        // Only an enrolled answer is remembered: the admin enrols on the panel's
        // setup page and is admitted on the very next request.
        if (enabled)
        {
            _cache[sub] = new CacheEntry(true, now + CacheTtl);
        }
        else
        {
            _cache.TryRemove(sub, out _);
        }
        return enabled;
    }

    // Test hook: preload the cache without hitting Cognito.
    public void SetCached(string sub, bool enabled)
    {
        _cache[sub] = new CacheEntry(enabled, _time.GetUtcNow() + CacheTtl);
    }

    // Test hook: drop cached decisions so a scripted checker's next answer is
    // observed. Production paths never need this (five minutes is the point).
    public void ClearCache() => _cache.Clear();

    private sealed record CacheEntry(bool Enabled, DateTimeOffset ExpiresAt);
}

public interface IAdminTotpChecker
{
    Task<bool> HasSoftwareTokenMfaAsync(string sub, CancellationToken ct);
}

// api.md 6.3: cognito-idp:AdminGetUser on the instance role; the admin pool id
// (WMSFO_COGNITO_ADMIN_USER_POOL_ID, or the people pool's in single-pool mode)
// in the secret.
public sealed class CognitoAdminTotpChecker : IAdminTotpChecker
{
    private readonly IAmazonCognitoIdentityProvider _cognito;
    private readonly WmsfoOptions _options;

    public CognitoAdminTotpChecker(IAmazonCognitoIdentityProvider cognito, WmsfoOptions options)
    {
        _cognito = cognito;
        _options = options;
    }

    public async Task<bool> HasSoftwareTokenMfaAsync(string sub, CancellationToken ct)
    {
        var response = await _cognito.AdminGetUserAsync(new AdminGetUserRequest
        {
            UserPoolId = _options.EffectiveAdminUserPoolId,
            Username = sub,
        }, ct);
        return response.UserMFASettingList is { } list
               && list.Any(m => string.Equals(m, AdminTotpGate.SoftwareTokenMfa, StringComparison.Ordinal));
    }
}
