using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Wmsfo.Api.Config;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.Tests;

// contracts 1.8, api.md 9 and 10.1: a stored location's live object carries
// the event status and snapshot URL the location transaction read, not the
// node's cached copy, and a location write counts for the tick's rewrite rule
// from the moment it is requested, not from the moment its PUT lands. The
// two-node race itself is replayed against Postgres in the integration suite
// (LiveObjectRaceTests); these cover the pure rules.
public class LiveObjectStaleStatusTests
{
    private const long EventId = 41;

    [Fact]
    public void The_transaction_fields_win_when_their_version_is_newer_than_memory()
    {
        // Memory: version 10, status 1 (planned), old snapshot URL. The
        // transaction saw version 11 with status 3 and the new URL.
        var state = Snapshot(version: 10, statusId: 1, url: "https://cdn.example/snapshots/old.json");
        var location = Fields(version: 11, statusId: 3, url: "https://cdn.example/snapshots/new.json");

        var (obj, _) = Writer(state).Build(state, location);

        Assert.Equal(EventId, obj.EventId);
        Assert.Equal(3, obj.EventStatusId);
        Assert.Equal("https://cdn.example/snapshots/new.json", obj.SnapshotUrl);
        Assert.Equal(location.Seq, obj.Seq);
        Assert.Equal(location.Lat, obj.Lat);
    }

    [Fact]
    public void The_transaction_fields_win_on_an_equal_version()
    {
        var state = Snapshot(version: 10, statusId: 3, url: "https://cdn.example/snapshots/same.json");
        var location = Fields(version: 10, statusId: 3, url: "https://cdn.example/snapshots/same.json");

        var (obj, _) = Writer(state).Build(state, location);

        Assert.Equal(3, obj.EventStatusId);
        Assert.Equal("https://cdn.example/snapshots/same.json", obj.SnapshotUrl);
        Assert.Equal(location.Seq, obj.Seq);
    }

    [Fact]
    public void Memory_wins_when_it_is_newer_than_the_transaction_and_the_fix_still_rides_along()
    {
        // An admin write on this node committed after the location transaction
        // and refreshed memory to version 12 with status 4 (ended). The fix was
        // for the same event, so its position is still the newest one.
        var state = Snapshot(version: 12, statusId: 4, url: "https://cdn.example/snapshots/ended.json");
        var location = Fields(version: 11, statusId: 3, url: "https://cdn.example/snapshots/live.json");

        var (obj, _) = Writer(state).Build(state, location);

        Assert.Equal(4, obj.EventStatusId);
        Assert.Equal("https://cdn.example/snapshots/ended.json", obj.SnapshotUrl);
        Assert.Equal(location.Seq, obj.Seq);
        Assert.Equal(location.Lat, obj.Lat);
    }

    [Fact]
    public void Memory_wins_and_drops_the_fix_when_it_is_newer_and_holds_another_event()
    {
        var state = Snapshot(version: 12, statusId: 2, url: "https://cdn.example/snapshots/next.json") with
        {
            CurrentEvent = new CurrentEvent(EventId + 1, 2, null),
            LatestPublished = null,
        };
        var location = Fields(version: 11, statusId: 3, url: "https://cdn.example/snapshots/live.json");

        var (obj, _) = Writer(state).Build(state, location);

        Assert.Equal(EventId + 1, obj.EventId);
        Assert.Equal(2, obj.EventStatusId);
        Assert.Null(obj.Seq);
        Assert.Null(obj.Lat);
    }

