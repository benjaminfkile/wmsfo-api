using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.Tests;

// A133: the leader logs one `wmsfo_live_tick` line per 60 s while the current
// event is live (api.md 16). Driven through ReconcileTick.LogLiveTick with a
// fake clock, a leader status set on the node state, and a capturing logger.
public class LiveTickTests
{
    private const long EventId = 7;

    [Fact]
    public void Logs_once_a_minute_on_the_leader_while_live()
    {
        var h = new Harness();
        h.SetLeader(true);

        h.Tick(Live);
        Assert.Single(h.Lines);
        Assert.Equal(LogMarkers.LiveTick, h.Lines[0]["marker"]);
        Assert.Equal(EventId.ToString(), h.Lines[0]["eventId"]);

        h.Advance(30);
        h.Tick(Live);
        Assert.Single(h.Lines);

        h.Advance(30);
        h.Tick(Live);
        Assert.Equal(2, h.Lines.Count);
    }

    [Theory]
    [InlineData((short)2)]
    [InlineData((short)4)]
    public void Logs_nothing_when_the_event_is_not_live(short statusId)
    {
        var h = new Harness();
        h.SetLeader(true);
        h.Tick(WithStatus(statusId));
        h.Tick(NodeSnapshot.Empty);
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void Logs_nothing_when_not_the_leader()
    {
        var h = new Harness();
        h.SetLeader(false);
        h.Tick(Live);
        h.Advance(61);
        h.Tick(Live);
        Assert.Empty(h.Lines);
    }

    [Fact]
    public void The_first_tick_after_a_return_to_live_logs_at_once()
    {
        var h = new Harness();
        h.SetLeader(true);
        h.Tick(Live);
        h.Advance(10);
        h.Tick(WithStatus(2));
        h.Advance(10);
        h.Tick(Live);
        Assert.Equal(2, h.Lines.Count);
    }

    [Fact]
    public void The_first_tick_after_leadership_returns_logs_at_once()
    {
        var h = new Harness();
        h.SetLeader(true);
        h.Tick(Live);
        h.Advance(10);
        h.SetLeader(false);
        h.Tick(Live);
        h.Advance(10);
        h.SetLeader(true);
        h.Tick(Live);
        Assert.Equal(2, h.Lines.Count);
    }

    private static NodeSnapshot Live => WithStatus(3);

    private static NodeSnapshot WithStatus(short statusId) =>
        NodeSnapshot.Empty with { CurrentEvent = new CurrentEvent(EventId, statusId, null) };

    private sealed class Harness
    {
        private readonly FakeClock _clock = new(new DateTimeOffset(2027, 12, 24, 18, 0, 0, TimeSpan.Zero));
        private readonly NodeStateService _state;
        private readonly ReconcileTick _tick;
        private readonly CapturingLogger _logger = new();

        public Harness()
        {
            var connections = WmsfoConnectionStrings.ForTests("Host=localhost;Database=x;Username=x");
            var options = new WmsfoOptions { ServiceName = "wmsfo-api-test" };
            _state = new NodeStateService(connections, NullLogger<NodeStateService>.Instance);
            var writer = new LiveObjectWriter(
                new NullStore(), new NullGateway(), _state, connections, options,
                new NodeCounters(), NullLogger<LiveObjectWriter>.Instance);
            _tick = new ReconcileTick(_state, writer, options, new WmsfoReadinessGate(), _logger, _clock);
        }

        public List<IReadOnlyDictionary<string, string?>> Lines =>
            _logger.Entries.Where(e => e.TryGetValue("marker", out var m) && m == LogMarkers.LiveTick).ToList();

        public void SetLeader(bool isLeader) =>
            _state.SetLeader(new LeaderStatus(isLeader, _clock.GetUtcNow(), "node-test"));

        public void Advance(int seconds)
        {
            _clock.Now = _clock.Now.AddSeconds(seconds);
            // The leader answer stays fresh, as the 2 s poll keeps it.
            _state.SetLeader(_state.Leader with { EvaluatedAt = _clock.Now });
        }

        public void Tick(NodeSnapshot refreshed) => _tick.LogLiveTick(refreshed);
    }

    private sealed class FakeClock : TimeProvider
    {
        public FakeClock(DateTimeOffset now) { Now = now; }
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class CapturingLogger : ILogger<ReconcileTick>
    {
        public ConcurrentQueue<IReadOnlyDictionary<string, string?>> Queue { get; } = new();
        public List<IReadOnlyDictionary<string, string?>> Entries => Queue.ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var props = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kv in kvps)
                {
                    if (kv.Key == "{OriginalFormat}") continue;
                    // The JSON console writes holes as camel case (api.md 16).
                    props[char.ToLowerInvariant(kv.Key[0]) + kv.Key[1..]] = kv.Value?.ToString();
                }
            }
            if (logLevel == LogLevel.Information) Queue.Enqueue(props);
        }
    }

    private sealed class NullStore : IObjectStore
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

    private sealed class NullGateway : IGatewayInternalClient
    {
        public string? LastInstanceId => null;
        public Task<bool> PublishAsync(string channel, string @event, ReadOnlyMemory<byte> payloadBytes, CancellationToken ct) => Task.FromResult(true);
        public Task<LeaderAnswer> GetLeaderAsync(CancellationToken ct) => Task.FromResult(new LeaderAnswer(false, null, null, false));
        public Task<IReadOnlyList<string>?> GetPresenceAsync(string channel, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
        public Task<int?> GetPresenceCountAsync(string channel, CancellationToken ct) => Task.FromResult<int?>(null);
    }
}
