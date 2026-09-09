using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Content;
using Wmsfo.Api.Data;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.IntegrationTests;

// A19 end-to-end host - mirrors Program.cs with the full set of endpoints so
// the sequence summaries of contracts 12 can play out through the same handlers
// production runs. LocalObjectStore (backed by a temp directory) stands in for
// S3, FakeGatewayClient stands in for the gateway internal client, DbPersonUpsert
// so /cookies inserts land against a real person row. Rate limits are disabled
// so scenario tests do not throttle themselves.
public sealed class A19Host : IAsyncDisposable
{
    public WebApplication App { get; }
    public HttpClient Client { get; }
    public WmsfoOptions Options { get; }
    public LocalObjectStore Store { get; }
    public string StoreRoot { get; }
    public FakeGatewayClient Gateway { get; }
    public NodeStateService State => App.Services.GetRequiredService<NodeStateService>();
    public LiveObjectWriter Writer => App.Services.GetRequiredService<LiveObjectWriter>();

    private A19Host(WebApplication app, HttpClient client, WmsfoOptions options,
        LocalObjectStore store, string storeRoot, FakeGatewayClient gateway)
    {
        App = app;
        Client = client;
        Options = options;
        Store = store;
        StoreRoot = storeRoot;
        Gateway = gateway;
    }

    public static async Task<A19Host> StartAsync(string connectionString)
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "wmsfo-a19-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeRoot);

        var options = new WmsfoOptions
        {
            Env = "dev",
            ServiceName = "wmsfo-api-test",
            DbConnection = connectionString,
            DbMigrationConnection = connectionString,
            AwsRegion = "us-east-2",
            S3Bucket = "wmsfo-test",
            CdnBaseUrl = "https://cdn.example",
            PublicApiBaseUrl = "https://api.example.com",
            SiteBaseUrl = "https://site.example.com",
            HubUrl = "wss://gateway.example.com/hub",
            GatewayInternalUrl = "http://127.0.0.1:1",
            CorsOrigins = "https://site.example.com",
            TrustedProxyHops = 2,
            CognitoIssuer = "https://cognito-idp.us-east-2.amazonaws.com/us-east-2_pool",
            CognitoClientIds = "site-client-id",
            CognitoUserPoolId = "us-east-2_pool",
            AdminGroup = "admin",
            EditorGroup = "editor",
            SesFromAddress = "alerts@example.com",
            ContactNotifyEmail = "inbox@example.com",
            AlertSendPerSec = 10,
            EnrollmentEncryptionKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            ReconcileTickMs = 1000,
            LogLevel = "Warning",
            DevStaticTokens = true,
            ObjectStoreDir = storeRoot,
        };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(A19Host).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var connections = WmsfoConnectionStrings.ForTests(connectionString);
        var store = new LocalObjectStore(storeRoot, options.PublicApiBaseUrl);
        var gateway = new FakeGatewayClient();
        var iconLibrary = IconLibrary.Load(TestPaths.IconsDir, options.CdnBaseUrl);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(connections);
        builder.Services.AddSingleton<IObjectStore>(store);
        builder.Services.AddSingleton<IGatewayInternalClient>(gateway);
        builder.Services.AddSingleton<NodeStateService>();
        builder.Services.AddSingleton<NodeCounters>();
        builder.Services.AddDbContextFactory<WmsfoDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        builder.Services.AddDbContext<WmsfoDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        builder.Services.AddSingleton<IServerClock, SystemServerClock>();
        builder.Services.AddSingleton(iconLibrary);
        builder.Services.AddSingleton(SharedContent.Registry);
        builder.Services.AddSingleton(SharedContent.Validator);
        builder.Services.AddSingleton<DocumentBuilder>();
        builder.Services.AddSingleton<SnapshotBuilder>();
        builder.Services.AddSingleton<LiveObjectWriter>();
        builder.Services.AddSingleton<LocationIngest>();
        builder.Services.AddSingleton<AdminSnapshotTransaction>();
        builder.Services.AddSingleton<StarterContent>();
        builder.Services.AddSingleton<Publisher>();
        builder.Services.AddSingleton<Restorer>();
        builder.Services.AddSingleton<SnapshotBootstrap>();
        // Real DB person upsert so cookies (with a foreign key to person) persist.
        builder.Services.AddSingleton<IPersonUpsert, DbPersonUpsert>();

        builder.Services.ConfigureForwardedHeaders(options.TrustedProxyHops);
        builder.Services.AddWmsfoCors(options);
        builder.Services.AddWmsfoAuth(options);
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            foreach (var policy in AllPolicies())
            {
                o.AddPolicy(policy, _ => RateLimitPartition.GetNoLimiter("no-limit"));
            }
        });

        var app = builder.Build();

        app.UseWmsfoPipeline();

        // Wire every endpoint family, matching Program.cs so scenarios can hit
        // the same handlers production runs.
        BeaconEndpoints.MapAll(app);
        RealtimeEndpoints.MapAll(app);
        AdminEventEndpoints.MapAll(app);
        AdminRouteEndpoints.MapAll(app);
        AdminBeaconEndpoints.MapAll(app);
        AdminMediaEndpoints.MapAll(app);
        AdminIconEndpoints.MapAll(app);
        AdminSponsorEndpoints.MapAll(app);
        AdminCookieTypeEndpoints.MapAll(app);
        AdminSettingsEndpoints.MapAll(app);
        MeEndpoints.MapAll(app);
        PublicWriteEndpoints.MapAll(app);
        AdminCookieModerationEndpoints.MapAll(app);
        AdminInboxEndpoints.MapAll(app);
        AdminContentEndpoints.MapAll(app);
        AdminDiagnosticsEndpoints.MapAdminDiagnostics(app);

        // Local-upload route (Program.cs installs this when LocalObjectStore is
        // the backing store).
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
            await store.WriteUploadAsync(key, ms.ToArray(), contentType, ct);
            return Results.NoContent();
        }).DisableRateLimiting();

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First().TrimEnd('/');
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new A19Host(app, client, options, store, storeRoot, gateway);
    }

    private static IEnumerable<string> AllPolicies()
    {
        yield return RateLimitPolicies.LocationsPerBeacon;
        yield return RateLimitPolicies.HeartbeatPerBeacon;
        yield return RateLimitPolicies.BeaconLogsPerBeacon;
        yield return RateLimitPolicies.BeaconMePerBeacon;
        yield return RateLimitPolicies.EnrollPerIp;
        yield return RateLimitPolicies.ContactPerIp;
        yield return RateLimitPolicies.SubscriptionsPerIp;
        yield return RateLimitPolicies.PreviewPerIp;
        yield return RateLimitPolicies.CookiesPerPerson;
        yield return RateLimitPolicies.MeSubscribePerPerson;
        yield return RateLimitPolicies.MediaUploadUrlPerPerson;
        yield return RateLimitPolicies.AdminPerPerson;
    }

    public HttpRequestMessage AdminRequest(HttpMethod method, string path) =>
        Bearer(method, path, DevStaticTokens.AdminToken);

    public HttpRequestMessage EditorRequest(HttpMethod method, string path) =>
        Bearer(method, path, DevStaticTokens.EditorToken);

    public HttpRequestMessage PersonRequest(HttpMethod method, string path) =>
        Bearer(method, path, DevStaticTokens.PersonToken);

    private static HttpRequestMessage Bearer(HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    public T GetService<T>() where T : notnull =>
        App.Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await App.StopAsync(); } catch { }
        await App.DisposeAsync();
        try { Directory.Delete(StoreRoot, recursive: true); } catch { }
    }
}
