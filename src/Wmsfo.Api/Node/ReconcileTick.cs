using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Node;

// api.md 9: BackgroundService that ticks every WMSFO_RECONCILE_TICK_MS.
// Each tick:
//   - captures state.SnapshotVersion (before)
//   - Refresh("tick")
//   - if the version moved and this node has not written that version yet:
//       - if wroteForLocationSinceVersionChange: liveObjectWriter.WriteFromState("tick-rewrite")
//         (once; clears the flag)
//       - else: refresh only, no write, no publish
//   - otherwise, on the leader only, when the refreshed tally differs from the
//     tally in the object this node last wrote while the event is live:
//     liveObjectWriter.WriteFromState("tally"), so cookies reach the CDN within
//     a tick even when no beacon is sending locations.
public sealed class ReconcileTick : BackgroundService
{
    private readonly NodeStateService _state;
    private readonly LiveObjectWriter _writer;
    private readonly WmsfoOptions _options;
    private readonly WmsfoReadinessGate _readiness;
    private readonly ILogger<ReconcileTick> _logger;

    public ReconcileTick(
        NodeStateService state,
        LiveObjectWriter writer,
        WmsfoOptions options,
        WmsfoReadinessGate readiness,
        ILogger<ReconcileTick> logger)
    {
        _state = state;
        _writer = writer;
        _options = options;
        _readiness = readiness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the readiness gate so the tick does not run before migrations.
        while (!stoppingToken.IsCancellationRequested && !_readiness.IsReady)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.ReconcileTickMs));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // api.md 9: the leader rewrites the live object when the tally it last
    // wrote no longer matches the refreshed one, while the event is live.
    private bool TallyMoved(NodeSnapshot refreshed)
    {
        if (refreshed.CurrentEvent is not { StatusId: 3 }) return false;
        if (!_state.Leader.IsCurrentlyLeader(DateTimeOffset.UtcNow)) return false;
        var last = _writer.LastWrittenObject?.CookieTally;
        var now = refreshed.CookieTally;
        if (last is null) return now.Count > 0;
        if (last.Count != now.Count) return true;
        foreach (var kv in now)
        {
            if (!last.TryGetValue(kv.Key, out var v) || v != kv.Value) return true;
        }
        return false;
    }

    // Extracted so tests can drive one tick without waiting on the timer.
    public async Task TickOnceAsync(CancellationToken ct)
    {
        try
        {
            var before = _state.Current.SnapshotVersion;
            var refreshed = await _state.RefreshAsync("tick", ct).ConfigureAwait(false);
            if (refreshed.SnapshotVersion == before)
            {
                if (TallyMoved(refreshed))
                {
                    await _writer.WriteFromStateAsync("tally", ct).ConfigureAwait(false);
                }
                return;
            }
            if (refreshed.SnapshotVersion == _state.LastWrittenVersion) return;

            if (_state.WroteForLocationSinceVersionChange)
            {
                _state.ClearWroteForLocation();
                _state.RecordWroteVersion(refreshed.SnapshotVersion);
                await _writer.WriteFromStateAsync("tick-rewrite", ct).ConfigureAwait(false);
            }
            else
            {
                // A node that never wrote for this event's location refreshes memory only.
                _state.RecordWroteVersion(refreshed.SnapshotVersion);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "reconcile tick failed; retrying next tick");
        }
    }
}
