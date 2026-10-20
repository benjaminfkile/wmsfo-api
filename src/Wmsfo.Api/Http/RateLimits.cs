using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Wmsfo.Api.Auth;

namespace Wmsfo.Api.Http;

// api.md 5 step 8 + contracts 4.0 rate table. Token buckets per node, keyed by
// beacon id, person id or API key id, the sha256 of a token the request
// carries, or one constant key per policy; nothing is keyed by the caller's
// address. Over budget: 429 with the error shape carrying retryAfterSeconds and
// the Retry-After header.
//
// Policy names live here so both the pipeline registration and the endpoint
// tagging refer to the same constants; a typo becomes a runtime warning from
// ASP.NET Core rather than silent unlimited traffic.
public static class RateLimitPolicies
{
    public const string LocationsPerBeacon = "wmsfo:locations";                  // 10/s burst 20 per beacon id
    public const string HeartbeatPerBeacon = "wmsfo:heartbeat";                  // 1/s burst 5 per beacon id
    public const string BeaconLogsPerBeacon = "wmsfo:beacon-logs";               // 1/min burst 2 per beacon id
    public const string BeaconMePerBeacon = "wmsfo:beacon-me";                   // 1/s burst 5 per beacon id
    public const string BeaconStateReadPerBeacon = "wmsfo:beacon-state-read";    // 10/s burst 20 per beacon id
    public const string BeaconStateWritePerBeacon = "wmsfo:beacon-state-write";  // 10/s burst 20 per beacon id
    public const string EnrollPerNode = "wmsfo:beacon-enroll";                   // 10/min burst 10, one bucket per node
    public const string ContactPerNode = "wmsfo:contact";                        // 20/s burst 20, one bucket per node
    public const string QrScansPerNode = "wmsfo:qr-scans";                       // 200/s burst 400, one bucket per node
    public const string SubscriptionsPerToken = "wmsfo:subscriptions";           // 2/s burst 5 per token hash
    public const string PreviewPerToken = "wmsfo:preview";                       // 240/min burst 60 per preview token hash
    public const string MeReadsPerPerson = "wmsfo:me-reads";                     // 2/s burst 5 per person id
    public const string CookiesPerPerson = "wmsfo:cookies";                      // 1/s burst 3 per person id
    public const string MeSubscribePerPerson = "wmsfo:me-subscribe";             // 5/hour burst 5 per person id
    public const string MediaUploadUrlPerPerson = "wmsfo:media-upload-url";      // 30/min burst 30 per person id
    public const string AdminPerPerson = "wmsfo:admin";                          // 20/s burst 40 per person id or API key id

    // Every policy name, so a host that swaps in its own limiter registers them all.
    public static readonly IReadOnlyList<string> All =
    [
        LocationsPerBeacon,
        HeartbeatPerBeacon,
        BeaconLogsPerBeacon,
        BeaconMePerBeacon,
        BeaconStateReadPerBeacon,
        BeaconStateWritePerBeacon,
        EnrollPerNode,
        ContactPerNode,
        QrScansPerNode,
        SubscriptionsPerToken,
        PreviewPerToken,
        MeReadsPerPerson,
        CookiesPerPerson,
        MeSubscribePerPerson,
        MediaUploadUrlPerPerson,
        AdminPerPerson,
    ];

    // The rate limits are attached to the endpoints the RateLimiter middleware
    // sees; endpoints without a policy pass the endpoint stage unlimited. The
    // per-node caps (RateLimitKeys.NodeGroup) are the global limiter and stack
    // with the endpoint policies. api.md 5: the callbacks and /api/health carry
    // DisableRateLimiting, which skips both.
}

// One row of the contracts 4.0 table: tokensPerPeriod every period, at most burst.
public sealed record TokenBucketRate(int TokensPerPeriod, TimeSpan Period, int Burst);

