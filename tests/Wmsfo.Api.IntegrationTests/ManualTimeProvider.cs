namespace Wmsfo.Api.IntegrationTests;

// A TimeProvider that stands still until a test advances it. It starts at the
// wall clock so values read from it look current.
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public void Advance(TimeSpan delta)
    {
        lock (_gate) _now += delta;
    }
}
