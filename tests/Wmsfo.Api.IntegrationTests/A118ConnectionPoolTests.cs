using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A118: the beacon paths open connections from the `Beacon` pool and nothing
// else does (sql.md 13), read from `application_name` in `pg_stat_activity`;
// and /api/health runs one probe for a burst of calls inside one second
// (api.md 17).
public sealed class A118ConnectionPoolTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private WebApplication? _app;
    private HttpClient? _client;
    private WmsfoOptions? _options;

    public A118ConnectionPoolTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            foreach (var sql in new[]
            {
                "delete from beacon_log;",
                "delete from location;",
                "delete from beacon_enrollment_token;",
                "delete from beacon;",
            })
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                await cmd.ExecuteNonQueryAsync();
            }
        }
        await StartHostAsync();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Beacon_paths_use_the_beacon_pool_and_nothing_else_does()
    {
        ClearPool(_fixture.AppConnectionString);
        ClearPool(_fixture.BeaconConnectionString);
        await WaitForCountAsync(PostgresFixture.AppApplicationName, 0);
        await WaitForCountAsync(PostgresFixture.BeaconApplicationName, 0);

        // Visitor work: the health probe and a public write run on `App` only.
        using (var health = await _client!.GetAsync("/api/health"))
        {
            Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        }
        using (var verify = await _client!.PostAsync("/subscriptions/verify",
            Json("{\"token\":\"wsv_" + new string('a', 43) + "\"}")))
        {
            Assert.Equal(HttpStatusCode.NotFound, verify.StatusCode);
        }
        Assert.True(await CountAsync(PostgresFixture.AppApplicationName) > 0);
        Assert.Equal(0, await CountAsync(PostgresFixture.BeaconApplicationName));

        // Beacon work: every beacon door, both ingest doors, and the ingest
        // branch of the authorize callback run on `Beacon` only.
        ClearPool(_fixture.AppConnectionString);
        await WaitForCountAsync(PostgresFixture.AppApplicationName, 0);

        var beaconId = await SeedBeaconAsync();
        var (token, key) = await MintEnrollmentTokenAsync(beaconId);

        using (var enroll = await _client!.PostAsync("/beacons/enroll", Json($"{{\"token\":\"{token}\"}}")))
        {
            Assert.Equal(HttpStatusCode.OK, enroll.StatusCode);
        }
        Assert.Equal(HttpStatusCode.OK, await SendBeaconAsync(HttpMethod.Get, "/beacons/me", key, null));
        Assert.Equal(HttpStatusCode.OK, await SendBeaconAsync(HttpMethod.Get, "/beacons/me/state", key, null));
        Assert.Equal(HttpStatusCode.OK, await SendBeaconAsync(HttpMethod.Post, "/beacons/heartbeat", key,
            Json("{\"sentAt\":\"2026-12-22T01:31:07Z\"}")));
        Assert.Equal(HttpStatusCode.Created, await SendBeaconAsync(HttpMethod.Post, "/beacons/logs", key,
            new StringContent("hello there", Encoding.UTF8, "text/plain")));
        Assert.Equal(HttpStatusCode.Conflict, await SendBeaconAsync(HttpMethod.Post, "/locations", key,
            Json("{\"lat\":46.87,\"lng\":-114,\"recordedAt\":\"2026-12-22T01:31:07Z\"}")));

        using (var publicTopic = await _client!.PostAsync("/realtime/authorize",
            Json("{\"channel\":\"wmsfo-api-test:location\",\"connectionId\":\"c1\"}")))
        {
            Assert.Equal(HttpStatusCode.OK, publicTopic.StatusCode);
        }
        using (var authorize = await _client!.PostAsync("/realtime/authorize",
            Json($"{{\"channel\":\"wmsfo-api-test:ingest\",\"credential\":\"{key}\",\"connectionId\":\"c1\"}}")))
        {
            Assert.Equal(HttpStatusCode.OK, authorize.StatusCode);
            Assert.Contains("\"allow\":true", await authorize.Content.ReadAsStringAsync());
        }
        using (var message = await _client!.PostAsync("/realtime/message",
            Json($"{{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"location\",\"data\":{{\"lat\":46.87,\"lng\":-114,\"recordedAt\":\"2026-12-22T01:31:07Z\"}},\"connectionId\":\"c1\",\"identity\":\"{beaconId}:1\"}}")))
        {
            // No live event: the ingest's transaction answers 409 after the
            // identity check passed.
            Assert.Equal(HttpStatusCode.Conflict, message.StatusCode);
        }

        Assert.True(await CountAsync(PostgresFixture.BeaconApplicationName) > 0);
        Assert.Equal(0, await CountAsync(PostgresFixture.AppApplicationName));
    }

    [Fact]
    public async Task Twenty_parallel_health_calls_inside_one_second_run_one_probe()
    {
        var probe = _app!.Services.GetRequiredService<HealthProbe>();
        // Let any probe left from an earlier call age out.
        await Task.Delay(HealthProbe.MaxAge + TimeSpan.FromMilliseconds(100));
        var before = probe.Runs;

        var started = DateTimeOffset.UtcNow;
        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => _client!.GetAsync("/api/health")));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.All(responses, r => Assert.Equal("no-store", r.Headers.CacheControl?.ToString()));
        foreach (var r in responses) r.Dispose();
        Assert.True(elapsed < HealthProbe.MaxAge, $"the burst took {elapsed.TotalMilliseconds} ms");
        Assert.Equal(1, probe.Runs - before);
    }

    [Fact]
    public async Task Health_is_503_until_ready()
    {
        var gate = _app!.Services.GetRequiredService<WmsfoReadinessGate>();
        gate.Reset();
        try
        {
            using var response = await _client!.GetAsync("/api/health");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }
        finally
        {
            gate.MarkReady();
        }
    }

    // ---------- host ----------

    private async Task StartHostAsync()
    {
        var connectionString = _fixture.ConnectionString;
        _options = new WmsfoOptions
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
            ApplicationName = typeof(A118ConnectionPoolTests).Assembly.GetName().Name,
        });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        var connections = TestConnections.For(connectionString);
        var options = _options;
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(connections);
        builder.Services.AddSingleton<IObjectStore>(new RecordingObjectStore());
        builder.Services.AddSingleton<IGatewayInternalClient>(new FakeGatewayClient());
        builder.Services.AddSingleton<NodeStateService>();
        builder.Services.AddSingleton<NodeCounters>();
        // The EF contexts use the superuser string so nothing they open can
        // carry either application name.
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
        builder.Services.AddSingleton<BeaconRateLimiter>();
        builder.Services.AddSingleton<LocationIngest>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<WmsfoReadinessGate>();
        builder.Services.AddSingleton<HealthMarkerLogger>();
        builder.Services.AddSingleton<HealthProbe>();

        builder.Services.ConfigureForwardedHeaders(options.TrustedProxyHops);
        builder.Services.AddWmsfoCors(options);
        builder.Services.AddWmsfoAuth(options);
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            foreach (var policy in RateLimitPolicyNames())
            {
                o.AddPolicy(policy, _ => RateLimitPartition.GetNoLimiter("no-limit"));
            }
        });

        var app = builder.Build();
        app.Services.GetRequiredService<WmsfoReadinessGate>().MarkReady();
        app.UseWmsfoPipeline();

        HealthEndpoint.Map(app);
        BeaconEndpoints.MapAll(app);
        RealtimeEndpoints.MapAll(app);
        PublicWriteEndpoints.MapAll(app);

        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses.First().TrimEnd('/');
        _app = app;
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    private static IEnumerable<string> RateLimitPolicyNames()
    {
        yield return RateLimitPolicies.LocationsPerBeacon;
        yield return RateLimitPolicies.HeartbeatPerBeacon;
        yield return RateLimitPolicies.BeaconLogsPerBeacon;
        yield return RateLimitPolicies.BeaconMePerBeacon;
        yield return RateLimitPolicies.BeaconStateReadPerBeacon;
        yield return RateLimitPolicies.BeaconStateWritePerBeacon;
        yield return RateLimitPolicies.EnrollPerIp;
        yield return RateLimitPolicies.ContactPerIp;
        yield return RateLimitPolicies.SubscriptionsPerIp;
        yield return RateLimitPolicies.PreviewPerIp;
        yield return RateLimitPolicies.CookiesPerPerson;
        yield return RateLimitPolicies.MeSubscribePerPerson;
        yield return RateLimitPolicies.MediaUploadUrlPerPerson;
        yield return RateLimitPolicies.AdminPerPerson;
    }

    // ---------- helpers ----------

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private async Task<HttpStatusCode> SendBeaconAsync(HttpMethod method, string path, string key, HttpContent? content)
    {
        using var req = new HttpRequestMessage(method, path) { Content = content };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        using var response = await _client!.SendAsync(req);
        return response.StatusCode;
    }

    private static void ClearPool(string connectionString)
    {
        using var conn = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(conn);
    }

    // Server connections in this test's database carrying the application name.
    private async Task<long> CountAsync(string applicationName)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select count(*) from pg_stat_activity where datname = current_database() and application_name = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = applicationName });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    // A cleared pool closes its idle connections; the server process ends a
    // moment later.
    private async Task WaitForCountAsync(string applicationName, long expected)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (await CountAsync(applicationName) != expected)
        {
            Assert.True(DateTime.UtcNow < deadline, $"{applicationName} connections never reached {expected}");
            await Task.Delay(50);
        }
    }

    private async Task<long> SeedBeaconAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        var key = Keys.MintKey();
        await using var cmd = new NpgsqlCommand(@"
insert into beacon (name, key_hash, key_prefix, is_active, min_interval_ms, created_by, updated_at)
values ('Helicopter', $1, $2, true, 0, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = key.Hash });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key.Prefix });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<(string token, string key)> MintEnrollmentTokenAsync(long beaconId)
    {
        var keyBytes = Convert.FromBase64String(_options!.EnrollmentEncryptionKey);
        var minted = Keys.MintKey();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var upd = new NpgsqlCommand(
            "update beacon set key_hash = $1, key_prefix = $2 where id = $3;", conn))
        {
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = minted.Hash });
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = minted.Prefix });
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
            await upd.ExecuteNonQueryAsync();
        }
        var enroll = Keys.MintEnrollmentToken();
        var cipher = Keys.Encrypt(keyBytes, minted.Token);
        await using (var cmd = new NpgsqlCommand(@"
insert into beacon_enrollment_token (beacon_id, token_hash, key_ciphertext, expires_at)
values ($1, $2, $3, now() + interval '15 minutes');", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = enroll.Hash });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = cipher });
            await cmd.ExecuteNonQueryAsync();
        }
        return (enroll.Token, minted.Token);
    }
}
