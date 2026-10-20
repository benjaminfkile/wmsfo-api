using Microsoft.Extensions.Logging.Abstractions;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Tests;

// api.md 17: the health probe runs at most once per second per node; callers
// in between, and callers while a probe is in flight, share its result.
public class HealthProbeTests
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now += delta;
    }

    private static HealthMarkerLogger Marker() => new(NullLogger<HealthMarkerLogger>.Instance);

    [Fact]
    public async Task Callers_within_one_second_share_one_probe()
    {
        var time = new ManualTime();
        var statements = 0;
        var probe = HealthProbe.WithStatement(_ => { Interlocked.Increment(ref statements); return Task.CompletedTask; }, Marker(), time);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => probe.CheckAsync()));
        Assert.All(results, Assert.True);
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.True(await probe.CheckAsync());
        Assert.Equal(1, statements);
        Assert.Equal(1, probe.Runs);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(await probe.CheckAsync());
        Assert.Equal(2, statements);
    }

    [Fact]
    public async Task Callers_during_a_slow_probe_await_the_same_task()
    {
        var time = new ManualTime();
        var release = new TaskCompletionSource();
        var statements = 0;
        var probe = HealthProbe.WithStatement(async _ => { Interlocked.Increment(ref statements); await release.Task; }, Marker(), time);

        var first = probe.CheckAsync();
        time.Advance(TimeSpan.FromSeconds(5));
        var second = probe.CheckAsync();
        Assert.Same(first, second);
        release.SetResult();
        Assert.True(await second);
        Assert.Equal(1, statements);
    }

    [Fact]
    public async Task A_failed_probe_is_cached_for_the_same_second()
    {
        var time = new ManualTime();
        var statements = 0;
        var probe = HealthProbe.WithStatement(_ =>
        {
            Interlocked.Increment(ref statements);
            throw new InvalidOperationException("db down");
        }, Marker(), time);

        Assert.False(await probe.CheckAsync());
        Assert.False(await probe.CheckAsync());
        Assert.Equal(1, statements);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.False(await probe.CheckAsync());
        Assert.Equal(2, statements);
    }
}
