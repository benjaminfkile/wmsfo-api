using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Http;

// api.md 17: the shared per-node database probe behind /api/health. One
// `select 1` on the `App` pool with a 2 s timeout, run by the first caller
// after the previous result aged past 1 s (measured from the probe's start);
// every caller in between, and every caller while a probe is in flight,
// awaits the same task. A flood of health hits therefore costs at most one
// statement per second per node. A failed probe logs the
// `wmsfo_health_unavailable` marker (throttled by HealthMarkerLogger) and
// its result is cached for the same second as a success.
public sealed class HealthProbe
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan StatementTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<CancellationToken, Task> _statement;
    private readonly HealthMarkerLogger _marker;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private Task<bool>? _current;
    private DateTimeOffset _startedAt;
    private long _runs;

    public HealthProbe(WmsfoConnectionStrings connections, HealthMarkerLogger marker, TimeProvider time)
        : this(ct => SelectOneAsync(connections.App, ct), marker, time)
    {
    }

    // Tests pass their own statement to count runs or force a failure.
    public static HealthProbe WithStatement(
        Func<CancellationToken, Task> statement, HealthMarkerLogger marker, TimeProvider time) =>
        new(statement, marker, time);

    private HealthProbe(Func<CancellationToken, Task> statement, HealthMarkerLogger marker, TimeProvider time)
    {
        _statement = statement;
        _marker = marker;
        _time = time;
    }

    // The number of probes started since the node booted.
    public long Runs => Interlocked.Read(ref _runs);

    // True when the database answered the most recent probe; at most 1 s old.
    public Task<bool> CheckAsync()
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (_current is not null && (!_current.IsCompleted || now - _startedAt < MaxAge))
            {
                return _current;
            }
            _startedAt = now;
            Interlocked.Increment(ref _runs);
            _current = Task.Run(RunAsync);
            return _current;
        }
    }

    private async Task<bool> RunAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(StatementTimeout);
            await _statement(cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _marker.LogUnavailable(ex);
            return false;
        }
    }

    private static async Task SelectOneAsync(string connectionString, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new NpgsqlCommand("select 1", conn) { CommandTimeout = 2 };
        _ = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}

// api.md 17: /api/health answers 503 until ready, then the shared probe's
// result: 200 `{"status":"ok"}` or 503. Exempt from the rate limiter;
// `Cache-Control: no-store` comes from the pipeline.
public static class HealthEndpoint
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/health", async (WmsfoReadinessGate gate, HealthProbe probe, HealthMarkerLogger marker, CancellationToken ct) =>
        {
            if (!gate.IsReady)
            {
                marker.LogUnavailable(null);
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
            }
            return await probe.CheckAsync().WaitAsync(ct)
                ? Results.Ok(new { status = "ok" })
                : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        })
        .DisableRateLimiting();
    }
}
