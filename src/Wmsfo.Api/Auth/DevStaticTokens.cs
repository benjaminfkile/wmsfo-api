using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Auth;

// api.md 20: WMSFO_DEV_STATIC_TOKENS=true accepts three fixed test bearer tokens
// so the integration tests can exercise every policy without a real Cognito pool.
// Refused when WMSFO_ENV is prod (validator in WmsfoOptionsValidator).
public static class DevStaticTokens
{
    public const string PersonToken = "wmsfo-dev-person";
    public const string EditorToken = "wmsfo-dev-editor";
    public const string AdminToken = "wmsfo-dev-admin";

    // Cognito subs are UUIDs; a stable fake per role keeps the person upsert
    // deterministic across test runs.
    public const string PersonSub = "00000000-0000-4000-8000-000000000001";
    public const string EditorSub = "00000000-0000-4000-8000-000000000002";
    public const string AdminSub = "00000000-0000-4000-8000-000000000003";

    public const string PersonEmail = "person@wmsfo.test";
    public const string EditorEmail = "editor@wmsfo.test";
    public const string AdminEmail = "admin@wmsfo.test";

    public static (string Sub, string Email, string[] Groups)? Resolve(string token, WmsfoOptions options)
    {
        return token switch
        {
            PersonToken => (PersonSub, PersonEmail, Array.Empty<string>()),
            EditorToken => (EditorSub, EditorEmail, new[] { options.EditorGroup }),
            AdminToken => (AdminSub, AdminEmail, new[] { options.AdminGroup }),
            _ => null,
        };
    }
}

// Bearer scheme that accepts the three tokens above. Registered instead of the
// JwtBearer scheme when WMSFO_DEV_STATIC_TOKENS is on. Same scheme name so every
// [RequireAuthorization("Person"|"Editor"|"Admin")] works without change.
public sealed class DevStaticTokenAuthenticationOptions : AuthenticationSchemeOptions { }

public sealed class DevStaticTokenAuthenticationHandler : AuthenticationHandler<DevStaticTokenAuthenticationOptions>
{
    private readonly WmsfoOptions _options;

    public DevStaticTokenAuthenticationHandler(
        IOptionsMonitor<DevStaticTokenAuthenticationOptions> optionsMonitor,
        ILoggerFactory logger,
        UrlEncoder encoder,
        WmsfoOptions options)
        : base(optionsMonitor, logger, encoder)
    {
        _options = options;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var header) || StringValuesHelper.IsEmpty(header))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
        var raw = header.ToString();
        if (!raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.Fail("expected Bearer token"));
        }
        var token = raw.Substring("Bearer ".Length).Trim();
        var resolved = DevStaticTokens.Resolve(token, _options);
        if (resolved is null)
        {
            return Task.FromResult(AuthenticateResult.Fail("unknown dev token"));
        }
        var (sub, email, groups) = resolved.Value;

        var identity = new ClaimsIdentity(AuthSchemes.CognitoJwt);
        identity.AddClaim(new Claim(PersonClaims.Sub, sub));
        identity.AddClaim(new Claim(PersonClaims.Email, email));
        identity.AddClaim(new Claim(PersonClaims.TokenUse, "id"));
        foreach (var group in groups)
        {
            identity.AddClaim(new Claim(PersonClaims.Groups, group));
        }
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), AuthSchemes.CognitoJwt);
        return Task.FromResult(AuthenticateResult.Success(ticket));
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
}

// A dev IPersonUpsert that returns a stable id per sub; no database required.
public sealed class InMemoryPersonUpsert : IPersonUpsert
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _ids = new(StringComparer.Ordinal);
    private long _next;

    public Task<long?> EnsureAsync(string sub, string email, CancellationToken ct)
    {
        var id = _ids.GetOrAdd(sub, _ => System.Threading.Interlocked.Increment(ref _next));
        return Task.FromResult<long?>(id);
    }
}
