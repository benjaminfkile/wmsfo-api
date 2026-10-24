using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Auth;
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
    public static readonly TimeSpan ContactInboxCadence = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan NightlyPollCadence = TimeSpan.FromMinutes(1);

    // Nightly cleanup fires at 09:00 UTC exactly once per day, the pending
    // map sweep right after it, then the map size measure. The poll cadence above is how often the host
    // wakes to check the clock; each day's fire is guarded by
    // _nightlyLastRunDate.
    public static readonly TimeSpan NightlyRunAt = TimeSpan.FromHours(9);

    private readonly NodeStateService _state;
    private readonly WmsfoReadinessGate _readiness;
    private readonly OutboxPublisher _outbox;
    private readonly AlertSender _alert;
    private readonly StaleBeaconFlagger _stale;
    private readonly MediaOrphanCollector _orphan;
    private readonly NightlyCleanup _nightly;
    private readonly PendingMapSweeper _mapSweep;
    private readonly MapSizeMeasurer _mapSizes;
    private readonly ContactInboxImporter _contactInbox;
    private readonly SignUpsSwitch _signUps;
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
        PendingMapSweeper mapSweep,
        MapSizeMeasurer mapSizes,
        ContactInboxImporter contactInbox,
        SignUpsSwitch signUps,
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
        _mapSweep = mapSweep;
        _mapSizes = mapSizes;
        _contactInbox = contactInbox;
        _signUps = signUps;
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
        var contactInboxTask = RunOnCadenceAsync("contact-inbox", ContactInboxCadence, _contactInbox.RunOnceAsync, stoppingToken);
        var nightlyTask = RunNightlyAsync(stoppingToken);
        var leaderStartTask = RunLeaderStartOnceAsync(stoppingToken);

        await Task.WhenAll(outboxTask, alertTask, staleTask, orphanTask, contactInboxTask, nightlyTask, leaderStartTask).ConfigureAwait(false);
    }

    // The leader start work runs once, on the first tick (at the outbox
    // cadence) where this node is the leader, and then the loop ends.
    private async Task RunLeaderStartOnceAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(OutboxCadence);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (!_state.Leader.IsCurrentlyLeader(_clock.UtcNow)) continue;
                await RunLeaderStartAsync(ct).ConfigureAwait(false);
                return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The leader start work in order: the sign-ups drift check, then the map
    // size measure. A throw from the measure is logged.
    public async Task RunLeaderStartAsync(CancellationToken ct)
    {
        await _signUps.CheckDriftAsync(ct).ConfigureAwait(false);
        await RunNightlyChoreAsync("map-size-measure", _mapSizes.RunOnceAsync, ct).ConfigureAwait(false);
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
                _nightlyLastRunDate = today;
                if (!await RunNightlySequenceAsync(ct).ConfigureAwait(false)) return;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // The nightly run in order: the cleanup, the pending map sweep, the map
    // size measure. False when the host is stopping.
    public async Task<bool> RunNightlySequenceAsync(CancellationToken ct)
    {
        if (!await RunNightlyChoreAsync("nightly", _nightly.RunOnceAsync, ct).ConfigureAwait(false)) return false;
        if (!await RunNightlyChoreAsync("pending-map-sweep", _mapSweep.RunOnceAsync, ct).ConfigureAwait(false)) return false;
        return await RunNightlyChoreAsync("map-size-measure", _mapSizes.RunOnceAsync, ct).ConfigureAwait(false);
    }

    // One nightly chore; a throw is logged and the next chore still runs.
    // False when the host is stopping.
    private async Task<bool> RunNightlyChoreAsync<T>(string name, Func<CancellationToken, Task<T>> body, CancellationToken ct)
    {
        try
        {
            await body(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Chore} chore threw", name);
        }
        return true;
    }
}
