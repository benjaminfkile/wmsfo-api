using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Config;
using Wmsfo.Api.Realtime;

namespace Wmsfo.Api.Node;

// api.md 13: polls GET /internal/leader every 2 s with a 1 s timeout. IsLeader is
// true only when the last answer was 2xx, isLeader=true, evaluatedAt is non-null
// and < 10 s old at evaluation time. WMSFO_FORCE_LEADER=true short-circuits (dev only).
public sealed class LeaderMonitor : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly NodeStateService _state;
    private readonly IGatewayInternalClient _gateway;
    private readonly WmsfoOptions _options;
    private readonly WmsfoReadinessGate _readiness;
    private readonly ILogger<LeaderMonitor> _logger;

    public LeaderMonitor(
        NodeStateService state,
        IGatewayInternalClient gateway,
        WmsfoOptions options,
        WmsfoReadinessGate readiness,
        ILogger<LeaderMonitor> logger)
    {
        _state = state;
        _gateway = gateway;
        _options = options;
        _readiness = readiness;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && !_readiness.IsReady)
        {
            try { await Task.Delay(TimeSpan.FromMilliseconds(200), stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }

        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            // Run an initial poll so the first request that reaches the node
            // does not fall back to the default "follower at MinValue" status.
            await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await PollOnceAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task PollOnceAsync(CancellationToken ct)
    {
        if (_options.ForceLeader)
        {
            var forced = new LeaderStatus(IsLeader: true, EvaluatedAt: DateTimeOffset.UtcNow, InstanceId: "forced");
            var wasLeader = _state.Leader.IsLeader;
            _state.SetLeader(forced);
            if (!wasLeader) _logger.LogInformation("leadership gained (forced); marker=wmsfo_leader_gained");
            return;
        }

        var answer = await _gateway.GetLeaderAsync(ct).ConfigureAwait(false);
        var previous = _state.Leader;
        var isLeader = answer.Reachable
            && answer.IsLeader
            && answer.EvaluatedAt is { } eval
            && DateTimeOffset.UtcNow - eval < LeaderStatus.Expiry;
        var evaluatedAt = answer.EvaluatedAt ?? DateTimeOffset.UtcNow;
        var status = new LeaderStatus(isLeader, evaluatedAt, answer.InstanceId ?? previous.InstanceId);
        _state.SetLeader(status);

        if (isLeader && !previous.IsLeader)
            _logger.LogInformation("leadership gained; marker=wmsfo_leader_gained");
        else if (!isLeader && previous.IsLeader)
            _logger.LogInformation("leadership lost; marker=wmsfo_leader_lost");
    }
}
