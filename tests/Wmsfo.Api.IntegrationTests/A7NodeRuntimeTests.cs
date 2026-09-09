using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Data;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.IntegrationTests;

// A7 acceptance criteria 750:
//   - snapshot transaction rolls back when the PUT fails
//   - identical data yields the same key with an incremented version
//   - the tick rewrite rule (a node that wrote for a location rewrites once on a
//     version change, another node does not)
//   - leader expiry
//   - first boot writes snapshot version 1 and the live object
//   - the live object bytes equal the published bytes
public sealed class A7NodeRuntimeTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public A7NodeRuntimeTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // The snapshot transaction rolls back when the PUT fails.
    [Fact]
    public async Task Snapshot_transaction_rolls_back_when_put_fails()
    {
        await MigrateAsync();
        var setup = await BuildAsync(failPut: true);

        // A snapshot row does not exist before first boot; insert one so we can
        // exercise the transactional rebuild path independently of bootstrap.
        await InsertBaselineSnapshotAsync();

        // Confirm version starts at 1.
        var before = await ReadSnapshotVersionAsync();
        Assert.Equal(1, before);

        var thrown = await Assert.ThrowsAsync<Wmsfo.Api.Http.ApiException>(async () =>
            await setup.Builder.RebuildAsync(default));
        Assert.Equal(502, thrown.StatusCode);
        Assert.Equal("snapshot_write_failed", thrown.Code);

        var after = await ReadSnapshotVersionAsync();
        Assert.Equal(1, after);   // No increment - the transaction rolled back.
    }

    // Identical data yields the same key with an incremented version.
    [Fact]
    public async Task Identical_data_yields_same_key_and_incremented_version()
    {
        await MigrateAsync();
        var setup = await BuildAsync(failPut: false);
        await setup.Bootstrap.EnsureVersionOneAsync(default);

        var first = await ReadSnapshotRowAsync();
        var firstKey = first.S3Key;
        Assert.Equal(1, first.Version);

        var info = await setup.Builder.RebuildAsync(default);
        Assert.Equal(2, info.Version);
        Assert.Equal(firstKey, info.Key);
    }

    // First boot writes snapshot version 1 and the live object.
    // Live-object bytes equal the published bytes.
    [Fact]
    public async Task First_boot_writes_snapshot_v1_and_live_object_with_matching_bytes()
    {
        await MigrateAsync();
        var setup = await BuildAsync(failPut: false);

        await setup.Bootstrap.EnsureVersionOneAsync(default);

        // Snapshot row inserted with version 1.
        var row = await ReadSnapshotRowAsync();
        Assert.Equal(1, row.Version);
        Assert.StartsWith("snapshots/", row.S3Key);

        // Snapshot object landed in the store with the immutable cache header.
        var snapHead = await setup.Store.HeadObjectAsync(row.S3Key);
        Assert.NotNull(snapHead);
        Assert.Equal("application/json; charset=utf-8", snapHead!.ContentType);

        // Live-object write from state, after bootstrap.
        await setup.State.RefreshAsync("boot", default);
        await setup.Writer.WriteFromStateAsync("boot", default);

        // Bytes stored at live/location.json equal the bytes handed to the gateway publish.
        var live = await setup.Store.GetObjectAsync("live/location.json");
        Assert.NotNull(live);
        Assert.NotNull(setup.Writer.LastWrittenBytes);
        Assert.Equal(setup.Writer.LastWrittenBytes, live!.Bytes);
        // And the gateway saw the same bytes.
        Assert.NotNull(setup.Gateway.LastPublished);
        Assert.Equal(setup.Writer.LastWrittenBytes, setup.Gateway.LastPublished);
    }

    // The tick rewrite rule: a node that wrote for a location rewrites once on
    // a version change; another node does not.
    [Fact]
    public async Task Tick_rewrite_rule_only_the_ingest_node_rewrites()
    {
        await MigrateAsync();
        var ingest = await BuildAsync(failPut: false);
        var admin = await BuildAsync(failPut: false, storeName: "admin");

        await ingest.Bootstrap.EnsureVersionOneAsync(default);
        await ingest.State.RefreshAsync("boot", default);
        await admin.State.RefreshAsync("boot", default);

        // Simulate the ingest node writing the live object for a stored location
        // (this is what WriteForLocation does after the location transaction).
        ingest.State.MarkWroteForLocation();
        ingest.State.RecordWroteVersion(ingest.State.Current.SnapshotVersion);
        admin.State.RecordWroteVersion(admin.State.Current.SnapshotVersion);

        // Move the snapshot version (as a snapshot-affecting write does).
        await BumpSnapshotVersionAsync();

        // Both nodes tick.
        var ingestPutsBefore = ingest.Store.PutCount("live/location.json");
        var adminPutsBefore = admin.Store.PutCount("live/location.json");
        await ingest.Tick.TickOnceAsync(default);
        await admin.Tick.TickOnceAsync(default);
        var ingestPutsAfter = ingest.Store.PutCount("live/location.json");
        var adminPutsAfter = admin.Store.PutCount("live/location.json");

        Assert.Equal(ingestPutsBefore + 1, ingestPutsAfter);
        Assert.Equal(adminPutsBefore, adminPutsAfter);
        // Flag cleared on the ingest node so a subsequent tick does not rewrite again.
        Assert.False(ingest.State.WroteForLocationSinceVersionChange);
    }

    // Leader expiry: a leader answer older than 90 s is not currently leader. The
    // gateway refreshes evaluatedAt on its 30 s reconcile loop, so an answer from
    // the previous loop is still current.
    [Fact]
    public void Leader_expires_ninety_seconds_after_last_answer()
    {
        var now = DateTimeOffset.UtcNow;
        var fresh = new LeaderStatus(IsLeader: true, EvaluatedAt: now, InstanceId: "i-1");
        var lastLoop = new LeaderStatus(IsLeader: true, EvaluatedAt: now - TimeSpan.FromSeconds(35), InstanceId: "i-1");
        var stale = new LeaderStatus(IsLeader: true, EvaluatedAt: now - TimeSpan.FromSeconds(91), InstanceId: "i-1");

        Assert.True(fresh.IsCurrentlyLeader(now));
        Assert.True(lastLoop.IsCurrentlyLeader(now));
        Assert.False(stale.IsCurrentlyLeader(now));
        // Also, at exactly 90 s, expiry has kicked in (< 90 s, strict).
        Assert.False(new LeaderStatus(true, now - TimeSpan.FromSeconds(90), null).IsCurrentlyLeader(now));
    }

    // --- Helpers ---

    private async Task MigrateAsync()
    {
        var options = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(options);
        await db.Database.MigrateAsync();
        // Reset rows this suite writes so tests run in isolation despite sharing
        // the class-scoped fixture (IClassFixture creates one database per class).
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from snapshot;",
            "delete from content_version;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<Setup> BuildAsync(bool failPut, string storeName = "cdn")
    {
        var options = TestOptions();
        var connections = new TestConnections(_fixture.ConnectionString);
        var store = new RecordingObjectStore(failPut);
        var gateway = new FakeGatewayClient();
        var iconLibrary = IconLibrary.Load(TestPaths.IconsDir, options.CdnBaseUrl);
        var builder = new SnapshotBuilder(store, iconLibrary, options, NullLogger<SnapshotBuilder>.Instance);
        var bootstrap = new SnapshotBootstrap(builder, connections, options, NullLogger<SnapshotBootstrap>.Instance);
        var state = new NodeStateService(connections, NullLogger<NodeStateService>.Instance);
        var writer = new LiveObjectWriter(store, gateway, state, connections, options, new NodeCounters(), NullLogger<LiveObjectWriter>.Instance);
        var readiness = new WmsfoReadinessGate();
        readiness.MarkReady();
        var tick = new ReconcileTick(state, writer, options, readiness, NullLogger<ReconcileTick>.Instance);
        return await Task.FromResult(new Setup(options, store, gateway, builder, bootstrap, state, writer, tick));
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

    private async Task InsertBaselineSnapshotAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        // Ensure content_version exists so the snapshot builder finds one.
        await using (var check = new NpgsqlCommand("select 1 from content_version limit 1;", conn))
        {
            var r = await check.ExecuteScalarAsync();
            if (r is null)
            {
                var doc = FixtureData.BuildContentDocument();
                var bytes = CanonicalJson.SerializeToUtf8Bytes(doc);
                var sha = CanonicalJson.Sha256Hex(bytes);
                await using var ins = new NpgsqlCommand(@"
insert into content_version (document, sha256, media_ids, label, published_by)
values ($1::jsonb, $2, '{}'::uuid[], 'seed', 'seed');", conn);
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Encoding.UTF8.GetString(bytes) });
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
                await ins.ExecuteNonQueryAsync();
            }
        }
        await using (var cmd = new NpgsqlCommand(
            "insert into snapshot (id, version, url, s3_key, built_at) values (1, 1, 'https://cdn.example/snapshots/seed.json', 'snapshots/seed.json', now()) on conflict (id) do nothing;", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private async Task<long> ReadSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version from snapshot where id = 1;", conn);
        var r = await cmd.ExecuteScalarAsync();
        return r is null ? 0 : Convert.ToInt64(r);
    }

    private async Task<(long Version, string S3Key, string Url)> ReadSnapshotRowAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version, s3_key, url from snapshot where id = 1;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetString(1), reader.GetString(2));
    }

    private async Task BumpSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update snapshot set version = version + 1, built_at = now() where id = 1;", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed record Setup(
        WmsfoOptions Options,
        RecordingObjectStore Store,
        FakeGatewayClient Gateway,
        SnapshotBuilder Builder,
        SnapshotBootstrap Bootstrap,
        NodeStateService State,
        LiveObjectWriter Writer,
        ReconcileTick Tick);
}

