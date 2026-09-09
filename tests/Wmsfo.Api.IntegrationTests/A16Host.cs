using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wmsfo.Api.Chores;
using Wmsfo.Api.Config;
using Wmsfo.Api.Email;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// A16 test host — lightweight (no HTTP endpoints). Registers the chores,
// the fake SES sender, a settable IChoreClock, and the connection strings
// pointing at the Postgres fixture. Tests drive each chore directly via
// its RunOnceAsync so cadences do not require real time to pass.
public sealed class A16Host : IDisposable
{
    public WmsfoOptions Options { get; }
    public WmsfoConnectionStrings Connections { get; }
    public FakeChoreClock Clock { get; }
    public FakeSesSender Sender { get; }
    public NodeStateService State { get; }
    public OutboxPublisher Outbox { get; }
    public AlertSender Alerts { get; }
    public StaleBeaconFlagger Stale { get; }
    public MediaOrphanCollector OrphanCollector { get; }
    public NightlyCleanup Nightly { get; }
    public IObjectStore Store { get; }

    private A16Host(WmsfoOptions options, WmsfoConnectionStrings connections, FakeChoreClock clock,
        FakeSesSender sender, NodeStateService state, OutboxPublisher outbox, AlertSender alerts,
        StaleBeaconFlagger stale, MediaOrphanCollector orphan, NightlyCleanup nightly, IObjectStore store)
    {
        Options = options;
        Connections = connections;
        Clock = clock;
        Sender = sender;
        State = state;
        Outbox = outbox;
        Alerts = alerts;
        Stale = stale;
        OrphanCollector = orphan;
        Nightly = nightly;
        Store = store;
    }

    public static A16Host Create(string connectionString, IObjectStore? store = null)
    {
        var options = new WmsfoOptions
        {
            Env = "dev",
            ServiceName = "wmsfo-api-test",
            DbConnection = connectionString,
            DbMigrationConnection = connectionString,
            AwsRegion = "us-east-2",
            S3Bucket = "wmsfo-test",
            CdnBaseUrl = "https://cdn.example",
            PublicApiBaseUrl = "https://api.example.com",
            SiteBaseUrl = "https://site.example.com",
            HubUrl = "wss://gateway.example.com/hub",
            GatewayInternalUrl = "http://127.0.0.1:1",
            CorsOrigins = "https://site.example.com",
            TrustedProxyHops = 2,
            CognitoIssuer = "https://cognito-idp.us-east-2.amazonaws.com/us-east-2_pool",
            CognitoClientIds = "site-client-id",
            CognitoUserPoolId = "us-east-2_pool",
            AdminGroup = "admin",
            EditorGroup = "editor",
            SesFromAddress = "Alerts <alerts@example.com>",
            ContactNotifyEmail = "inbox@example.com",
            AlertSendPerSec = 10,
            EnrollmentEncryptionKey = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray()),
            ReconcileTickMs = 1000,
            LogLevel = "Warning",
            DevStaticTokens = true,
            SesDryRun = true,
        };

        var connections = WmsfoConnectionStrings.ForTests(connectionString);
        var clock = new FakeChoreClock(new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var sender = new FakeSesSender();
        var templates = EmailTemplates.Load(TestPaths.EmailTemplatesDir);
        var effectiveStore = store ?? new InMemoryObjectStore();
        var loggerFactory = NullLoggerFactory.Instance;

        var state = new NodeStateService(connections, loggerFactory.CreateLogger<NodeStateService>());
        var outbox = new OutboxPublisher(connections, options, sender, loggerFactory.CreateLogger<OutboxPublisher>());
        var alerts = new AlertSender(connections, options, sender, loggerFactory.CreateLogger<AlertSender>());
        var stale = new StaleBeaconFlagger(connections, state, loggerFactory.CreateLogger<StaleBeaconFlagger>());
        var orphan = new MediaOrphanCollector(connections, effectiveStore, loggerFactory.CreateLogger<MediaOrphanCollector>());
        var nightly = new NightlyCleanup(connections, loggerFactory.CreateLogger<NightlyCleanup>());

        _ = templates; // keeps the compiler happy — the sender fake never renders
        return new A16Host(options, connections, clock, sender, state, outbox, alerts, stale, orphan, nightly, effectiveStore);
    }

    public void Dispose() { }
}

// Settable clock for the ChoreHost and nightly-cleanup scheduling.
public sealed class FakeChoreClock : IChoreClock
{
    public DateTimeOffset UtcNow { get; set; }
    public FakeChoreClock(DateTimeOffset start) { UtcNow = start; }
    public void Advance(TimeSpan delta) => UtcNow = UtcNow + delta;
}

// Records every send call and returns a synthetic id so alert_delivery rows
// look "sent" after a run. Tests can inject failures by clearing FailAll.
public sealed class FakeSesSender : ISesSender
{
    private int _sequence;
    public List<SesMessage> Sent { get; } = new();
    public bool FailAll { get; set; }
    public Exception? FailWith { get; set; }

    public Task<string> SendAsync(SesMessage message, CancellationToken ct)
    {
        if (FailAll)
        {
            throw FailWith ?? new InvalidOperationException("fake send failure");
        }
        Sent.Add(message);
        return Task.FromResult("mid-" + Interlocked.Increment(ref _sequence));
    }
}
