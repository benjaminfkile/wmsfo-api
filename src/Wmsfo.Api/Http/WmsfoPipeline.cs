using System.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Http;

// api.md 5: the full pipeline as one composable extension so Program.cs and the
// test hosts wire the middleware in the same order. Readiness lives in
// Program.cs because the gate is not yet available at ConfigureServices time.
public static class WmsfoPipeline
{
    public static void AddWmsfoAuth(this IServiceCollection services, WmsfoOptions options)
    {
        var authBuilder = services.AddAuthentication();

        // Beacon scheme (6.1) is always registered.
        authBuilder.AddScheme<BeaconAuthenticationOptions, BeaconAuthenticationHandler>(
            AuthSchemes.BeaconKey, _ => { });

        // Cognito JWT (6.2) or the dev static token scheme when
        // WMSFO_DEV_STATIC_TOKENS is on. Both register under the same scheme name
        // so endpoint policies stay one line.
        if (options.DevStaticTokens)
        {
            authBuilder.AddScheme<DevStaticTokenAuthenticationOptions, DevStaticTokenAuthenticationHandler>(
                AuthSchemes.CognitoJwt, _ => { });
        }
        else
        {
            authBuilder.AddJwtBearer(AuthSchemes.CognitoJwt, o => CognitoAuth.Configure(o, options));
        }

        // 6.4 api key scheme, and a composite that hands wak_ bearers to it and
        // anything else to the Cognito scheme. Policies list the composite so a
        // single endpoint responds to either principal.
        authBuilder.AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>(
            AuthSchemes.ApiKey, _ => { });
        authBuilder.AddPolicyScheme(AuthSchemes.CognitoOrApiKey, "Cognito or API key", o =>
        {
            o.ForwardDefaultSelector = ctx =>
            {
                var auth = ctx.Request.Headers.Authorization.ToString();
                if (auth.StartsWith("Bearer " + Wmsfo.Api.Security.Keys.ApiKeyPrefix, StringComparison.Ordinal))
                {
                    return AuthSchemes.ApiKey;
                }
                return AuthSchemes.CognitoJwt;
            };
        });

        services.AddHttpContextAccessor();
        services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, CapabilityOrGroupHandler>();

        services.AddAuthorizationBuilder()
            // 6.1 policies
            .AddPolicy(AuthPolicies.Beacon, p =>
            {
                p.AddAuthenticationSchemes(AuthSchemes.BeaconKey);
                p.RequireClaim(BeaconClaims.BeaconId);
            })
            .AddPolicy(AuthPolicies.BeaconAdmin, p =>
            {
                p.AddAuthenticationSchemes(AuthSchemes.BeaconKey);
                p.RequireClaim(BeaconClaims.BeaconRole, BeaconClaims.RoleAdmin);
            })
            // 6.2 policies
            .AddPolicy(AuthPolicies.Person, p =>
            {
                p.AddAuthenticationSchemes(AuthSchemes.CognitoJwt);
                p.RequireAuthenticatedUser();
            })
            // 6.4 Editor and Admin admit either a Cognito user in the named
            // group or an API key with a matching capability, as decided by
            // the endpoint's RequireCapability / DenyApiKeys metadata.
            .AddPolicy(AuthPolicies.Editor, p =>
            {
                p.AddAuthenticationSchemes(AuthSchemes.CognitoOrApiKey);
                p.RequireAuthenticatedUser();
                p.AddRequirements(new CapabilityOrGroupRequirement(options.EditorGroup, options.AdminGroup));
            })
            .AddPolicy(AuthPolicies.Admin, p =>
            {
                p.AddAuthenticationSchemes(AuthSchemes.CognitoOrApiKey);
                p.RequireAuthenticatedUser();
                p.AddRequirements(new CapabilityOrGroupRequirement(options.AdminGroup));
            });

        // The forbidden-vs-mfa mapping: default authorization result handling is
        // 403 without a body; the ExceptionHandling middleware fills the body when
        // AdminTotpGate throws. A missing token is still 401 unauthenticated.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, WmsfoAuthorizationResultHandler>();

        services.AddSingleton<AdminTotpGate>();
        if (!services.Any(s => s.ServiceType == typeof(IAdminTotpChecker)))
        {
            services.AddSingleton<IAdminTotpChecker, AlwaysFailAdminTotpChecker>();
        }
        if (!services.Any(s => s.ServiceType == typeof(IPersonUpsert)))
        {
            if (options.DevStaticTokens)
            {
                services.AddSingleton<IPersonUpsert, InMemoryPersonUpsert>();
            }
            else
            {
                services.AddSingleton<IPersonUpsert, DbPersonUpsert>();
            }
        }
        if (!services.Any(s => s.ServiceType == typeof(IBeaconKeyLookup)))
        {
            services.AddScoped<IBeaconKeyLookup, DbBeaconKeyLookup>();
        }
        if (!services.Any(s => s.ServiceType == typeof(IApiKeyLookup)))
        {
            services.AddScoped<IApiKeyLookup, DbApiKeyLookup>();
        }
        if (!services.Any(s => s.ServiceType == typeof(IApiKeyLastUsedStamp)))
        {
            services.AddSingleton<IApiKeyLastUsedStamp, DbApiKeyLastUsedStamp>();
        }
    }

