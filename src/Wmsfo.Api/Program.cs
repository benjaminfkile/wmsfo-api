using Amazon;
using Amazon.S3;
using Amazon.SimpleEmailV2;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wmsfo.Api.Chores;
using Wmsfo.Api.Config;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Data;
using Wmsfo.Api.Email;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

if (args.Length > 0)
{
    switch (args[0])
    {
        case "export-openapi":
            var openapiPath = args.Length > 1 ? args[1] : "contracts/openapi.json";
            await OpenApiExport.WriteAsync(openapiPath);
            return;
        case "export-contracts":
            var root = args.Length > 1 ? args[1] : "contracts";
            Directory.CreateDirectory(root);
            SchemaExport.WriteAll(Path.Combine(root, "schema"));
            FixtureExport.WriteAll(Path.Combine(root, "fixtures"));
            // Starter content mirrors the content-document fixture byte-for-byte; the seed loader reads this file.
            await File.WriteAllBytesAsync(
                Path.Combine(root, "starter-content.json"),
                CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildContentDocument()));
            AdminThresholds.WriteTo(Path.Combine(root, "admin-thresholds.json"));
            await OpenApiExport.WriteAsync(Path.Combine(root, "openapi.json"));
            return;
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Configuration.AddEnvironmentVariables();

// api.md 3 step 1: read configuration and fail fast.
var options = WmsfoOptions.Bind(builder.Configuration);
var validation = new WmsfoOptionsValidator().Validate(null, options);
if (validation.Failed)
{
    Console.Error.WriteLine(validation.FailureMessage);
    Environment.Exit(2);
    return;
}
Console.WriteLine($"wmsfo boot: env={options.Env} service={options.ServiceName} region={options.AwsRegion} cdn={options.CdnBaseUrl} api={options.PublicApiBaseUrl} hub={options.HubUrl}");

// api.md 16: JSON console logging with ts/level/msg/requestId/service/env/node
// on every line. Node accessor is wired after Build() when the gateway client
// singleton is available; until then the machine name stands in.
builder.Logging.AddWmsfoJsonConsoleLogging(options);
// The gateway leader poll runs every 2 s on every node; HttpClient's per-request
// Information lines would be most of the log volume (api.md 16 lists what the
// service itself logs). Warnings and errors from the client still get through.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

// api.md 3 step 2: build the two connection strings and register the contexts.
var connections = WmsfoConnectionStrings.Build(options);
builder.Services.AddSingleton(connections);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<WmsfoReadinessGate>();
// Both registrations share one DbContextOptions<WmsfoDbContext>, so they must
// agree: both serve requests on the app connection. Migrations build their own
// context on the migrate connection inside DatabaseMigrator.
builder.Services.AddDbContextFactory<WmsfoDbContext>(o => o
    .UseNpgsql(connections.App)
    .UseSnakeCaseNamingConvention());
builder.Services.AddDbContext<WmsfoDbContext>(o => o
    .UseNpgsql(connections.App)
    .UseSnakeCaseNamingConvention());

// api.md 3 step 3: the object store. S3 in the fleet, LocalObjectStore when
// WMSFO_OBJECT_STORE_DIR is set (dev, tests). Both implement IObjectStore
// identically; nothing outside this composition root branches on the choice.
if (!string.IsNullOrEmpty(options.ObjectStoreDir))
{
    builder.Services.AddSingleton<IObjectStore>(new LocalObjectStore(options.ObjectStoreDir, options.PublicApiBaseUrl));
}
else
{
    builder.Services.AddSingleton<IAmazonS3>(_ =>
        new AmazonS3Client(RegionEndpoint.GetBySystemName(options.AwsRegion)));
    builder.Services.AddSingleton<IObjectStore>(sp =>
        new S3ObjectStore(sp.GetRequiredService<IAmazonS3>(), options.S3Bucket));
}

// api.md 3 step 3: icon library, node state, gateway internal client, snapshot
// builder, live-object writer, snapshot bootstrap (task A7). The IconLibrary lives
// under `icons/` in the container image; when unavailable a null-op library keeps
// the export path working before the migration hosts it.
var iconRoot = ResolveIconRoot(AppContext.BaseDirectory);
if (iconRoot is not null)
{
    builder.Services.AddSingleton(_ => IconLibrary.Load(iconRoot, options.CdnBaseUrl));
}
static string? ResolveIconRoot(string start)
{
    var dir = new DirectoryInfo(start);
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir.FullName, "icons");
        if (File.Exists(Path.Combine(candidate, "library.json"))) return candidate;
        dir = dir.Parent;
    }
    return null;
}

builder.Services.AddSingleton<NodeStateService>();
builder.Services.AddSingleton<NodeCounters>();
builder.Services.AddSingleton<HealthMarkerLogger>();
builder.Services.AddHttpClient<IGatewayInternalClient, GatewayInternalClient>();
builder.Services.AddSingleton<SnapshotBuilder>();
builder.Services.AddSingleton<LiveObjectWriter>();
builder.Services.AddSingleton<SnapshotBootstrap>();
builder.Services.AddHostedService<ReconcileTick>();
builder.Services.AddHostedService<LeaderMonitor>();

