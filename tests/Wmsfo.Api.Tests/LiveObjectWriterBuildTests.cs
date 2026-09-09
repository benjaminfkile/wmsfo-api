using System.Collections.Immutable;
using Microsoft.Extensions.Logging.Abstractions;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.Tests;

// A7 acceptance criterion 751: `fixtures/live-object.json` byte-equals what the
// writer produces for the fixture data. The Build method here is the pure builder
// LiveObjectWriter uses to serialize the object it writes and publishes; giving
// it the same DTO as `FixtureData.BuildLiveObject` must yield the same bytes.
public class LiveObjectWriterBuildTests
{
    [Fact]
    public void Build_produces_bytes_identical_to_fixture_for_fixture_data()
    {
        var fixture = FixtureData.BuildLiveObject();

        // Assemble a NodeSnapshot and location fields matching the fixture inputs.
        var tally = ImmutableSortedDictionary<long, int>.Empty
            .Add(1, 412)
            .Add(3, 90);
        var state = new NodeSnapshot(
            SnapshotVersion: 42,
            SnapshotUrl: fixture.SnapshotUrl,
            CurrentEvent: new CurrentEvent(fixture.EventId ?? 0, fixture.EventStatusId is int s ? (short)s : (short)0, null),
            ActiveBeaconId: 5,
            LatestPublished: null,
            CookieTally: tally,
            Settings: NodeSettings.Defaults,
            RefreshedAt: DateTimeOffset.UtcNow);

        var location = new LocationSourceFields(
            EventId: fixture.EventId ?? 0,
            EventStatusId: fixture.EventStatusId is int s2 ? (short)s2 : (short)0,
            SnapshotVersion: 42,
            SnapshotUrl: fixture.SnapshotUrl,
            Seq: fixture.Seq ?? 0,
            Lat: fixture.Lat ?? 0,
            Lng: fixture.Lng ?? 0,
            SpeedMps: fixture.SpeedMps,
            AltitudeM: fixture.AltitudeM,
            HeadingDeg: fixture.HeadingDeg,
            AccuracyM: fixture.AccuracyM,
            RecordedAt: fixture.RecordedAt ?? DateTimeOffset.MinValue,
            ReceivedAt: fixture.ReceivedAt ?? DateTimeOffset.MinValue);

        var writer = new LiveObjectWriter(
            store: new StubStore(),
            gateway: new StubGateway(),
            state: new NodeStateService(FakeConnections(), NullLogger<NodeStateService>.Instance),
            connections: FakeConnections(),
            options: new WmsfoOptions { ServiceName = "wmsfo-api-test" },
            counters: new NodeCounters(),
            logger: NullLogger<LiveObjectWriter>.Instance);

        var (built, bytes) = writer.Build(state, location, publishedAt: fixture.PublishedAt);

        // Byte-equal to what CanonicalJson would write for the fixture DTO.
        var expected = CanonicalJson.SerializeToUtf8Bytes(fixture);
        Assert.Equal(expected, bytes);

        // And byte-equal to the checked-in fixture on disk.
        var onDisk = File.ReadAllBytes(Path.Combine(ContractsPaths.FixturesDir, "live-object.json"));
        Assert.Equal(onDisk, bytes);

        // Sanity: the DTO the writer built matches the fixture DTO field for field.
        Assert.Equal(fixture.SchemaVersion, built.SchemaVersion);
        Assert.Equal(fixture.EventId, built.EventId);
        Assert.Equal(fixture.EventStatusId, built.EventStatusId);
        Assert.Equal(fixture.PollIntervalMs, built.PollIntervalMs);
        Assert.Equal(fixture.SnapshotUrl, built.SnapshotUrl);
        Assert.Equal(fixture.Seq, built.Seq);
        Assert.Equal(fixture.Lat, built.Lat);
        Assert.Equal(fixture.PublishedAt, built.PublishedAt);
    }

    private static WmsfoConnectionStrings FakeConnections()
        => WmsfoConnectionStrings.ForTests("Host=localhost;Database=x;Username=x");

    private sealed class StubStore : IObjectStore
    {
        public Task PutObjectAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl, string? tag = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
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

    private sealed class StubGateway : IGatewayInternalClient
    {
        public string? LastInstanceId => null;
        public Task<bool> PublishAsync(string channel, string @event, ReadOnlyMemory<byte> payloadBytes, CancellationToken ct) => Task.FromResult(true);
        public Task<LeaderAnswer> GetLeaderAsync(CancellationToken ct) => Task.FromResult(new LeaderAnswer(false, null, null, false));
        public Task<IReadOnlyList<string>?> GetPresenceAsync(string channel, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
    }
}
