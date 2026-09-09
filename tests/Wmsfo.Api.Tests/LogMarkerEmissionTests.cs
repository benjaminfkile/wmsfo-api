using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.Tests;

// A17 acceptance criterion 819: a test asserts each marker string is emitted by
// its path.
//
// Two flavours here:
//   • Direct emission tests exercise the production class that emits the marker
//     end-to-end — HealthMarkerLogger, LeaderMonitor (via forced leader),
//     GatewayInternalClient (against a bad URL).
//   • Source-code tests locate the `LogMarkers.<Name>` reference next to a
//     `Log*` call in the API source, so the marker constant is provably wired
//     to a real log line even where the path needs Postgres, S3, or the full
//     publish flow to reach in a unit test.
//
// A catch-all `AllMarkers_are_covered_by_at_least_one_test` case iterates
// LogMarkers.All so adding a new marker constant without a covering test in
// this file fails the suite by name.
public class LogMarkerEmissionTests
{
    private static readonly HashSet<string> CoveredDirect = new(StringComparer.Ordinal)
    {
        LogMarkers.HealthUnavailable,
        LogMarkers.LeaderGained,
        LogMarkers.PublishFailed,
    };

    // Markers whose emission is proven by locating the LogMarkers.<Name>
    // reference next to a Log* call in the API source. A missing or renamed
    // call-site fails the theory below with the exact expected path.
    private static readonly Dictionary<string, string> CoveredBySource = new(StringComparer.Ordinal)
    {
        [LogMarkers.LeaderLost] = "Node/LeaderMonitor.cs",
        [LogMarkers.LivePutFailed] = "Node/LiveObjectWriter.cs",
        [LogMarkers.SnapshotWriteFailed] = "Node/SnapshotBuilder.cs",
        [LogMarkers.OutboxExhausted] = "Chores/OutboxPublisher.cs",
        [LogMarkers.AlertExhausted] = "Chores/AlertSender.cs",
        [LogMarkers.MediaWriteFailed] = "Endpoints/AdminMediaEndpoints.cs",
        [LogMarkers.ContentPublished] = "Content/Publisher.cs",
        [LogMarkers.IconLibraryWritten] = "Node/FleetFirstBootHook.cs",
    };

    [Fact]
    public void AllMarkers_are_covered_by_at_least_one_test()
    {
        var covered = new HashSet<string>(CoveredDirect, StringComparer.Ordinal);
        covered.UnionWith(CoveredBySource.Keys);
        var expected = new HashSet<string>(LogMarkers.All, StringComparer.Ordinal);
        Assert.Equal(expected, covered);
    }

    [Fact]
    public void HealthUnavailable_is_emitted_when_health_check_fails()
    {
        var (logger, sink) = MakeLogger<HealthMarkerLogger>();
        var marker = new HealthMarkerLogger(logger);
        marker.LogUnavailable(exception: new InvalidOperationException("db down"));
        AssertMarker(sink, LogMarkers.HealthUnavailable);
    }

    [Fact]
    public void HealthUnavailable_is_throttled_within_30s()
    {
        var (logger, sink) = MakeLogger<HealthMarkerLogger>();
        var marker = new HealthMarkerLogger(logger);
        var t0 = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
        marker.LogUnavailable(null, t0);
        marker.LogUnavailable(null, t0.AddSeconds(5));
        marker.LogUnavailable(null, t0.AddSeconds(29));
        Assert.Single(EntriesFor(sink, LogMarkers.HealthUnavailable));
        marker.LogUnavailable(null, t0.AddSeconds(31));
        Assert.Equal(2, EntriesFor(sink, LogMarkers.HealthUnavailable).Count);
    }

    [Fact]
    public async Task LeaderGained_is_emitted_when_LeaderMonitor_flips_to_leader()
    {
        var (logger, sink) = MakeLogger<LeaderMonitor>();
        var state = new NodeStateService(WmsfoConnectionStrings.ForTests(FakeConn), NullLogger<NodeStateService>.Instance);
        var readiness = new WmsfoReadinessGate();
        readiness.MarkReady();
        var monitor = new LeaderMonitor(
            state,
            new NullGatewayClient(),
            new WmsfoOptions { ServiceName = "wmsfo-api-test", ForceLeader = true },
            readiness,
            logger);
        await monitor.PollOnceAsync(CancellationToken.None);
        AssertMarker(sink, LogMarkers.LeaderGained);
    }

    [Fact]
    public async Task PublishFailed_is_emitted_when_the_gateway_publish_throws()
    {
        var (logger, sink) = MakeLogger<GatewayInternalClient>();
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var options = new WmsfoOptions
        {
            ServiceName = "wmsfo-api-test",
            GatewayInternalUrl = "http://127.0.0.1:1",  // nothing listens; connection refused
            GatewayRealtimeToken = "test-token",
        };
        var client = new GatewayInternalClient(httpClient, options, logger);
        _ = await client.PublishAsync("wmsfo-api-test:location", "location", new byte[] { 0x7b, 0x7d }, CancellationToken.None);
        AssertMarker(sink, LogMarkers.PublishFailed);
    }