// api.md 13: the leader chores. Templates live under templates/email/; the
// SES sender flips to log-only when WMSFO_SES_DRY_RUN=true (dev only).
var templatesRoot = ResolveTemplatesRoot(AppContext.BaseDirectory);
if (templatesRoot is not null)
{
    builder.Services.AddSingleton(_ => EmailTemplates.Load(templatesRoot));
}
if (!options.SesDryRun)
{
    builder.Services.AddSingleton<IAmazonSimpleEmailServiceV2>(_ =>
        new AmazonSimpleEmailServiceV2Client(RegionEndpoint.GetBySystemName(options.AwsRegion)));
}
builder.Services.AddSingleton<ISesSender, SesSender>();
builder.Services.AddSingleton<IChoreClock, SystemChoreClock>();
builder.Services.AddSingleton<OutboxPublisher>();
builder.Services.AddSingleton<AlertSender>();
builder.Services.AddSingleton<StaleBeaconFlagger>();
builder.Services.AddSingleton<MediaOrphanCollector>();
builder.Services.AddSingleton<NightlyCleanup>();
builder.Services.AddHostedService<ChoreHost>();

static string? ResolveTemplatesRoot(string start)
{
    var dir = new DirectoryInfo(start);
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir.FullName, "templates", "email");
        if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, EmailTemplates.SubscriptionVerify + ".html")))
        {
            return candidate;
        }
        dir = dir.Parent;
    }
    return null;
}

// A8: the shared location transaction is the message-path and REST handler both.
builder.Services.AddSingleton<LocationIngest>();
builder.Services.AddSingleton<IServerClock, SystemServerClock>();

// A9: the [snapshot] frame that admin writes run through.
builder.Services.AddSingleton<AdminSnapshotTransaction>();

// A13: content kind registry, schema validator, and document builder. The
// registry loads contracts/kinds.json plus every schema under contracts/schema/;
// SchemaValidator derives the draft variants at construction time (once).
var contractsRoot = ResolveContractsRoot(AppContext.BaseDirectory);
if (contractsRoot is not null)
{
    builder.Services.AddSingleton(_ => KindRegistry.Load(contractsRoot));
    builder.Services.AddSingleton<SchemaValidator>();
    builder.Services.AddSingleton<DocumentBuilder>();
}

// A14: publish, restore, starter content seeding. The first-boot sequence
// consumes StarterContent + Publisher; POST /admin/content/publish and the
// restore endpoint consume them at request time.
builder.Services.AddSingleton<StarterContent>();
builder.Services.AddSingleton<Publisher>();
builder.Services.AddSingleton<Restorer>();
static string? ResolveContractsRoot(string start)
{
    var dir = new DirectoryInfo(start);
    for (var i = 0; i < 8 && dir is not null; i++)
    {
        var candidate = Path.Combine(dir.FullName, "contracts");
        if (File.Exists(Path.Combine(candidate, "kinds.json"))) return candidate;
        dir = dir.Parent;
    }
    return null;
}

// api.md 3 step 5 hook: the migrator plus the first-boot steps of sql.md 8.16
// (icon library from A3, snapshot v1 from A7). Starter content and version 1
// belong to A14; the SnapshotBootstrap stand-in inserts the fixture content so
// the pipeline is testable end to end.
builder.Services.AddSingleton<IFirstBootHook, FleetFirstBootHook>();
builder.Services.AddScoped<DatabaseMigrator>(sp => new DatabaseMigrator(
    connections.Migrate,
    sp.GetRequiredService<IFirstBootHook>(),
    sp.GetRequiredService<ILoggerFactory>().CreateLogger<DatabaseMigrator>()));
builder.Services.AddHostedService<MigrationHostedService>();

// api.md 5 pipeline services: forwarded headers, CORS, auth, rate limits.
builder.Services.ConfigureForwardedHeaders(options.TrustedProxyHops);
builder.Services.AddWmsfoCors(options);
builder.Services.AddWmsfoAuth(options);
builder.Services.ConfigureRateLimits();

builder.Services.AddOpenApi();

var app = builder.Build();
var readiness = app.Services.GetRequiredService<WmsfoReadinessGate>();

// api.md 16: `node` field is the gateway instance id once known, else the
// hostname. Wire the accessor now that the singleton exists.
var loggingFields = app.Services.GetRequiredService<IOptions<WmsfoLoggingFields>>().Value;
var gatewayClient = app.Services.GetRequiredService<IGatewayInternalClient>();
loggingFields.NodeAccessor = () => gatewayClient.LastInstanceId ?? loggingFields.Node;