    // Middleware order - api.md 5 outermost first. The readiness step is added by
    // Program.cs since it needs the gate wired to the DI scope on the app.
    public static void UseWmsfoPipeline(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Wmsfo.Api.Http");

        // Step 2: forwarded headers with UseWhen except /realtime/*.
        app.UseForwardedHeadersExceptRealtime();

        // Step 3: request id and logging scope.
        app.Use(async (context, next) =>
        {
            var requestId = Activity.Current?.Id ?? context.TraceIdentifier;
            using (logger.BeginScope(new Dictionary<string, object?> { ["requestId"] = requestId }))
            {
                context.TraceIdentifier = requestId;
                await next();
            }
        });

        // Step 4: exception handler. Wraps everything below so throws map to the
        // contracts 0.3 error body.
        ExceptionHandlingMiddleware.Use(app, logger);

        // Cache-Control: no-store on every response (api.md 5 response conventions).
        NoStoreMiddleware.Use(app);

        // Route resolution before body-limits, CORS, and auth so endpoint
        // metadata (policies, rate limits, body limits) is visible to the
        // middleware below.
        app.UseRouting();

        // Step 5: per-route body-size limits from RequestBodyLimit metadata.
        BodyLimits.Use(app);

        // Step 6: CORS on every route except the two realtime callbacks and health.
        app.UseWhen(
            ctx => !ctx.Request.Path.StartsWithSegments("/realtime")
                && !ctx.Request.Path.Equals("/api/health"),
            branch => branch.UseCors(WmsfoCors.PolicyName));

        // Step 7: both-headers guard runs before either scheme.
        BothAuthHeadersGuard.Use(app);

        app.UseAuthentication();
        app.UseAuthorization();

        // Step 8: rate limiting per the contracts 4.0 table.
        app.UseRateLimiter();
    }

}

// A safe default (Cognito unavailable in tests / local runs without the checker
// registered): AdminGetUser failure is treated as MFA-not-enabled per api.md 6.3.
public sealed class AlwaysFailAdminTotpChecker : IAdminTotpChecker
{
    public Task<bool> HasSoftwareTokenMfaAsync(string sub, CancellationToken ct) => Task.FromResult(false);
}

// The default AuthorizationMiddlewareResultHandler writes a bare 401/403 with no
// body. We translate an authorization failure into the ApiException the exception
// middleware maps to the contracts 0.3 error body.
internal sealed class WmsfoAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Challenged)
        {
            throw new ApiException(StatusCodes.Status401Unauthorized, ApiErrorCodes.Unauthenticated, "authentication required");
        }
        if (authorizeResult.Forbidden)
        {
            throw new ApiException(StatusCodes.Status403Forbidden, ApiErrorCodes.Forbidden, "not permitted");
        }
        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