    [Fact]
    public void LocationStored_line_is_wired_in_LocationIngest()
    {
        // api.md 16: the location handler logs `location stored` at Information
        // with seq, beaconId, eventId, and published. Platform's
        // `LocationPublished` metric filter keys on that literal message prefix.
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Wmsfo.Api", "Endpoints", "LocationIngest.cs");
        var source = File.ReadAllText(path);
        var pattern = new Regex(
            @"LogInformation\s*\(\s*""location stored[^""]*\{Seq\}[^""]*\{BeaconId\}[^""]*\{EventId\}[^""]*\{Published\}",
            RegexOptions.Singleline);
        Assert.True(pattern.IsMatch(source),
            "LocationIngest.cs must contain a LogInformation call whose template starts with `location stored ` and includes {Seq}, {BeaconId}, {EventId}, {Published}");
    }

    // ---- Source-code emission proofs ----

    [Theory]
    [MemberData(nameof(SourceMarkers))]
    public void Marker_is_wired_to_a_log_call_in_the_source(string marker, string relativePath)
    {
        var root = FindRepoRoot();
        var path = Path.Combine(root, "src", "Wmsfo.Api", relativePath);
        Assert.True(File.Exists(path), $"expected source file exists: {path}");
        var source = File.ReadAllText(path);

        var shortName = marker[6..]; // strip "wmsfo_" prefix
        var constantName = ToPascal(shortName);
        // Match a `Log{Level}(` opening followed later by the marker constant.
        // `.*?` in Singleline mode crosses newlines so the multiline log-call
        // formatting we use throughout the code base is still recognized.
        var pattern = new Regex(
            @"Log(Information|Warning|Error|Critical|Debug|Trace)\s*\(.*?LogMarkers\." + Regex.Escape(constantName) + @"\b",
            RegexOptions.Singleline);
        var match = pattern.Match(source);
        Assert.True(match.Success,
            $"{relativePath} must contain a `Log*` call that references LogMarkers.{constantName} (for marker {marker})");
    }

    public static IEnumerable<object[]> SourceMarkers()
    {
        foreach (var kv in CoveredBySource) yield return new object[] { kv.Key, kv.Value };
    }

    // ---- helpers ----

    // A syntactically valid conn string that never opens — NodeStateService only
    // uses it lazily and LeaderMonitor with ForceLeader=true never consults it.
    private const string FakeConn = "Host=localhost;Database=x;Username=x;SSL Mode=Disable";

    private static (CapturedLogger<T> logger, LogSink sink) MakeLogger<T>()
    {
        var sink = new LogSink();
        return (new CapturedLogger<T>(sink), sink);
    }

    private static void AssertMarker(LogSink sink, string marker)
    {
        var entries = EntriesFor(sink, marker);
        Assert.NotEmpty(entries);
        foreach (var e in entries)
        {
            var value = e.PropertyValueByAnyCase("Marker") ?? e.PropertyValueByAnyCase("marker");
            Assert.Equal(marker, value);
        }
    }

    private static IReadOnlyList<CapturedLogEntry> EntriesFor(LogSink sink, string marker) =>
        sink.Entries.Where(e =>
        {
            var value = e.PropertyValueByAnyCase("Marker") ?? e.PropertyValueByAnyCase("marker");
            return string.Equals(value as string, marker, StringComparison.Ordinal);
        }).ToList();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Wmsfo.slnx"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("repo root not found from " + AppContext.BaseDirectory);
    }

    private static string ToPascal(string snake)
    {
        var parts = snake.Split('_', StringSplitOptions.RemoveEmptyEntries);
        var sb = new System.Text.StringBuilder(snake.Length);
        foreach (var p in parts)
        {
            if (p.Length == 0) continue;
            sb.Append(char.ToUpperInvariant(p[0]));
            if (p.Length > 1) sb.Append(p[1..]);
        }
        return sb.ToString();
    }

    private sealed class NullGatewayClient : IGatewayInternalClient
    {
        public string? LastInstanceId => null;
        public Task<bool> PublishAsync(string channel, string @event, ReadOnlyMemory<byte> payloadBytes, CancellationToken ct) => Task.FromResult(false);
        public Task<LeaderAnswer> GetLeaderAsync(CancellationToken ct) => Task.FromResult(new LeaderAnswer(false, null, null, false));
        public Task<IReadOnlyList<string>?> GetPresenceAsync(string channel, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>(null);
    }

    private sealed class LogSink
    {
        public ConcurrentBag<CapturedLogEntry> Entries { get; } = new();
    }

    private sealed record CapturedLogEntry(LogLevel Level, string Category, string Message, IReadOnlyDictionary<string, object?> Properties)
    {
        public object? PropertyValueByAnyCase(string name)
        {
            if (Properties.TryGetValue(name, out var v)) return v;
            foreach (var pair in Properties)
            {
                if (string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)) return pair.Value;
            }
            return null;
        }
    }

    private sealed class CapturedLogger<T> : ILogger<T>
    {
        private readonly LogSink _sink;
        public CapturedLogger(LogSink sink) { _sink = sink; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var msg = formatter(state, exception);
            var props = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (state is IEnumerable<KeyValuePair<string, object?>> kvps)
            {
                foreach (var kv in kvps)
                {
                    if (kv.Key == "{OriginalFormat}") continue;
                    props[kv.Key] = kv.Value?.ToString();
                }
            }
            _sink.Entries.Add(new CapturedLogEntry(logLevel, typeof(T).FullName ?? typeof(T).Name, msg, props));
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
