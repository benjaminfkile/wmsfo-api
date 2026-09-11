using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Wmsfo.Api.Auth;

namespace Wmsfo.Api.Http;

// api.md 5 step 8 + contracts 4.0 rate table. Token buckets per node, keyed as
// the table says (beacon id, person id, or client IP). Over budget: 429 with the
// error shape carrying retryAfterSeconds and the Retry-After header.
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
    public const string EnrollPerIp = "wmsfo:beacon-enroll";                     // 10/min burst 10 per client IP
    public const string ContactPerIp = "wmsfo:contact";                          // 5/hour burst 5 per client IP
    public const string SubscriptionsPerIp = "wmsfo:subscriptions";              // 30/min burst 30 per client IP
    public const string PreviewPerIp = "wmsfo:preview";                          // 60/min burst 60 per client IP
    public const string CookiesPerPerson = "wmsfo:cookies";                      // 1/s burst 3 per person id
    public const string MeSubscribePerPerson = "wmsfo:me-subscribe";             // 5/hour burst 5 per person id
    public const string MediaUploadUrlPerPerson = "wmsfo:media-upload-url";      // 30/min burst 30 per person id
    public const string AdminPerPerson = "wmsfo:admin";                          // 20/s burst 40 per person id

    // The rate limits are attached to the endpoints the RateLimiter middleware
    // sees; endpoints without a policy are unlimited. api.md 5: the callbacks and
    // /api/health carry DisableRateLimiting.
}

