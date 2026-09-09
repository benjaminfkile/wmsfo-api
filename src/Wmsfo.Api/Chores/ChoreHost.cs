using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Config;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.Chores;

// api.md 13: one BackgroundService running each chore on its own cadence
// only while Leader.IsLeader is true at the moment the chore starts. A
// chore in flight finishes even if leadership lapses; every chore is
// idempotent so an overlapping leader hand-off never double-executes.
public sealed class ChoreHost : BackgroundService
{
    public static readonly TimeSpan OutboxCadence = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan AlertCadence = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan StaleCadence = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan OrphanCadence = TimeSpan.FromHours(1);
    public static readonly TimeSpan NightlyPollCadence = TimeSpan.FromMinutes(1);

    // Nightly cleanup fires at 09:00 UTC exactly once per day. The poll
    // cadence above is how often the host wakes to check the clock; each
    // day's fire is guarded by _nightlyLastRunDate.
    public static readonly TimeSpan NightlyRunAt = TimeSpan.FromHours(9);

    private readonly NodeStateService _state;
    private readonly WmsfoReadinessGate _readiness;
    private readonly OutboxPublisher _outbox;
    private readonly AlertSender _alert;
    private readonly StaleBeaconFlagger _stale;
    private readonly MediaOrphanCollector _orphan;
    private readonly NightlyCleanup _nightly;
    private readonly IChoreClock _clock;
    private readonly ILogger<ChoreHost> _logger;

    private DateTime? _nightlyLastRunDate;

    public ChoreHost(
        NodeStateService state,
        WmsfoReadinessGate readiness,
        OutboxPublisher outbox,
        AlertSender alert,
        StaleBeaconFlagger stale,
        MediaOrphanCollector orphan,
        NightlyCleanup nightly,
        IChoreClock clock,
        ILogger<ChoreHost> logger)
    {
        _state = state;
        _readiness = readiness;
        _outbox = outbox;
        _alert = alert;
        _stale = stale;
        _orphan = orphan;
        _nightly = nightly;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for the readiness gate - chores must not run before migrations.
        while (!stoppingToken.IsCancellationRequested && !_readiness.IsReady)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        var outboxTask = RunOnCadenceAsync("outbox", OutboxCadence, _outbox.RunOnceAsync, stoppingToken);
        var alertTask = RunOnCadenceAsync("alert", AlertCadence, _alert.RunOnceAsync, stoppingToken);
        var staleTask = RunOnCadenceAsync("stale-beacon", StaleCadence, _stale.RunOnceAsync, stoppingToken);
        var orphanTask = RunOnCadenceAsync("media-orphan", OrphanCadence, _orphan.RunOnceAsync, stoppingToken);
        var nightlyTask = RunNightlyAsync(stoppingToken);

        await Task.WhenAll(outboxTask, alertTask, staleTask, orphanTask, nightlyTask).ConfigureAwait(false);
    }

    // Cadenced runner. Every chore starts only while IsLeader at the tick,
    // the run is awaited in full (so an in-flight chore rides a leadership
    // change), and exceptions are logged without stopping the loop.
    private async Task RunOnCadenceAsync(string name, TimeSpan cadence, Func<CancellationToken, Task> body, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(cadence);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_state.Leader.IsCurrentlyLeader(_clock.UtcNow)) continue;
                try
                {
                    await body(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "{Chore} chore threw", name);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // Runner for a Func<CancellationToken, Task<T>> variant.
    private Task RunOnCadenceAsync<T>(string name, TimeSpan cadence, Func<CancellationToken, Task<T>> body, CancellationToken ct)
        => RunOnCadenceAsync(name, cadence, async token => { await body(token).ConfigureAwait(false); }, ct);

    private async Task RunNightlyAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(NightlyPollCadence);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_state.Leader.IsCurrentlyLeader(_clock.UtcNow)) continue;
                var now = _clock.UtcNow.UtcDateTime;
                var today = now.Date;
                var targetToday = today + NightlyRunAt;
                if (now < targetToday) continue;
                if (_nightlyLastRunDate == today) continue;
                try
                {
                    _nightlyLastRunDate = today;
                    await _nightly.RunOnceAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "nightly chore threw");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
