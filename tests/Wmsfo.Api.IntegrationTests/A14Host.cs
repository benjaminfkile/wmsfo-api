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
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Data;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.IntegrationTests;

// A14 test host — the publish transaction (Publisher), versions, restore, the
// preview flow, and first-boot orchestration. Registers real /admin/content/*
// endpoints plus /preview/document, with an in-memory RecordingObjectStore so
// the snapshot PUTs are inspectable and can be forced to fail.
public sealed class A14Host : IAsyncDisposable
{
    public WebApplication App { get; }
    public HttpClient Client { get; }
    public WmsfoOptions Options { get; }
    public RecordingObjectStore Store { get; }
    public FakeGatewayClient Gateway { get; }

    private A14Host(WebApplication app, HttpClient client, WmsfoOptions options,
        RecordingObjectStore store, FakeGatewayClient gateway)
    {
        App = app;
        Client = client;
        Options = options;
        Store = store;
        Gateway = gateway;
    }

    // Load KindRegistry / SchemaValidator once per test run so JsonSchema.Net's
    // global $id registry does not double-register.
    public static KindRegistry SharedRegistry => SharedContent.Registry;
    public static SchemaValidator SharedValidator => SharedContent.Validator;

    public static async Task<A14Host> StartAsync(string connectionString)
    {
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
        };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(A14Host).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var connections = WmsfoConnectionStrings.ForTests(connectionString);
        var store = new RecordingObjectStore();
        var gateway = new FakeGatewayClient();
        var iconLibrary = IconLibrary.Load(TestPaths.IconsDir, options.CdnBaseUrl);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(connections);
        builder.Services.AddSingleton<IObjectStore>(store);
        builder.Services.AddSingleton<IGatewayInternalClient>(gateway);
        builder.Services.AddSingleton<NodeStateService>();
        builder.Services.AddDbContextFactory<WmsfoDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        builder.Services.AddDbContext<WmsfoDbContext>(o => o
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention());
        builder.Services.AddSingleton<IServerClock, SystemServerClock>();
        builder.Services.AddSingleton(iconLibrary);
        builder.Services.AddSingleton(SharedRegistry);
        builder.Services.AddSingleton(SharedValidator);
        builder.Services.AddSingleton<DocumentBuilder>();
        builder.Services.AddSingleton<SnapshotBuilder>();
        builder.Services.AddSingleton<LiveObjectWriter>();
        builder.Services.AddSingleton<LocationIngest>();
        builder.Services.AddSingleton<AdminSnapshotTransaction>();
        builder.Services.AddSingleton<StarterContent>();
        builder.Services.AddSingleton<Publisher>();
        builder.Services.AddSingleton<Restorer>();
        builder.Services.AddSingleton<SnapshotBootstrap>();

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
        AdminContentEndpoints.MapAll(app);

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First().TrimEnd('/');
        var client = new HttpClient { BaseAddress = new Uri(address) };
        return new A14Host(app, client, options, store, gateway);
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

    public HttpRequestMessage EditorRequest(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", DevStaticTokens.EditorToken);
        return req;
    }

    public T GetService<T>() where T : notnull =>
        App.Services.GetRequiredService<T>();

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
