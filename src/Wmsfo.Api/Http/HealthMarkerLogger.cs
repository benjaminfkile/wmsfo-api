using Microsoft.Extensions.Logging;

namespace Wmsfo.Api.Http;

// api.md 17: /api/health logs the `wmsfo_health_unavailable` marker at Warning
// at most once per 30 s when the database probe fails. The throttling window
// keeps a bursty failure from spamming CloudWatch while still surfacing on the
// metric filter (platform.md 10).
public sealed class HealthMarkerLogger
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger<HealthMarkerLogger> _logger;
    private long _lastEmittedTicks;

    public HealthMarkerLogger(ILogger<HealthMarkerLogger> logger)
    {
        _logger = logger;
        _lastEmittedTicks = 0;
    }

    public void LogUnavailable(Exception? exception, DateTimeOffset? now = null)
    {
        var currentTicks = (now ?? DateTimeOffset.UtcNow).UtcTicks;
        while (true)
        {
            var last = Interlocked.Read(ref _lastEmittedTicks);
            if (last != 0 && new TimeSpan(currentTicks - last) < MinInterval) return;
            if (Interlocked.CompareExchange(ref _lastEmittedTicks, currentTicks, last) == last) break;
        }
        if (exception is null)
        {
            _logger.LogWarning("health check failed; marker={Marker}", LogMarkers.HealthUnavailable);
        }
        else
        {
            _logger.LogWarning(exception, "health check failed; marker={Marker}", LogMarkers.HealthUnavailable);
        }
    }
}
