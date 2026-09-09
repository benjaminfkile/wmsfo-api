using Wmsfo.Api.Node;

namespace Wmsfo.Api.Tests;

// A17: the counter set of api.md 16 is exposed under `node.counters` on
// GET /admin/live. The service is the process-wide singleton; every increment
// crosses Interlocked so concurrent requests do not lose counts.
public class NodeCountersTests
{
    [Fact]
    public void Snapshot_starts_at_zero()
    {
        var c = new NodeCounters();
        var s = c.Snapshot();
        Assert.Equal(0, s.LocationsStored);
        Assert.Equal(0, s.LocationsPublished);
        Assert.Equal(0, s.LivePutsOk);
        Assert.Equal(0, s.LivePutsFailed);
        Assert.Equal(0, s.PublishesOk);
        Assert.Equal(0, s.PublishesFailed);
        Assert.Equal(0, s.Heartbeats);
        Assert.Equal(0, s.AuthorizePublicAllow);
        Assert.Equal(0, s.AuthorizeIngestAllow);
        Assert.Equal(0, s.AuthorizeIngestDeny);
        Assert.Equal(0, s.MessageLocationOk);
        Assert.Equal(0, s.MessageForbidden);
        Assert.Equal(0, s.MessageValidationFailed);
    }

    [Fact]
    public void Every_increment_is_reflected_in_the_snapshot()
    {
        var c = new NodeCounters();
        c.IncrementLocationStored();
        c.IncrementLocationStored();
        c.IncrementLocationPublished();
        c.IncrementLivePutOk();
        c.IncrementLivePutFailed();
        c.IncrementPublishOk();
        c.IncrementPublishFailed();
        c.IncrementHeartbeat();
        c.IncrementHeartbeat();
        c.IncrementHeartbeat();
        c.IncrementAuthorize(AuthorizeBranch.PublicAllow);
        c.IncrementAuthorize(AuthorizeBranch.IngestAllow);
        c.IncrementAuthorize(AuthorizeBranch.IngestDeny);
        c.IncrementMessage(MessageOutcome.LocationOk);
        c.IncrementMessage(MessageOutcome.Forbidden);
        c.IncrementMessage(MessageOutcome.ValidationFailed);
        var s = c.Snapshot();
        Assert.Equal(2, s.LocationsStored);
        Assert.Equal(1, s.LocationsPublished);
        Assert.Equal(1, s.LivePutsOk);
        Assert.Equal(1, s.LivePutsFailed);
        Assert.Equal(1, s.PublishesOk);
        Assert.Equal(1, s.PublishesFailed);
        Assert.Equal(3, s.Heartbeats);
        Assert.Equal(1, s.AuthorizePublicAllow);
        Assert.Equal(1, s.AuthorizeIngestAllow);
        Assert.Equal(1, s.AuthorizeIngestDeny);
        Assert.Equal(1, s.MessageLocationOk);
        Assert.Equal(1, s.MessageForbidden);
        Assert.Equal(1, s.MessageValidationFailed);
    }

    [Fact]
    public async Task Concurrent_increments_do_not_lose_counts()
    {
        var c = new NodeCounters();
        var tasks = new List<Task>();
        for (var i = 0; i < 8; i++)
        {
            tasks.Add(Task.Run(() =>
            {
                for (var j = 0; j < 1000; j++)
                {
                    c.IncrementHeartbeat();
                    c.IncrementLocationStored();
                }
            }));
        }
        await Task.WhenAll(tasks);
        var s = c.Snapshot();
        Assert.Equal(8_000, s.Heartbeats);
        Assert.Equal(8_000, s.LocationsStored);
    }
}
