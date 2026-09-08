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

    // Extracted so tests can drive one tick without waiting on the timer.
    public async Task TickOnceAsync(CancellationToken ct)
    {
        try
        {
            var before = _state.Current.SnapshotVersion;
            var refreshed = await _state.RefreshAsync("tick", ct).ConfigureAwait(false);
            if (refreshed.SnapshotVersion == before) return;
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