    [Fact]
    public async Task A_location_write_counts_for_the_tick_rewrite_before_its_put_lands()
    {
        var connections = WmsfoConnectionStrings.ForTests("Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=1");
        var options = new WmsfoOptions { ServiceName = "wmsfo-api-test" };
        var state = new NodeStateService(connections, NullLogger<NodeStateService>.Instance);
        var store = new GatedStore();
        var writer = new LiveObjectWriter(
            store, new NullGateway(), state, connections, options,
            new NodeCounters(), NullLogger<LiveObjectWriter>.Instance);

        Assert.False(state.WroteForLocationSinceVersionChange);

        // Memory is at version 0 (never refreshed) and so are the fields, so the
        // writer does not try to refresh from the (absent) database.
        var write = writer.WriteForLocationAsync(Fields(version: 0, statusId: 3, url: ""), CancellationToken.None);
        await store.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The PUT is in flight and the flag is already up: a tick running now
        // would ask for the rewrite instead of recording the version as handled.
        Assert.False(write.IsCompleted);
        Assert.True(state.WroteForLocationSinceVersionChange);

        store.ReleasePut.SetResult();
        await write.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, store.Puts);
    }

    [Fact]
    public async Task A_rewrite_requested_while_a_location_write_is_in_flight_lands_right_after_it()
    {
        var connections = WmsfoConnectionStrings.ForTests("Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=1");
        var options = new WmsfoOptions { ServiceName = "wmsfo-api-test" };
        var state = new NodeStateService(connections, NullLogger<NodeStateService>.Instance);
        var store = new GatedStore();
        var writer = new LiveObjectWriter(
            store, new NullGateway(), state, connections, options,
            new NodeCounters(), NullLogger<LiveObjectWriter>.Instance);

        var write = writer.WriteForLocationAsync(Fields(version: 0, statusId: 3, url: ""), CancellationToken.None);
        await store.PutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The tick's request coalesces: it returns at once and the running
        // writer loops after its PUT. The coalesced iteration refreshes from
        // SQL, which fails here (no database) and is logged, and the loop ends.
        var rewrite = writer.WriteFromStateAsync("tick-rewrite", CancellationToken.None);
        Assert.True(rewrite.IsCompleted);

        store.ReleasePut.SetResult();
        // The coalesced iteration ran: its refresh hit the absent database and
        // faulted the write after the first PUT. The integration test proves
        // the second PUT with a real database; this one proves the queueing.
        await Assert.ThrowsAnyAsync<Exception>(() => write.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, store.Puts);
    }

    private static NodeSnapshot Snapshot(long version, short statusId, string url) => new(
        SnapshotVersion: version,
        SnapshotUrl: url,
        CurrentEvent: new CurrentEvent(EventId, statusId, null),
        ActiveBeaconId: 5,
        LatestPublished: new PublishedLocation(99, 46.0, -114.0, null, null, null, null,
            new DateTimeOffset(2026, 12, 24, 18, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 12, 24, 18, 0, 1, TimeSpan.Zero)),
        CookieTally: ImmutableSortedDictionary<long, int>.Empty,
        Settings: NodeSettings.Defaults,
        RefreshedAt: DateTimeOffset.UtcNow);

    private static LocationSourceFields Fields(long version, short statusId, string url) => new(
        EventId: EventId,
        EventStatusId: statusId,
        SnapshotVersion: version,
        SnapshotUrl: url,
        Seq: 100,
        Lat: 46.87,
        Lng: -114.0,
        SpeedMps: 12.5,
        AltitudeM: 1200,
        HeadingDeg: 90,
        AccuracyM: 5,
        RecordedAt: new DateTimeOffset(2026, 12, 24, 18, 0, 2, TimeSpan.Zero),
        ReceivedAt: new DateTimeOffset(2026, 12, 24, 18, 0, 3, TimeSpan.Zero));

    private static LiveObjectWriter Writer(NodeSnapshot state)
    {
        var connections = WmsfoConnectionStrings.ForTests("Host=127.0.0.1;Port=1;Database=x;Username=x;Timeout=1");
        return new LiveObjectWriter(
            new GatedStore(), new NullGateway(),
            new NodeStateService(connections, NullLogger<NodeStateService>.Instance),
            connections, new WmsfoOptions { ServiceName = "wmsfo-api-test" },
            new NodeCounters(), NullLogger<LiveObjectWriter>.Instance);
    }

    // A store whose first PUT of the live object waits on a gate, so a test can
    // observe the writer mid-flight.
    private sealed class GatedStore : IObjectStore
    {
        public TaskCompletionSource PutEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePut { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _puts;
        public int Puts => Volatile.Read(ref _puts);

        public async Task PutObjectAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl, string? tag = null, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _puts) == 1)
            {
                PutEntered.TrySetResult();
                await ReleasePut.Task.WaitAsync(cancellationToken);
            }
        }

        public Task DeleteObjectAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async IAsyncEnumerable<ObjectListEntry> ListPrefixAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
        public Task PutObjectTaggingAsync(string key, string tag, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DeleteObjectTaggingAsync(string key, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string?> GetObjectTaggingAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task<ObjectHead?> HeadObjectAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<ObjectHead?>(null);
        public Task<ObjectContent?> GetObjectAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult<ObjectContent?>(null);
        public string PresignPut(string key, string contentType, string tag) => "";
    }

    private sealed class NullGateway : IGatewayInternalClient
    {
        public string? LastInstanceId => null;
        public Task<bool> PublishAsync(string channel, string @event, ReadOnlyMemory<byte> payloadBytes, CancellationToken ct) => Task.FromResult(true);
        public Task<LeaderAnswer> GetLeaderAsync(CancellationToken ct) => Task.FromResult(new LeaderAnswer(false, null, null, false));
        public Task<IReadOnlyList<string>?> GetPresenceAsync(string channel, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<int?> GetPresenceCountAsync(string channel, CancellationToken ct) => Task.FromResult<int?>(null);
    }
}
