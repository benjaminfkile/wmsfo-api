namespace Wmsfo.Api.Chores;

// Injected clock so the nightly-cleanup schedule and the media orphan
// transitions can be tested without waiting for real time to pass.
// Integration tests pass a stub whose UtcNow they advance manually
// (see the A16 tests for the four orphan-transition scenarios).
public interface IChoreClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemChoreClock : IChoreClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
