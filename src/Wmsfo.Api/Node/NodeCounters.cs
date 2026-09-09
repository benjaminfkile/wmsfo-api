namespace Wmsfo.Api.Node;

// api.md 16: in-memory counters kept per node and exposed on GET /admin/live
// under `node.counters`. There is no metrics endpoint; CloudWatch metric
// filters on the log markers are the alarm source. These are the process
// counterparts of those markers so an operator can spot-check whether a
// specific node is doing what it says.
public sealed class NodeCounters
{
    private long _locationsStored;
    private long _locationsPublished;
    private long _livePutsOk;
    private long _livePutsFailed;
    private long _publishesOk;
    private long _publishesFailed;
    private long _heartbeats;

    // POST /realtime/authorize branches per contracts 2.4.
    private long _authorizePublicAllow;
    private long _authorizeIngestAllow;
    private long _authorizeIngestDeny;

    // POST /realtime/message outcomes per contracts 2.5.
    private long _messageLocationOk;
    private long _messageForbidden;
    private long _messageValidationFailed;

    public void IncrementLocationStored() => Interlocked.Increment(ref _locationsStored);
    public void IncrementLocationPublished() => Interlocked.Increment(ref _locationsPublished);
    public void IncrementLivePutOk() => Interlocked.Increment(ref _livePutsOk);
    public void IncrementLivePutFailed() => Interlocked.Increment(ref _livePutsFailed);
    public void IncrementPublishOk() => Interlocked.Increment(ref _publishesOk);
    public void IncrementPublishFailed() => Interlocked.Increment(ref _publishesFailed);
    public void IncrementHeartbeat() => Interlocked.Increment(ref _heartbeats);

    public void IncrementAuthorize(AuthorizeBranch branch)
    {
        switch (branch)
        {
            case AuthorizeBranch.PublicAllow: Interlocked.Increment(ref _authorizePublicAllow); break;
            case AuthorizeBranch.IngestAllow: Interlocked.Increment(ref _authorizeIngestAllow); break;
            case AuthorizeBranch.IngestDeny: Interlocked.Increment(ref _authorizeIngestDeny); break;
        }
    }

    public void IncrementMessage(MessageOutcome outcome)
    {
        switch (outcome)
        {
            case MessageOutcome.LocationOk: Interlocked.Increment(ref _messageLocationOk); break;
            case MessageOutcome.Forbidden: Interlocked.Increment(ref _messageForbidden); break;
            case MessageOutcome.ValidationFailed: Interlocked.Increment(ref _messageValidationFailed); break;
        }
    }

    public NodeCountersSnapshot Snapshot() => new()
    {
        LocationsStored = Interlocked.Read(ref _locationsStored),
        LocationsPublished = Interlocked.Read(ref _locationsPublished),
        LivePutsOk = Interlocked.Read(ref _livePutsOk),
        LivePutsFailed = Interlocked.Read(ref _livePutsFailed),
        PublishesOk = Interlocked.Read(ref _publishesOk),
        PublishesFailed = Interlocked.Read(ref _publishesFailed),
        Heartbeats = Interlocked.Read(ref _heartbeats),
        AuthorizePublicAllow = Interlocked.Read(ref _authorizePublicAllow),
        AuthorizeIngestAllow = Interlocked.Read(ref _authorizeIngestAllow),
        AuthorizeIngestDeny = Interlocked.Read(ref _authorizeIngestDeny),
        MessageLocationOk = Interlocked.Read(ref _messageLocationOk),
        MessageForbidden = Interlocked.Read(ref _messageForbidden),
        MessageValidationFailed = Interlocked.Read(ref _messageValidationFailed),
    };
}

public enum AuthorizeBranch { PublicAllow, IngestAllow, IngestDeny }

public enum MessageOutcome { LocationOk, Forbidden, ValidationFailed }

// Immutable view. Property names round-trip to the JSON keys under
// `node.counters` (camelCase via System.Text.Json defaults).
public sealed class NodeCountersSnapshot
{
    public long LocationsStored { get; set; }
    public long LocationsPublished { get; set; }
    public long LivePutsOk { get; set; }
    public long LivePutsFailed { get; set; }
    public long PublishesOk { get; set; }
    public long PublishesFailed { get; set; }
    public long Heartbeats { get; set; }
    public long AuthorizePublicAllow { get; set; }
    public long AuthorizeIngestAllow { get; set; }
    public long AuthorizeIngestDeny { get; set; }
    public long MessageLocationOk { get; set; }
    public long MessageForbidden { get; set; }
    public long MessageValidationFailed { get; set; }
}