// api.md 5 step 1: readiness middleware answers 503 to every non-health request
// until the migration completes.
app.Use(async (context, next) =>
{
    if (!readiness.IsReady && context.Request.Path != "/api/health")
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        var body = Wmsfo.Api.Http.ExceptionHandlingMiddleware.SerializeError(
            Wmsfo.Api.Http.ApiErrorCodes.Unavailable, "service starting", null, context.TraceIdentifier);
        await context.Response.Body.WriteAsync(body);
        return;
    }
    await next();
});

// api.md 5 steps 2-8: forwarded headers, request id, exception handler, no-store,
// body limits, routing, CORS, both-headers guard, auth, rate limiting.
app.UseWmsfoPipeline();

// api.md 17: /api/health answers 503 until ready, then `select 1` with a 2 s
// timeout. On failure log `wmsfo_health_unavailable` at Warning at most once
// per 30 s so CloudWatch's metric filter fires without flooding.
app.MapGet("/api/health", async (WmsfoConnectionStrings cs, WmsfoReadinessGate gate, HealthMarkerLogger marker, CancellationToken ct) =>
{
    if (!gate.IsReady)
    {
        marker.LogUnavailable(null);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
    try
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(2));
        await using var conn = new Npgsql.NpgsqlConnection(cs.App);
        await conn.OpenAsync(cts.Token);
        await using var command = new Npgsql.NpgsqlCommand("select 1", conn) { CommandTimeout = 2 };
        _ = await command.ExecuteScalarAsync(cts.Token);
        return Results.Ok(new { status = "ok" });
    }
    catch (Exception ex)
    {
        marker.LogUnavailable(ex);
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
})
.DisableRateLimiting();

// A8: register the real beacon and realtime handlers before the remaining stubs.
BeaconEndpoints.MapAll(app);
RealtimeEndpoints.MapAll(app);
// A9: real admin events + routes handlers replace the corresponding stubs.
AdminEventEndpoints.MapAll(app);
AdminRouteEndpoints.MapAll(app);
// A10: real admin beacons endpoints replace the stubs.
AdminBeaconEndpoints.MapAll(app);
// A15: real admin media + icons endpoints replace the stubs.
AdminMediaEndpoints.MapAll(app);
AdminIconEndpoints.MapAll(app);
// A11: real admin sponsors + cookie-types + settings endpoints replace the stubs.
AdminSponsorEndpoints.MapAll(app);
AdminCookieTypeEndpoints.MapAll(app);
AdminSettingsEndpoints.MapAll(app);
// A12: /me family, public writes (contact, verify, unsubscribe), moderation, inbox.
MeEndpoints.MapAll(app);
PublicWriteEndpoints.MapAll(app);
AdminCookieModerationEndpoints.MapAll(app);
AdminInboxEndpoints.MapAll(app);
// A13: real /admin/pages*, /admin/sections*, /admin/items*, /admin/site-settings*
// endpoints plus GET /admin/content/kinds, /admin/content/draft, /admin/content/status.
AdminContentEndpoints.MapAll(app);
// A26: /admin/api-keys list, mint, revoke - Cognito Admin only.
AdminApiKeyEndpoints.MapAll(app);
EndpointStubs.MapAll(app,
    includeBeaconStubs: false,
    includeRealtimeStubs: false,
    includeAdminEventsStubs: false,
    includeAdminRoutesStubs: false,
    includeAdminBeaconsStubs: false,
    includeAdminMediaStubs: false,
    includeAdminIconsStubs: false,
    includeAdminSponsorsStubs: false,
    includeAdminCookieTypesStubs: false,
    includeAdminSettingsStubs: false,
    includeMeStubs: false,
    includePublicWriteStubs: false,
    includePreviewStub: false,
    includeAdminCookiesStubs: false,
    includeAdminInboxStubs: false,
    includeAdminPagesStubs: false,
    includeAdminSectionsStubs: false,
    includeAdminSiteSettingsStubs: false,
    includeAdminContentStubs: false,
    includeAdminApiKeysStubs: false);
AdminDiagnosticsEndpoints.MapAdminDiagnostics(app);

// api.md 20: with WMSFO_OBJECT_STORE_DIR set, LocalObjectStore cannot presign,
// so upload tickets point uploadUrl at PUT /local-upload/{id} on the API. The
// route writes the bytes and the pending tag through the store. It exists only
// when the directory store is active and never in prod (the options validator
// refuses that combination up front).
if (app.Services.GetRequiredService<IObjectStore>() is LocalObjectStore localStore)
{
    app.MapPut("/local-upload/{id}", async (
        string id,
        HttpRequest request,
        CancellationToken ct) =>
    {
        var filenameRaw = request.Query["filename"].ToString();
        var filename = string.IsNullOrEmpty(filenameRaw) ? "upload.bin" : filenameRaw;
        var key = $"media/{id}/{filename}";
        var contentType = request.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, detail: "Content-Type required");
        }
        using var ms = new MemoryStream();
        await request.Body.CopyToAsync(ms, ct);
        await localStore.WriteUploadAsync(key, ms.ToArray(), contentType, ct);
        return Results.NoContent();
    }).DisableRateLimiting();
}

app.MapOpenApi();

app.Run();

public partial class Program;
