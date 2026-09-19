using System.Collections.Concurrent;
using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// api.md 5 pipeline exercised in isolation: no Postgres, no Cognito, no S3.
// The IBeaconKeyLookup is a fake so tests can seed keys; WMSFO_DEV_STATIC_TOKENS
// is on so JwtBearer is replaced by the dev token scheme; the AdminTotpGate is
// preloaded so tests decide who has TOTP.
public sealed class PipelineHost : IAsyncDisposable
{
    public WebApplication App { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public FakeBeaconKeyLookup Beacons { get; } = new();
    public AdminTotpGate TotpGate { get; private set; } = null!;
    public InMemoryPersonUpsert PersonUpsert { get; } = new();
    public FakeServerClock Clock { get; } = new();
    public bool Ready { get; set; } = true;
    public CapturingLoggerProvider Logs { get; } = new();

    public static async Task<PipelineHost> StartAsync(Action<PipelineHost, WebApplication>? mapEndpoints = null)
    {
        var host = new PipelineHost();
        await host.BuildAsync(mapEndpoints);
        return host;
    }

    private async Task BuildAsync(Action<PipelineHost, WebApplication>? mapEndpoints)
    {
        var options = TestOptions.WithDevStaticTokens();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(PipelineHost).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(Logs);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<IBeaconKeyLookup>(Beacons);
        builder.Services.AddSingleton<IPersonUpsert>(PersonUpsert);
        builder.Services.AddSingleton<IServerClock>(Clock);
        builder.Services.AddSingleton<IAdminTotpChecker, ScriptedAdminTotpChecker>();

        builder.Services.ConfigureForwardedHeaders(options.TrustedProxyHops);
        builder.Services.AddWmsfoCors(options);
        builder.Services.AddWmsfoAuth(options);
        builder.Services.ConfigureRateLimits();

        // Set the AdminTotpGate to be seeded per test.
        App = builder.Build();
        TotpGate = App.Services.GetRequiredService<AdminTotpGate>();

        // api.md 5 step 1: readiness. Tests default to ready; setting Ready=false
        // exercises the 503 unavailable path with the contracts 0.3 body.
        App.Use(async (context, next) =>
        {
            if (!Ready && context.Request.Path != "/api/health")
            {
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.Headers.CacheControl = "no-store";
                var body = ExceptionHandlingMiddleware.SerializeError(
                    ApiErrorCodes.Unavailable, "service starting", null, context.TraceIdentifier);
                await context.Response.Body.WriteAsync(body);
                return;
            }
            await next();
        });

        // api.md 5 middleware order.
        App.UseWmsfoPipeline();

        // A minimal set of test routes covering every pipeline branch. The
        // policy matrix, the rate limit, and the both-headers guard all key on
        // these routes.
        App.MapGet("/api/health", () => Results.Ok(new { status = "ok" })).DisableRateLimiting();

        App.MapGet("/test/beacon", (HttpContext ctx) => new { beacon = BeaconAuthenticationHandler.TryGetBeaconId(ctx.User) })
            .RequireAuthorization(AuthPolicies.Beacon);

        App.MapGet("/test/person", () => Results.Ok(new { ok = true }))
            .RequireAuthorization(AuthPolicies.Person);

        App.MapGet("/test/editor", () => Results.Ok(new { ok = true }))
            .RequireAuthorization(AuthPolicies.Editor);

        App.MapGet("/test/admin", () => Results.Ok(new { ok = true }))
            .RequireAuthorization(AuthPolicies.Admin);

        // /admin/* stub protected by Admin policy + AdminTotpGate; used by the
        // policy matrix test and the mfa_required check.
        App.MapGet("/admin/test", () => Results.Ok(new { ok = true }))
            .RequireAuthorization(AuthPolicies.Admin)
            .AddEndpointFilter(async (ctx, next) =>
                await App.Services.GetRequiredService<AdminTotpGate>().Filter(ctx, next));

        // Rate-limited route (beacon-scoped) for the 429/Retry-After test.
        App.MapGet("/test/rate-limited", (HttpContext _) => Results.Ok(new { ok = true }))
            .RequireAuthorization(AuthPolicies.Beacon)
            .RequireRateLimiting(RateLimitPolicies.HeartbeatPerBeacon);

        // Body-limit tester (8 KB heartbeat limit).
        App.MapPost("/test/tiny", async (HttpContext ctx) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms);
            return Results.Ok(new { bytes = ms.Length });
        }).WithBodyLimit(BodyLimits.Heartbeat);

