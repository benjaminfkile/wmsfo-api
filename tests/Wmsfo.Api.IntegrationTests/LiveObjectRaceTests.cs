using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// contracts 1.8, api.md 9 and 10.1: the two-node race between an admin status
// change on one node and a published fix on the other, replayed against
// Postgres with one object store shared by both nodes (the CDN). Found on dev
// 2026-10-24: going live on node B while node A's fix landed inside the same
// second left the CDN saying status 1 next to a live position, and nothing
// corrected it because the tick ran while A's PUT was in flight.
//
// Node A is the ingest node (its cache lags), node B takes the admin write.
public sealed class LiveObjectRaceTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public LiveObjectRaceTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    // Going live. B commits 1 -> 3 and writes status 3. A's fix transaction
    // saw status 3 at the new version, but A's memory still says 1. Before the
    // fix A built from memory and its PUT, landing after B's, put status 1
    // back on the CDN.
    [Fact]
    public async Task A_fix_on_the_other_node_right_after_going_live_publishes_status_3()
    {
        var cdn = new SharedCdn();
        var eventId = await ResetAsync(statusId: 1);
        var a = await NodeAsync(cdn);
        var b = await NodeAsync(cdn);
        await a.Bootstrap.EnsureVersionOneAsync(default);
        await a.State.RefreshAsync("boot", default);
        await b.State.RefreshAsync("boot", default);
        var before = a.State.Current.SnapshotVersion;
        Assert.Equal((short)1, a.State.Current.CurrentEvent!.StatusId);

        // B: the status change commits and B writes from refreshed state.
        var version = await CommitStatusAsync(eventId, 3);
        b.State.RecordWroteVersion(version);
        await b.Writer.WriteFromStateAsync("admin", default);
        Assert.Equal(3, cdn.LiveStatus());

        // A: a fix whose transaction saw the new version, while A's memory is
        // still at the old one.
        Assert.Equal(before, a.State.Current.SnapshotVersion);
        var puts = cdn.LivePuts;
        await a.Writer.WriteForLocationAsync(FixFields(eventId, statusId: 3, version, await SnapshotUrlAsync(), seq: 1), default);

        Assert.Equal(puts + 1, cdn.LivePuts);
        Assert.Equal(3, cdn.LiveStatus());
        Assert.Equal(1, cdn.LiveSeq());
        Assert.Equal(await SnapshotUrlAsync(), cdn.LiveSnapshotUrl());
        // A refreshed its memory on the way, so its next write is right too.
        Assert.Equal(version, a.State.Current.SnapshotVersion);
        Assert.Equal((short)3, a.State.Current.CurrentEvent!.StatusId);
    }

    // Ending, with A's PUT in flight. A's fix transaction honestly saw status 3
    // at the old version and committed before the end; its PUT is still in
    // flight when B ends the event and writes status 4, and when A's tick
    // observes the new version. The tick must count the in-flight write and
    // the rewrite must land after it, from refreshed state, because after the
    // end no fix is accepted and nothing else would correct the CDN.
    [Fact]
    public async Task A_fix_in_flight_across_the_end_is_corrected_by_the_tick_rewrite()
    {
        var cdn = new SharedCdn();
        var eventId = await ResetAsync(statusId: 3);
        var a = await NodeAsync(cdn);
        var b = await NodeAsync(cdn);
        await a.Bootstrap.EnsureVersionOneAsync(default);
        await a.State.RefreshAsync("boot", default);
        await b.State.RefreshAsync("boot", default);
        var oldVersion = a.State.Current.SnapshotVersion;
        a.State.RecordWroteVersion(oldVersion);
        b.State.RecordWroteVersion(oldVersion);

        // A's fix: its transaction committed (event.latest_fix carries seq 7)
        // and the PUT blocks on the gate.
        await StoreLatestFixAsync(eventId, seq: 7);
        var gate = cdn.GateNextLivePut();
        var fix = a.Writer.WriteForLocationAsync(FixFields(eventId, statusId: 3, oldVersion, await SnapshotUrlAsync(), seq: 7), default);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fix.IsCompleted);

        // B ends the event and writes status 4.
        var newVersion = await CommitStatusAsync(eventId, 4);
        b.State.RecordWroteVersion(newVersion);
        await b.Writer.WriteFromStateAsync("admin", default);
        Assert.Equal(4, cdn.LiveStatus());

        // A's tick runs while its PUT is still in flight.
        await a.Tick.TickOnceAsync(default);
        Assert.Equal(newVersion, a.State.LastWrittenVersion);

        // The stale PUT lands, then the coalesced rewrite.
        var putsBeforeRelease = cdn.LivePuts;
        gate.Release.SetResult();
        await fix.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(putsBeforeRelease + 2, cdn.LivePuts);
        Assert.Equal(3, cdn.LiveStatusAt(putsBeforeRelease));       // the stale write landed
        Assert.Equal(4, cdn.LiveStatusAt(putsBeforeRelease + 1));   // and was corrected at once
        Assert.Equal(4, cdn.LiveStatus());
        Assert.Equal(7, cdn.LiveSeq());                              // the fix itself is kept
        // A further tick changes nothing: the version is handled.
        var puts = cdn.LivePuts;
        await a.Tick.TickOnceAsync(default);
        Assert.Equal(puts, cdn.LivePuts);
    }

    // Ending, with A's fix fully landed before the end. B's write is the last
    // one and A's next tick rewrites once more from refreshed state (the
    // existing rule); the CDN ends on status 4 either way.
    [Fact]
    public async Task A_fix_landed_before_the_end_leaves_the_end_in_place()
    {
        var cdn = new SharedCdn();
        var eventId = await ResetAsync(statusId: 3);
        var a = await NodeAsync(cdn);
        var b = await NodeAsync(cdn);
        await a.Bootstrap.EnsureVersionOneAsync(default);
        await a.State.RefreshAsync("boot", default);
        await b.State.RefreshAsync("boot", default);
        var oldVersion = a.State.Current.SnapshotVersion;
        a.State.RecordWroteVersion(oldVersion);
        b.State.RecordWroteVersion(oldVersion);

        await StoreLatestFixAsync(eventId, seq: 8);
        await a.Writer.WriteForLocationAsync(FixFields(eventId, statusId: 3, oldVersion, await SnapshotUrlAsync(), seq: 8), default);
        Assert.Equal(3, cdn.LiveStatus());

        var newVersion = await CommitStatusAsync(eventId, 4);
        b.State.RecordWroteVersion(newVersion);
        await b.Writer.WriteFromStateAsync("admin", default);
        Assert.Equal(4, cdn.LiveStatus());

        await a.Tick.TickOnceAsync(default);
        Assert.Equal(4, cdn.LiveStatus());
        Assert.Equal(8, cdn.LiveSeq());
    }

    // A settings change on B (hub off) followed by a fix on A inside the same
    // tick: A refreshes because the fix's version is newer, so the hub flag
    // and poll interval in the object are the new ones too.
    [Fact]
    public async Task A_fix_after_a_settings_write_on_the_other_node_carries_the_new_settings()
    {
        var cdn = new SharedCdn();
        var eventId = await ResetAsync(statusId: 3);
        var a = await NodeAsync(cdn);
        var b = await NodeAsync(cdn);
        await a.Bootstrap.EnsureVersionOneAsync(default);
        await a.State.RefreshAsync("boot", default);
        await b.State.RefreshAsync("boot", default);
        Assert.True(a.State.Current.Settings.HubEnabled);

        var version = await CommitSettingAsync("hub_enabled", "false");
        await CommitSettingAsync("poll_interval_ms", "7000");
        version = await ReadSnapshotVersionAsync();
        b.State.RecordWroteVersion(version);
        await b.Writer.WriteFromStateAsync("admin", default);

        await a.Writer.WriteForLocationAsync(FixFields(eventId, statusId: 3, version, await SnapshotUrlAsync(), seq: 2), default);

        var live = cdn.Live();
        Assert.False(live.GetProperty("hubEnabled").GetBoolean());
        Assert.Equal(7000, live.GetProperty("pollIntervalMs").GetInt32());
        Assert.Equal(2, live.GetProperty("seq").GetInt64());
    }

    // --- Helpers ---

    private async Task<long> ResetAsync(short statusId)
    {
        var options = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(options))
            await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from location;",
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from event;",
            "delete from snapshot;",
            "delete from content_version;",
            "update app_setting set value = 'true' where key = 'hub_enabled';",
            "update app_setting set value = '5000' where key = 'poll_interval_ms';",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var year = 3000 + Random.Shared.Next(1, 90);
        await using var insert = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, $3, true, 'seed', now()) returning id;", conn);
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Race {year}" });
        insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = statusId });
        return (long)(await insert.ExecuteScalarAsync() ?? 0L);
    }

    // The status change as the admin frame commits it: the event row and the
    // snapshot version move in one transaction. Returns the new version.
    private async Task<long> CommitStatusAsync(long eventId, short statusId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var evt = new NpgsqlCommand("update event set status_id = $2, updated_at = now() where id = $1;", conn, tx))
        {
            evt.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            evt.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = statusId });
            await evt.ExecuteNonQueryAsync();
        }
        long version;
        await using (var bump = new NpgsqlCommand("update snapshot set version = version + 1, built_at = now() where id = 1 returning version;", conn, tx))
        {
            version = (long)(await bump.ExecuteScalarAsync() ?? 0L);
        }
        await tx.CommitAsync();
        return version;
    }

    private async Task<long> CommitSettingAsync(string key, string value)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand("update app_setting set value = $2::jsonb, updated_at = now() where key = $1;", conn, tx))
        {
            set.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
            set.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = value });
            await set.ExecuteNonQueryAsync();
        }
        long version;
        await using (var bump = new NpgsqlCommand("update snapshot set version = version + 1, built_at = now() where id = 1 returning version;", conn, tx))
        {
            version = (long)(await bump.ExecuteScalarAsync() ?? 0L);
        }
        await tx.CommitAsync();
        return version;
    }

    // What the location transaction leaves behind for a stored fix: the event
    // row's latest_fix, which a rewrite from state reads (contracts 7.2).
    private async Task StoreLatestFixAsync(long eventId, long seq)
    {
        var now = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(new
        {
            seq,
            beaconId = 1L,
            lat = 46.87,
            lng = -114.0,
            speedMps = 10.0,
            altitudeM = 1000.0,
            headingDeg = 45.0,
            accuracyM = 5.0,
            recordedAt = now.ToString("o"),
            receivedAt = now.ToString("o"),
        });
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "update event set latest_fix = $2::jsonb, next_seq = $3, updated_at = now() where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = json });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = seq + 1 });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ReadSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select version from snapshot where id = 1;", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<string> SnapshotUrlAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select url from snapshot where id = 1;", conn);
        return (string)(await cmd.ExecuteScalarAsync() ?? "");
    }

    private static LocationSourceFields FixFields(long eventId, short statusId, long version, string url, long seq) => new(
        EventId: eventId,
        EventStatusId: statusId,
        SnapshotVersion: version,
        SnapshotUrl: url,
        Seq: seq,
        Lat: 46.87,
        Lng: -114.0,
        SpeedMps: 10,
        AltitudeM: 1000,
        HeadingDeg: 45,
        AccuracyM: 5,
        RecordedAt: DateTimeOffset.UtcNow,
        ReceivedAt: DateTimeOffset.UtcNow);

    private async Task<Node> NodeAsync(SharedCdn cdn)
    {
        var options = new WmsfoOptions
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
        var connections = TestConnections.For(_fixture.ConnectionString);
        var gateway = new FakeGatewayClient();
        var iconLibrary = IconLibrary.Load(TestPaths.IconsDir, options.CdnBaseUrl);
        var builder = new SnapshotBuilder(cdn, iconLibrary, options, NullLogger<SnapshotBuilder>.Instance);
        var state = new NodeStateService(connections, NullLogger<NodeStateService>.Instance);
        var writer = new LiveObjectWriter(cdn, gateway, state, connections, options, new NodeCounters(), NullLogger<LiveObjectWriter>.Instance);
        var bootstrap = new SnapshotBootstrap(builder, connections, options, NullLogger<SnapshotBootstrap>.Instance, liveObjectWriter: writer);
        var readiness = new WmsfoReadinessGate();
        readiness.MarkReady();
        var tick = new ReconcileTick(state, writer, options, readiness, NullLogger<ReconcileTick>.Instance);
        return await Task.FromResult(new Node(state, writer, tick, bootstrap));
    }

    private sealed record Node(NodeStateService State, LiveObjectWriter Writer, ReconcileTick Tick, SnapshotBootstrap Bootstrap);

    // One store for both nodes, standing in for the bucket behind the CDN. It
    // keeps every live-object write in order and can hold the next one on a
    // gate so a test can interleave the other node's write and a tick.
    private sealed class SharedCdn : IObjectStore
    {
        private readonly RecordingObjectStore _inner = new();
        private readonly List<byte[]> _liveWrites = new();
        private readonly object _lock = new();
        private LiveGate? _gate;

        public sealed class LiveGate
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public LiveGate GateNextLivePut()
        {
            var gate = new LiveGate();
            lock (_lock) _gate = gate;
            return gate;
        }

        public int LivePuts { get { lock (_lock) return _liveWrites.Count; } }

        public JsonElement Live() => LiveAt(LivePuts - 1);

        public JsonElement LiveAt(int index)
        {
            byte[] bytes;
            lock (_lock) bytes = _liveWrites[index];
            return JsonDocument.Parse(bytes).RootElement.Clone();
        }

        public int LiveStatus() => Live().GetProperty("eventStatusId").GetInt32();
        public int LiveStatusAt(int index) => LiveAt(index).GetProperty("eventStatusId").GetInt32();
        public long LiveSeq() => Live().GetProperty("seq").GetInt64();
        public string LiveSnapshotUrl() => Live().GetProperty("snapshotUrl").GetString() ?? "";

        public async Task PutObjectAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl, string? tag = null, CancellationToken cancellationToken = default)
        {
            if (key == LiveObjectWriter.CdnKey)
            {
                LiveGate? gate;
                lock (_lock)
                {
                    gate = _gate;
                    _gate = null;
                }
                if (gate is not null)
                {
                    gate.Entered.TrySetResult();
                    await gate.Release.Task.WaitAsync(cancellationToken);
                }
                lock (_lock) _liveWrites.Add(bytes.ToArray());
            }
            await _inner.PutObjectAsync(key, bytes, contentType, cacheControl, tag, cancellationToken);
        }

        public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteObjectAsync(key, cancellationToken);
        public IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(string prefix, CancellationToken cancellationToken = default) => _inner.ListPrefixAsync(prefix, cancellationToken);
        public Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default) => _inner.PutObjectTaggingAsync(key, tag, cancellationToken);
        public Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteObjectTaggingAsync(key, cancellationToken);
        public Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default) => _inner.GetObjectTaggingAsync(key, cancellationToken);
        public Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default) => _inner.HeadObjectAsync(key, cancellationToken);
        public Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default) => _inner.GetObjectAsync(key, cancellationToken);
        public string PresignPut(string key, string contentType, string tag) => _inner.PresignPut(key, contentType, tag);
    }
}