public static class RateLimitPipeline
{
    public static void ConfigureRateLimits(this Microsoft.Extensions.DependencyInjection.IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = OnRejected;

            AddBeaconTokenBucket(options, RateLimitPolicies.LocationsPerBeacon, tokensPerPeriod: 10, period: TimeSpan.FromSeconds(1), burst: 20);
            AddBeaconTokenBucket(options, RateLimitPolicies.HeartbeatPerBeacon, tokensPerPeriod: 1, period: TimeSpan.FromSeconds(1), burst: 5);
            AddBeaconTokenBucket(options, RateLimitPolicies.BeaconLogsPerBeacon, tokensPerPeriod: 1, period: TimeSpan.FromMinutes(1), burst: 2);
            AddBeaconTokenBucket(options, RateLimitPolicies.BeaconMePerBeacon, tokensPerPeriod: 1, period: TimeSpan.FromSeconds(1), burst: 5);

            AddIpTokenBucket(options, RateLimitPolicies.EnrollPerIp, tokensPerPeriod: 10, period: TimeSpan.FromMinutes(1), burst: 10);
            AddIpTokenBucket(options, RateLimitPolicies.ContactPerIp, tokensPerPeriod: 5, period: TimeSpan.FromHours(1), burst: 5);
            AddIpTokenBucket(options, RateLimitPolicies.SubscriptionsPerIp, tokensPerPeriod: 30, period: TimeSpan.FromMinutes(1), burst: 30);
            AddIpTokenBucket(options, RateLimitPolicies.PreviewPerIp, tokensPerPeriod: 60, period: TimeSpan.FromMinutes(1), burst: 60);

            AddPersonTokenBucket(options, RateLimitPolicies.CookiesPerPerson, tokensPerPeriod: 1, period: TimeSpan.FromSeconds(1), burst: 3);
            AddPersonTokenBucket(options, RateLimitPolicies.MeSubscribePerPerson, tokensPerPeriod: 5, period: TimeSpan.FromHours(1), burst: 5);
            AddPersonTokenBucket(options, RateLimitPolicies.MediaUploadUrlPerPerson, tokensPerPeriod: 30, period: TimeSpan.FromMinutes(1), burst: 30);
            AddPersonTokenBucket(options, RateLimitPolicies.AdminPerPerson, tokensPerPeriod: 20, period: TimeSpan.FromSeconds(1), burst: 40);
        });
    }

    private static void AddBeaconTokenBucket(RateLimiterOptions options, string policy, int tokensPerPeriod, TimeSpan period, int burst)
    {
        options.AddPolicy(policy, new BeaconIdPolicy(tokensPerPeriod, period, burst));
    }

    private static void AddIpTokenBucket(RateLimiterOptions options, string policy, int tokensPerPeriod, TimeSpan period, int burst)
    {
        options.AddPolicy(policy, new ClientIpPolicy(tokensPerPeriod, period, burst));
    }

    private static void AddPersonTokenBucket(RateLimiterOptions options, string policy, int tokensPerPeriod, TimeSpan period, int burst)
    {
        options.AddPolicy(policy, new PersonIdPolicy(tokensPerPeriod, period, burst));
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

    private sealed class BeaconIdPolicy : IRateLimiterPolicy<string>
    {
        private readonly TokenBucketRateLimiterOptions _template;

        public BeaconIdPolicy(int tokensPerPeriod, TimeSpan period, int burst)
        {
            _template = MakeBucket(tokensPerPeriod, period, burst);
        }

        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

        public RateLimitPartition<string> GetPartition(HttpContext httpContext)
        {
            var beaconId = BeaconAuthenticationHandler.TryGetBeaconId(httpContext.User);
            var key = beaconId.HasValue
                ? $"beacon:{beaconId.Value.ToString(CultureInfo.InvariantCulture)}"
                : $"beacon-ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
            return RateLimitPartition.GetTokenBucketLimiter(key, _ => Copy(_template));
        }
    }

    private sealed class PersonIdPolicy : IRateLimiterPolicy<string>
    {
        private readonly TokenBucketRateLimiterOptions _template;

        public PersonIdPolicy(int tokensPerPeriod, TimeSpan period, int burst)
        {
            _template = MakeBucket(tokensPerPeriod, period, burst);
        }

        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

        public RateLimitPartition<string> GetPartition(HttpContext httpContext)
        {
            // contracts 4.0: the /admin/* bucket is keyed by person id, or by
            // API key id when the caller is an API key (3.6).
            var apiKeyId = ApiKeyAuthenticationHandler.TryGetApiKeyId(httpContext.User);
            string key;
            if (apiKeyId.HasValue)
            {
                key = $"api-key:{apiKeyId.Value.ToString(CultureInfo.InvariantCulture)}";
                return RateLimitPartition.GetTokenBucketLimiter(key, _ => Copy(_template));
            }
            var personId = CognitoAuth.TryGetPersonId(httpContext.User);
            if (personId.HasValue)
            {
                key = $"person:{personId.Value.ToString(CultureInfo.InvariantCulture)}";
            }
            else
            {
                var sub = httpContext.User?.FindFirst(PersonClaims.Sub)?.Value;
                key = !string.IsNullOrEmpty(sub)
                    ? $"person-sub:{sub}"
                    : $"person-ip:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
            }
            return RateLimitPartition.GetTokenBucketLimiter(key, _ => Copy(_template));
        }
    }

    private sealed class ClientIpPolicy : IRateLimiterPolicy<string>
    {
        private readonly TokenBucketRateLimiterOptions _template;

        public ClientIpPolicy(int tokensPerPeriod, TimeSpan period, int burst)
        {
            _template = MakeBucket(tokensPerPeriod, period, burst);
        }

        public Func<OnRejectedContext, CancellationToken, ValueTask>? OnRejected => null;

        public RateLimitPartition<string> GetPartition(HttpContext httpContext)
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetTokenBucketLimiter($"ip:{ip}", _ => Copy(_template));
        }
    }

    private static TokenBucketRateLimiterOptions MakeBucket(int tokensPerPeriod, TimeSpan period, int burst) => new()
    {
        TokenLimit = burst,
        QueueLimit = 0,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        ReplenishmentPeriod = period,
        TokensPerPeriod = tokensPerPeriod,
        AutoReplenishment = true,
    };

    // A fresh options bag per partition; RateLimiter takes ownership after add.
    private static TokenBucketRateLimiterOptions Copy(TokenBucketRateLimiterOptions t) => new()
    {
        TokenLimit = t.TokenLimit,
        QueueLimit = t.QueueLimit,
        QueueProcessingOrder = t.QueueProcessingOrder,
        ReplenishmentPeriod = t.ReplenishmentPeriod,
        TokensPerPeriod = t.TokensPerPeriod,
        AutoReplenishment = t.AutoReplenishment,
    };
}