// A minimal WmsfoConnectionStrings substitute for tests.
public sealed class TestConnections : WmsfoConnectionStrings
{
    public TestConnections(string connectionString) : base(connectionString, connectionString) { }
}

public sealed class RecordingObjectStore : IObjectStore
{
    public bool FailPut { get; set; }
    private readonly Dictionary<string, (byte[] Bytes, string ContentType, string CacheControl, string? Tag)> _objects = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _puts = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public RecordingObjectStore(bool failPut = false) { FailPut = failPut; }

    public int PutCount(string key)
    {
        lock (_lock) return _puts.TryGetValue(key, out var v) ? v : 0;
    }

    public Task PutObjectAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl, string? tag = null, CancellationToken cancellationToken = default)
    {
        if (FailPut) throw new InvalidOperationException("simulated PUT failure");
        lock (_lock)
        {
            _objects[key] = (bytes.ToArray(), contentType, cacheControl, tag);
            _puts[key] = (_puts.TryGetValue(key, out var c) ? c : 0) + 1;
        }
        return Task.CompletedTask;
    }

    public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_lock) _objects.Remove(key);
        return Task.CompletedTask;
    }

#pragma warning disable CS1998
    public async IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<ObjectListEntry> matches;
        lock (_lock)
        {
            matches = _objects.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(kv => new ObjectListEntry(kv.Key, kv.Value.Bytes.LongLength)).ToList();
        }
        foreach (var m in matches) yield return m;
    }