// The contracts 4.0 table. The defaults are the documented values; a test host
// may pass smaller numbers to trip a bucket without sending hundreds of requests.
public sealed record RateLimitTable
{
    public TokenBucketRate Locations { get; init; } = new(10, TimeSpan.FromSeconds(1), 20);
    public TokenBucketRate Heartbeat { get; init; } = new(1, TimeSpan.FromSeconds(1), 5);
    public TokenBucketRate BeaconLogs { get; init; } = new(1, TimeSpan.FromMinutes(1), 2);
    public TokenBucketRate BeaconMe { get; init; } = new(1, TimeSpan.FromSeconds(1), 5);
    public TokenBucketRate BeaconStateRead { get; init; } = new(10, TimeSpan.FromSeconds(1), 20);
    public TokenBucketRate BeaconStateWrite { get; init; } = new(10, TimeSpan.FromSeconds(1), 20);
    public TokenBucketRate Enroll { get; init; } = new(10, TimeSpan.FromMinutes(1), 10);
    public TokenBucketRate Contact { get; init; } = new(20, TimeSpan.FromSeconds(1), 20);
    public TokenBucketRate QrScans { get; init; } = new(200, TimeSpan.FromSeconds(1), 400);
    public TokenBucketRate Subscriptions { get; init; } = new(2, TimeSpan.FromSeconds(1), 5);
    public TokenBucketRate Preview { get; init; } = new(240, TimeSpan.FromMinutes(1), 60);
    public TokenBucketRate MeReads { get; init; } = new(2, TimeSpan.FromSeconds(1), 5);
    public TokenBucketRate Cookies { get; init; } = new(1, TimeSpan.FromSeconds(1), 3);
    public TokenBucketRate MeSubscribe { get; init; } = new(5, TimeSpan.FromHours(1), 5);
    public TokenBucketRate MediaUploadUrl { get; init; } = new(30, TimeSpan.FromMinutes(1), 30);
    public TokenBucketRate Admin { get; init; } = new(20, TimeSpan.FromSeconds(1), 40);

    // Per-node caps, the global limiter's two groups.
    public TokenBucketRate SubscriptionsNode { get; init; } = new(100, TimeSpan.FromSeconds(1), 200);
    public TokenBucketRate MeNode { get; init; } = new(200, TimeSpan.FromSeconds(1), 400);
}

// The partition keys. Each returns a string; a request that reaches a policy
// without the identity the policy keys on lands in that policy's one constant
// partition.
public static class RateLimitKeys
{
    public const string NoBeacon = "beacon:none";
    public const string NoPerson = "person:none";
    public const string NoToken = "token:none";
    public const string Node = "node";

    // Global limiter groups (contracts 4.0 per-node caps).
    public const string MeGroup = "node:me";
    public const string SubscriptionsGroup = "node:subscriptions";

    // HttpContext.Items slot the token capture step fills for the per-token
    // subscription policy: the lowercase sha256 hex of the token, or null.
    public const string TokenHashItem = "wmsfo:rate-limit-token-hash";

    public static string Beacon(HttpContext ctx)
    {
        var beaconId = BeaconAuthenticationHandler.TryGetBeaconId(ctx.User);
        return beaconId.HasValue
            ? "beacon:" + beaconId.Value.ToString(CultureInfo.InvariantCulture)
            : NoBeacon;
    }

    // contracts 4.0: keyed by API key id when the caller is an API key (3.6),
    // else by person. The person row id is attached by the upsert filter, which
    // runs after the limiter, so a person is usually keyed by the token's sub.
    public static string Person(HttpContext ctx)
    {
        var apiKeyId = ApiKeyAuthenticationHandler.TryGetApiKeyId(ctx.User);
        if (apiKeyId.HasValue) return "api-key:" + apiKeyId.Value.ToString(CultureInfo.InvariantCulture);
        var personId = CognitoAuth.TryGetPersonId(ctx.User);
        if (personId.HasValue) return "person:" + personId.Value.ToString(CultureInfo.InvariantCulture);
        var sub = ctx.User?.FindFirst(PersonClaims.Sub)?.Value;
        return !string.IsNullOrEmpty(sub) ? "person-sub:" + sub : NoPerson;
    }