        // Content-type restricted route for the 415 test (accepts text/plain only).
        App.MapPost("/test/plain", async (HttpContext ctx) =>
        {
            if (!ctx.Request.ContentType?.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) ?? true)
            {
                throw new ApiException(StatusCodes.Status415UnsupportedMediaType,
                    ApiErrorCodes.UnsupportedMediaType, "text/plain required");
            }
            using var reader = new StreamReader(ctx.Request.Body);
            var body = await reader.ReadToEndAsync();
            return Results.Ok(new { length = body.Length });
        });

        // Forces the exception middleware to produce 500 internal_error.
        App.MapGet("/test/boom", () => { throw new InvalidOperationException("boom"); });

        // Client-IP echo route for the forwarded-headers assertion.
        App.MapGet("/test/ip", (HttpContext ctx) =>
            Results.Ok(new { ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "" }));

        // Validation demo: throws validation_failed with two fields, used to
        // check details.fields shape.
        App.MapPost("/test/validate", () =>
        {
            var v = new RequestValidation();
            v.Field("lat", "must be between -90 and 90");
            v.Field("lng", "must be between -180 and 180");
            v.ThrowIfInvalid();
            return Results.Ok();
        });

        // Realtime callback for the both-headers guard test (needs a route on
        // the /realtime path so UseWhen exclusion behaves).
        App.MapGet("/realtime/echo", (HttpContext ctx) =>
        {
            var xff = ctx.Request.Headers["X-Forwarded-For"].ToString();
            return Results.Ok(new { xff });
        });

        // Beacon-only route with serverTime filter attached, for the response
        // convention check.
        App.MapGet("/test/beacon-stamped", (HttpContext ctx) =>
            Results.Ok(new { beacon = BeaconAuthenticationHandler.TryGetBeaconId(ctx.User) }))
            .RequireAuthorization(AuthPolicies.Beacon)
            .AddServerTime();

        mapEndpoints?.Invoke(this, App);

        await App.StartAsync();
        var server = App.Services.GetRequiredService<IServer>();
        var address = server.Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        Client = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
        })
        {
            BaseAddress = new Uri(address),
        };
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        if (App is not null)
        {
            await App.StopAsync();
            await App.DisposeAsync();
        }
    }
}

// api.md 6.1: fake beacon lookup so tests can seed keys without a database.
public sealed class FakeBeaconKeyLookup : IBeaconKeyLookup
{
    private readonly Dictionary<string, BeaconAuthRow> _byHash = new(StringComparer.Ordinal);

    public FakeBeaconKeyLookup Add(string key, long id, bool isActive = true, DateTimeOffset? revokedAt = null, int keyVersion = 1)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        _byHash[Convert.ToBase64String(hash)] = new BeaconAuthRow(id, isActive, revokedAt, keyVersion);
        return this;
    }

    public Task<BeaconAuthRow?> FindByHashAsync(byte[] hash, CancellationToken ct)
    {
        var lookup = Convert.ToBase64String(hash);
        return Task.FromResult(_byHash.GetValueOrDefault(lookup));
    }
}

// api.md 6.3: tests preload the gate so the Cognito call never runs.
public sealed class ScriptedAdminTotpChecker : IAdminTotpChecker
{
    private readonly HashSet<string> _enabled = new(StringComparer.Ordinal);

    public Task<bool> HasSoftwareTokenMfaAsync(string sub, CancellationToken ct) =>
        Task.FromResult(_enabled.Contains(sub));

    public void SetEnabled(string sub) => _enabled.Add(sub);
    public void SetDisabled(string sub) => _enabled.Remove(sub);
}

// Frozen clock for the serverTime filter check.
public sealed class FakeServerClock : IServerClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 12, 22, 1, 31, 7, TimeSpan.Zero);
    public DateTimeOffset UtcNow() => Now;
}

// In-memory logger provider so pipeline tests can assert on the emitted log
// lines - api.md 16 request line is the immediate use.
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<CapturedLog> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() { }

    public IEnumerable<CapturedLog> LinesFor(string category, string messagePrefix) =>
        Entries.Where(e =>
            string.Equals(e.Category, category, StringComparison.Ordinal)
            && e.Message.StartsWith(messagePrefix, StringComparison.Ordinal));

    private sealed class Logger : ILogger
    {
        private readonly CapturingLoggerProvider _sink;
        private readonly string _category;
        public Logger(CapturingLoggerProvider sink, string category) { _sink = sink; _category = category; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => Noop.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            var props = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kv in kvps)
                {
                    if (kv.Key == "{OriginalFormat}") continue;
                    props[kv.Key] = kv.Value;
                }
            }
            _sink.Entries.Enqueue(new CapturedLog(logLevel, _category, message, props));
        }

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new();
            public void Dispose() { }
        }
    }
}

public sealed record CapturedLog(LogLevel Level, string Category, string Message, IReadOnlyDictionary<string, object?> Properties);

public static class TestOptions
{
    public static WmsfoOptions WithDevStaticTokens() => new()
    {
        Env = "dev",
        ServiceName = "wmsfo-api-test",
        DbConnection = "Host=localhost;Database=x;Username=x;SSL Mode=Require",
        DbMigrationConnection = "Host=localhost;Database=x;Username=x;SSL Mode=Require",
        AwsRegion = "us-east-2",
        S3Bucket = "wmsfo-test",
        CdnBaseUrl = "https://cdn.example.com",
        PublicApiBaseUrl = "https://api.example.com",
        SiteBaseUrl = "https://site.example.com",
        HubUrl = "wss://gateway.example.com/hub",
        GatewayInternalUrl = "http://127.0.0.1:1",
        CorsOrigins = "https://site.example.com",
        TrustedProxyHops = 2,
        CognitoIssuer = "https://cognito-idp.us-east-2.amazonaws.com/us-east-2_pool",
        CognitoClientIds = "site-client-id,admin-client-id",
        CognitoUserPoolId = "us-east-2_pool",
        AdminGroup = "admin",
        EditorGroup = "editor",
        SesFromAddress = "alerts@example.com",
        ContactNotifyEmail = "inbox@example.com",
        AlertSendPerSec = 10,
        EnrollmentEncryptionKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        ReconcileTickMs = 1000,
        LogLevel = "Information",
        DevStaticTokens = true,
    };
}
