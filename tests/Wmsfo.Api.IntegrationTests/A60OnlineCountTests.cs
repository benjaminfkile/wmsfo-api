using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.IntegrationTests;

// contracts 1.2 and 7.4: the live object's onlineCount is the gateway's count
// for the site's location channel while the current event is live and the hub
// is enabled, null otherwise; a failed count read writes null and the write
// still lands; the read is cached for one second.
public sealed class A60OnlineCountTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public A60OnlineCountTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Live_event_live_object_carries_online_count()
    {
        await PrepareAsync(statusId: 3, hubEnabled: true);
        var setup = await BuildAsync();
        setup.Gateway.SetPresenceCount(setup.Options.ServiceName + ":location", 37);

        await setup.Writer.WriteFromStateAsync("test", default);

        Assert.Equal(37, setup.Writer.LastWrittenObject!.OnlineCount);
        var live = await setup.Store.GetObjectAsync("live/location.json");
        Assert.NotNull(live);
        Assert.EndsWith(",\"onlineCount\":37}", Encoding.UTF8.GetString(live!.Bytes));
    }

    [Fact]
    public async Task Count_read_failing_writes_null_and_the_write_lands()
    {
        await PrepareAsync(statusId: 3, hubEnabled: true);
        var setup = await BuildAsync();
        setup.Gateway.ThrowOnCount = true;
        var putsBefore = setup.Store.PutCount("live/location.json");

        await setup.Writer.WriteFromStateAsync("test", default);

        Assert.Equal(putsBefore + 1, setup.Store.PutCount("live/location.json"));
        Assert.Equal(1, setup.Gateway.CountCalls);
        Assert.Null(setup.Writer.LastWrittenObject!.OnlineCount);
        var live = await setup.Store.GetObjectAsync("live/location.json");
        Assert.EndsWith(",\"onlineCount\":null}", Encoding.UTF8.GetString(live!.Bytes));
    }

    [Fact]
    public async Task Count_answer_missing_writes_null()
    {
        await PrepareAsync(statusId: 3, hubEnabled: true);
        var setup = await BuildAsync();
        setup.Gateway.SetPresenceCount(setup.Options.ServiceName + ":location", null);

        await setup.Writer.WriteFromStateAsync("test", default);

        Assert.Equal(1, setup.Gateway.CountCalls);
        Assert.Null(setup.Writer.LastWrittenObject!.OnlineCount);
    }

    [Fact]
    public async Task Non_live_event_writes_null_without_a_count_read()
    {
        await PrepareAsync(statusId: 2, hubEnabled: true);
        var setup = await BuildAsync();
        setup.Gateway.SetPresenceCount(setup.Options.ServiceName + ":location", 12);

        await setup.Writer.WriteFromStateAsync("test", default);

        Assert.Equal(0, setup.Gateway.CountCalls);
        Assert.Null(setup.Writer.LastWrittenObject!.OnlineCount);
    }

    [Fact]
    public async Task Hub_disabled_writes_null_without_a_count_read()
    {
        await PrepareAsync(statusId: 3, hubEnabled: false);
        var setup = await BuildAsync();
        setup.Gateway.SetPresenceCount(setup.Options.ServiceName + ":location", 12);

        await setup.Writer.WriteFromStateAsync("test", default);

        Assert.Equal(0, setup.Gateway.CountCalls);
        Assert.Null(setup.Writer.LastWrittenObject!.OnlineCount);
    }

    [Fact]
    public async Task Writes_within_one_second_share_one_count_read()
    {
        await PrepareAsync(statusId: 3, hubEnabled: true);
        var setup = await BuildAsync();
        var channel = setup.Options.ServiceName + ":location";
        setup.Gateway.SetPresenceCount(channel, 5);

        await setup.Writer.WriteFromStateAsync("first", default);
        setup.Gateway.SetPresenceCount(channel, 6);
        await setup.Writer.WriteFromStateAsync("second", default);
        await setup.Writer.WriteFromStateAsync("third", default);

        Assert.Equal(1, setup.Gateway.CountCalls);
        Assert.Equal(5, setup.Writer.LastWrittenObject!.OnlineCount);

        await Task.Delay(LiveObjectWriter.OnlineCountCacheFor + TimeSpan.FromMilliseconds(100));
        await setup.Writer.WriteFromStateAsync("fourth", default);

        Assert.Equal(2, setup.Gateway.CountCalls);
        Assert.Equal(6, setup.Writer.LastWrittenObject!.OnlineCount);
    }

    // --- Helpers ---

    // Migrates, then leaves exactly one current event with the given status and
    // sets hub_enabled.
    private async Task PrepareAsync(short statusId, bool hubEnabled)
    {
        var options = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "update event set is_current = false, status_id = 1;",
            "delete from snapshot;",
            "delete from content_version;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await using (var ins = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
select coalesce(max(year), 2026) + 1, 'online count', $1, true, 'seed', now() from event;", conn))
        {
            ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = statusId });
            await ins.ExecuteNonQueryAsync();
        }

        await using (var set = new NpgsqlCommand(
            "update app_setting set value = $1::jsonb where key = 'hub_enabled';", conn))
        {
            set.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = hubEnabled ? "true" : "false" });
            await set.ExecuteNonQueryAsync();
        }
    }

    private async Task<Setup> BuildAsync()
    {
        var options = TestOptions();
        var connections = TestConnections.For(_fixture.ConnectionString);
        var store = new RecordingObjectStore();
        var gateway = new ScriptedGatewayClient();
        var iconLibrary = IconLibrary.Load(TestPaths.IconsDir, options.CdnBaseUrl);
        var builder = new SnapshotBuilder(store, iconLibrary, options, NullLogger<SnapshotBuilder>.Instance);
        var state = new NodeStateService(connections, NullLogger<NodeStateService>.Instance);
        var writer = new LiveObjectWriter(store, gateway, state, connections, options, new NodeCounters(), NullLogger<LiveObjectWriter>.Instance);
        var bootstrap = new SnapshotBootstrap(builder, connections, options, NullLogger<SnapshotBootstrap>.Instance);
        await bootstrap.EnsureVersionOneAsync(default);
        return new Setup(options, store, gateway, writer);
    }

    private WmsfoOptions TestOptions() => new()
    {
        Env = "dev",
        ServiceName = "wmsfo-api-test",
        DbConnection = _fixture.ConnectionString,
        DbMigrationConnection = _fixture.ConnectionString,
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
        SesFromAddress = "alerts@example.com",
        ContactNotifyEmail = "inbox@example.com",
        AlertSendPerSec = 10,
        EnrollmentEncryptionKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
        ReconcileTickMs = 1000,
        LogLevel = "Information",
    };

    private sealed record Setup(
        WmsfoOptions Options,
        RecordingObjectStore Store,
        ScriptedGatewayClient Gateway,
        LiveObjectWriter Writer);
}