    // The per-token subscription key: the hash the capture step stored.
    public static string SubscriptionToken(HttpContext ctx) =>
        ctx.Items.TryGetValue(TokenHashItem, out var v) && v is string hash && hash.Length > 0
            ? "token:" + hash
            : NoToken;

    // The preview document's key: the hash of the `token` query value.
    public static string PreviewToken(HttpContext ctx)
    {
        var token = ctx.Request.Query["token"].ToString();
        return string.IsNullOrEmpty(token) ? NoToken : "token:" + HashToken(token);
    }

    public static string Constant(HttpContext _) => Node;

    // The global limiter's group for a request path, or null outside the groups.
    // Every /me route and /cookies share one group; the two token-carrying
    // subscription writes share another.
    public static string? NodeGroup(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (path.StartsWithSegments("/me") || path.StartsWithSegments("/cookies")) return MeGroup;
        if (path.StartsWithSegments("/subscriptions/verify") || path.StartsWithSegments("/subscriptions/unsubscribe"))
            return SubscriptionsGroup;
        return null;
    }

    public static string HashToken(string token) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

public static class RateLimitPipeline
{
    public static void ConfigureRateLimits(this Microsoft.Extensions.DependencyInjection.IServiceCollection services, RateLimitTable? table = null)
    {
        var t = table ?? new RateLimitTable();
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejected;
            options.GlobalLimiter = CreateNodeLimiter(t);

            AddPolicy(options, RateLimitPolicies.LocationsPerBeacon, RateLimitKeys.Beacon, t.Locations);
            AddPolicy(options, RateLimitPolicies.HeartbeatPerBeacon, RateLimitKeys.Beacon, t.Heartbeat);
            AddPolicy(options, RateLimitPolicies.BeaconLogsPerBeacon, RateLimitKeys.Beacon, t.BeaconLogs);
            AddPolicy(options, RateLimitPolicies.BeaconMePerBeacon, RateLimitKeys.Beacon, t.BeaconMe);
            AddPolicy(options, RateLimitPolicies.BeaconStateReadPerBeacon, RateLimitKeys.Beacon, t.BeaconStateRead);
            AddPolicy(options, RateLimitPolicies.BeaconStateWritePerBeacon, RateLimitKeys.Beacon, t.BeaconStateWrite);

            AddPolicy(options, RateLimitPolicies.EnrollPerNode, RateLimitKeys.Constant, t.Enroll);
            AddPolicy(options, RateLimitPolicies.ContactPerNode, RateLimitKeys.Constant, t.Contact);
            AddPolicy(options, RateLimitPolicies.QrScansPerNode, RateLimitKeys.Constant, t.QrScans);

            AddPolicy(options, RateLimitPolicies.SubscriptionsPerToken, RateLimitKeys.SubscriptionToken, t.Subscriptions);
            AddPolicy(options, RateLimitPolicies.PreviewPerToken, RateLimitKeys.PreviewToken, t.Preview);

            AddPolicy(options, RateLimitPolicies.MeReadsPerPerson, RateLimitKeys.Person, t.MeReads);
            AddPolicy(options, RateLimitPolicies.CookiesPerPerson, RateLimitKeys.Person, t.Cookies);
            AddPolicy(options, RateLimitPolicies.MeSubscribePerPerson, RateLimitKeys.Person, t.MeSubscribe);
            AddPolicy(options, RateLimitPolicies.MediaUploadUrlPerPerson, RateLimitKeys.Person, t.MediaUploadUrl);
            AddPolicy(options, RateLimitPolicies.AdminPerPerson, RateLimitKeys.Person, t.Admin);
        });
    }

    // The per-node caps: one token bucket per route group under a constant key,
    // no limiter for any other route.
    public static PartitionedRateLimiter<HttpContext> CreateNodeLimiter(RateLimitTable table) =>
        PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitKeys.NodeGroup(ctx) switch
        {
            RateLimitKeys.MeGroup => RateLimitPartition.GetTokenBucketLimiter(RateLimitKeys.MeGroup, _ => MakeBucket(table.MeNode)),
            RateLimitKeys.SubscriptionsGroup => RateLimitPartition.GetTokenBucketLimiter(RateLimitKeys.SubscriptionsGroup, _ => MakeBucket(table.SubscriptionsNode)),
            _ => RateLimitPartition.GetNoLimiter("node:none"),
        });

    private static void AddPolicy(RateLimiterOptions options, string policy, Func<HttpContext, string> key, TokenBucketRate rate)
    {
        options.AddPolicy(policy, new KeyedTokenBucketPolicy(key, rate));
    }

    // The token capture step for the per-token subscription policy. The
    // limiter's partition callback is synchronous and cannot read the body, so
    // this step runs just before it on the routes tagged with that policy: it
    // takes `token` from the query, the form, or the JSON body (buffered and
    // rewound so the handler reads it again) and stores its hash in
    // RateLimitKeys.TokenHashItem.
    public static void UseRateLimitTokenCapture(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            var policy = context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            if (string.Equals(policy, RateLimitPolicies.SubscriptionsPerToken, StringComparison.Ordinal))
            {
                var token = await ReadTokenAsync(context.Request, context.RequestAborted);
                if (!string.IsNullOrEmpty(token))
                {
                    context.Items[RateLimitKeys.TokenHashItem] = RateLimitKeys.HashToken(token);
                }
            }
            await next();
        });
    }

    private static async Task<string?> ReadTokenAsync(HttpRequest request, CancellationToken ct)
    {
        var fromQuery = request.Query["token"].ToString();
        if (!string.IsNullOrEmpty(fromQuery)) return fromQuery;
        if (request.ContentLength is 0) return null;

        if (request.HasFormContentType)
        {
            // The form is cached on the request, so the handler's read sees it too.
            var form = await request.ReadFormAsync(ct);
            return form["token"].ToString();
        }

        var contentType = request.ContentType ?? "";
        if (contentType.Length > 0 && !contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        request.EnableBuffering();
        try
        {
            using var doc = await JsonDocument.ParseAsync(request.Body, cancellationToken: ct);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("token", out var t)
                && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            request.Body.Position = 0;
        }
    }

    // Contracts 4.0: over budget writes the error shape with retryAfterSeconds and Retry-After.
    private static async ValueTask OnRejected(OnRejectedContext context, CancellationToken ct)
    {
        var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
            ? (int)Math.Ceiling(wait.TotalSeconds)
            : 1;
        if (retryAfter < 1) retryAfter = 1;

        var http = context.HttpContext;
        http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        http.Response.ContentType = "application/json; charset=utf-8";
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);

        var body = ExceptionHandlingMiddleware.SerializeError(
            ApiErrorCodes.RateLimited,
            $"too many requests; retry in {retryAfter}s",
            new RateLimitDetails(retryAfter),
            http.TraceIdentifier);
        await http.Response.Body.WriteAsync(body, ct);
    }

    // A token bucket per key the key function returns.
    private sealed class KeyedTokenBucketPolicy : IRateLimiterPolicy<string>
    {
        private readonly Func<HttpContext, string> _key;
        private readonly TokenBucketRate _rate;

        public KeyedTokenBucketPolicy(Func<HttpContext, string> key, TokenBucketRate rate)
        {
            _key = key;
            _rate = rate;
        }

        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

        public RateLimitPartition<string> GetPartition(HttpContext httpContext) =>
            RateLimitPartition.GetTokenBucketLimiter(_key(httpContext), _ => MakeBucket(_rate));
    }

    // A fresh options bag per partition; RateLimiter takes ownership after add.
    private static TokenBucketRateLimiterOptions MakeBucket(TokenBucketRate rate) => new()
    {
        TokenLimit = rate.Burst,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        ReplenishmentPeriod = rate.Period,
        TokensPerPeriod = rate.TokensPerPeriod,
        AutoReplenishment = true,
    };
}
