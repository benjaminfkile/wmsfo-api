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
using Wmsfo.Api.Data;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.IntegrationTests;

// A29 test host: media pipeline with the LocalObjectStore so the DZI tile PUTs
// land on disk exactly as the S3 store would (api.md 20). The `Store` may be
// overridden with a fault-injecting wrapper so the "failed tile PUT leaves the
// row pending" case is covered.
public sealed class A29Host : IAsyncDisposable
{
    public WebApplication App { get; }
    public HttpClient Client { get; }
    public WmsfoOptions Options { get; }
    public IObjectStore Store { get; }
    public string StoreRoot { get; }

    private A29Host(WebApplication app, HttpClient client, WmsfoOptions options, IObjectStore store, string root)
    {
        App = app;
        Client = client;
        Options = options;
        Store = store;
        StoreRoot = root;
    }

    public static async Task<A29Host> StartAsync(
        string connectionString,
        Func<LocalObjectStore, IObjectStore>? wrapStore = null)
    {
        var storeRoot = Path.Combine(Path.GetTempPath(), "wmsfo-a29-" + Guid.NewGuid().ToString("N"));
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
            ApplicationName = typeof(A29Host).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var connections = WmsfoConnectionStrings.ForTests(connectionString);
        var local = new LocalObjectStore(storeRoot, options.PublicApiBaseUrl);
        IObjectStore store = wrapStore is null ? local : wrapStore(local);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(connections);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<IGatewayInternalClient>(new FakeGatewayClient());
        builder.Services.AddSingleton<NodeStateService>();
        builder.Services.AddSingleton<NodeCounters>();
        builder.Services.AddDbContextFactory<WmsfoDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        builder.Services.AddDbContext<WmsfoDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        builder.Services.AddSingleton<IServerClock, SystemServerClock>();
        builder.Services.AddSingleton(sp => IconLibrary.Load(TestPaths.IconsDir, options.CdnBaseUrl));
        builder.Services.AddSingleton<SnapshotBuilder>();
        builder.Services.AddSingleton<LiveObjectWriter>();
        builder.Services.AddSingleton<LocationIngest>();
        builder.Services.AddSingleton<AdminSnapshotTransaction>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<AuditRecorder>();
        builder.Services.AddSingleton<AuditEndpointFilter>();

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

        AdminMediaEndpoints.MapAll(app);
        AdminIconEndpoints.MapAll(app);

        // The /local-upload/{id} route - same as A15Host: turns a browser PUT
        // into a store write with the pending tag.
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
            await local.WriteUploadAsync(key, ms.ToArray(), contentType, ct);
            return Results.NoContent();
        }).DisableRateLimiting();

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First().TrimEnd('/');
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new A29Host(app, client, options, store, storeRoot);
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

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        try { await App.StopAsync(); } catch { }
        await App.DisposeAsync();
        try { Directory.Delete(StoreRoot, recursive: true); } catch { }
    }
}