#pragma warning restore CS1998

    public Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);

    public Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_objects.TryGetValue(key, out var v)) return Task.FromResult<ObjectHead?>(null);
            return Task.FromResult<ObjectHead?>(new ObjectHead(v.Bytes.LongLength, v.ContentType, v.CacheControl));
        }
    }

    public Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            if (!_objects.TryGetValue(key, out var v)) return Task.FromResult<ObjectContent?>(null);
            return Task.FromResult<ObjectContent?>(new ObjectContent(v.Bytes.ToArray(), v.ContentType));
        }
    }

    public string PresignPut(string key, string contentType, string tag) => "https://example/local-upload";
}

public sealed class FakeGatewayClient : IGatewayInternalClient
{
    public byte[]? LastPublished { get; private set; }
    public string LastChannel { get; private set; } = "";
    public string LastEvent { get; private set; } = "";
    public string? LastInstanceId => "instance-fake";

    public Task<bool> PublishAsync(string channel, string @event, ReadOnlyMemory<byte> payloadBytes, CancellationToken ct)
    {
        LastChannel = channel;
        LastEvent = @event;
        LastPublished = payloadBytes.ToArray();
        return Task.FromResult(true);
    }

    public Task<LeaderAnswer> GetLeaderAsync(CancellationToken ct) => Task.FromResult(new LeaderAnswer(false, null, null, false));
    public Task<IReadOnlyList<string>?> GetPresenceAsync(string channel, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
}
