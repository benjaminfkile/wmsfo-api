using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Http;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A119: the contracts 4.0 table against the real handlers and the real limiter.
// Each test starts its own host so every bucket starts full.
public sealed class A119RateLimitTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public A119RateLimitTests(PostgresFixture fixture) { _fixture = fixture; }

    public Task InitializeAsync() => A12MeEndpointsTests.MigrateAndCleanAsync(_fixture.ConnectionString);

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Sixth_me_cookies_inside_a_second_for_one_person_is_429()
    {
        await using var host = await A12Host.StartAsync(_fixture.ConnectionString, new RateLimitTable());
        for (var i = 0; i < 5; i++)
        {
            using var ok = await SendAsync(host.Client, HttpMethod.Get, "/me/cookies", DevStaticTokens.PersonToken);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
        using var limited = await SendAsync(host.Client, HttpMethod.Get, "/me/cookies", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retry));
        Assert.NotEmpty(retry!);
        using var doc = JsonDocument.Parse(await limited.Content.ReadAsStringAsync());
        Assert.Equal(ApiErrorCodes.RateLimited, doc.RootElement.GetProperty("code").GetString());
        Assert.True(doc.RootElement.GetProperty("details").GetProperty("retryAfterSeconds").GetInt32() >= 1);
    }

    [Fact]
    public async Task Two_persons_do_not_share_a_bucket()
    {
        await using var host = await A12Host.StartAsync(_fixture.ConnectionString, new RateLimitTable());
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 6; i++)
        {
            using var r = await SendAsync(host.Client, HttpMethod.Get, "/me/cookies", DevStaticTokens.PersonToken);
            last = r.StatusCode;
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, last);

        using var other = await SendAsync(host.Client, HttpMethod.Get, "/me/cookies", DevStaticTokens.EditorToken);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Per_node_me_cap_trips_across_persons_and_routes()
    {
        var table = new RateLimitTable { MeNode = new TokenBucketRate(1, TimeSpan.FromHours(1), 3) };
        await using var host = await A12Host.StartAsync(_fixture.ConnectionString, table);

        using (var a = await SendAsync(host.Client, HttpMethod.Get, "/me/cookies", DevStaticTokens.PersonToken))
            Assert.Equal(HttpStatusCode.OK, a.StatusCode);
        using (var b = await SendAsync(host.Client, HttpMethod.Get, "/me/subscriptions", DevStaticTokens.PersonToken))
            Assert.Equal(HttpStatusCode.OK, b.StatusCode);
        using (var c = await SendAsync(host.Client, HttpMethod.Get, "/me/cookies", DevStaticTokens.EditorToken))
            Assert.Equal(HttpStatusCode.OK, c.StatusCode);

        // A fresh person on a fresh route: its own buckets are full, the node's is empty.
        using var d = await SendAsync(host.Client, HttpMethod.Get, "/me/subscriptions", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, d.StatusCode);
    }

    [Fact]
    public async Task Subscription_writes_are_keyed_by_the_token_they_carry()
    {
        await using var host = await A12Host.StartAsync(_fixture.ConnectionString, new RateLimitTable());
        var tokenA = Keys.SubscribeVerifyPrefix + new string('a', Keys.TokenBodyChars);
        var tokenB = Keys.SubscribeVerifyPrefix + new string('b', Keys.TokenBodyChars);

        for (var i = 0; i < 5; i++)
        {
            using var r = await PostJsonAsync(host.Client, "/subscriptions/verify", $"{{\"token\":\"{tokenA}\"}}");
            // The handler still reads the body after the token capture step: unknown token, 404.
            Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        }
        using (var limited = await PostJsonAsync(host.Client, "/subscriptions/verify", $"{{\"token\":\"{tokenA}\"}}"))
            Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        using (var other = await PostJsonAsync(host.Client, "/subscriptions/verify", $"{{\"token\":\"{tokenB}\"}}"))
            Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        // The unsubscribe query token is a different token, so a different bucket.
        var unsub = Keys.SubscribeUnsubscribePrefix + new string('c', Keys.TokenBodyChars);
        using (var q = await host.Client.PostAsync("/subscriptions/unsubscribe?token=" + unsub, null))
            Assert.Equal(HttpStatusCode.NotFound, q.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_routes_carry_the_admin_policy_and_trip_it()
    {
        await using var host = await A19Host.StartAsync(_fixture.ConnectionString,
            new RateLimitTable { Admin = new TokenBucketRate(1, TimeSpan.FromHours(1), 2) });

        var policies = PoliciesByRoute(host.App);
        foreach (var route in new[]
        {
            "GET /admin/snapshot",
            "POST /admin/snapshot/rebuild",
            "GET /admin/live",
            "POST /admin/live/republish",
        })
        {
            Assert.True(policies.TryGetValue(route, out var policy), route + " is not mapped");
            Assert.Equal(RateLimitPolicies.AdminPerPerson, policy);
        }

        for (var i = 0; i < 2; i++)
        {
            using var r = await SendAsync(host.Client, HttpMethod.Get, "/admin/snapshot", DevStaticTokens.AdminToken);
            Assert.NotEqual(HttpStatusCode.TooManyRequests, r.StatusCode);
        }
        using var limited = await SendAsync(host.Client, HttpMethod.Get, "/admin/live", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
    }

    [Fact]
    public async Task Every_admin_and_me_route_carries_its_table_policy()
    {
        await using var host = await A19Host.StartAsync(_fixture.ConnectionString);
        var policies = PoliciesByRoute(host.App);

        foreach (var (route, policy) in policies)
        {
            var path = route[(route.IndexOf(' ') + 1)..];
            if (path.StartsWith("/admin/", StringComparison.Ordinal))
            {
                var expected = route == "POST /admin/media/upload-url"
                    ? RateLimitPolicies.MediaUploadUrlPerPerson
                    : RateLimitPolicies.AdminPerPerson;
                Assert.True(expected == policy, $"{route}: expected {expected}, got {policy ?? "none"}");
            }
        }

        Assert.Equal(RateLimitPolicies.MeReadsPerPerson, policies["GET /me/subscriptions"]);
        Assert.Equal(RateLimitPolicies.MeReadsPerPerson, policies["DELETE /me/subscriptions/{id:long}"]);
        Assert.Equal(RateLimitPolicies.MeReadsPerPerson, policies["GET /me/cookies"]);
        Assert.Equal(RateLimitPolicies.MeReadsPerPerson, policies["GET /me/notifications"]);
        Assert.Equal(RateLimitPolicies.MeSeenPerPerson, policies["PUT /me/notifications/seen"]);
        Assert.Equal(RateLimitPolicies.MeSubscribePerPerson, policies["POST /me/subscriptions"]);
        Assert.Equal(RateLimitPolicies.MeSubscribePerPerson, policies["POST /me/subscriptions/{id:long}/resend-verification"]);
        Assert.Equal(RateLimitPolicies.CookiesPerPerson, policies["POST /cookies"]);
        Assert.Equal(RateLimitPolicies.ContactPerNode, policies["POST /contact"]);
        Assert.Equal(RateLimitPolicies.QrScansPerNode, policies["POST /qr-codes/{tag}/scans"]);
        Assert.Equal(RateLimitPolicies.SubscriptionsPerToken, policies["POST /subscriptions/verify"]);
        Assert.Equal(RateLimitPolicies.SubscriptionsPerToken, policies["POST /subscriptions/unsubscribe"]);
        Assert.Equal(RateLimitPolicies.PreviewPerToken, policies["GET /preview/document"]);
        Assert.Equal(RateLimitPolicies.EnrollPerNode, policies["POST /beacons/enroll"]);
    }

    // "METHOD pattern" to the endpoint's rate limit policy name, or null.
    private static Dictionary<string, string?> PoliciesByRoute(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var source in app.Services.GetServices<EndpointDataSource>())
        {
            foreach (var endpoint in source.Endpoints.OfType<RouteEndpoint>())
            {
                var methods = endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods;
                if (methods is null) continue;
                var policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
                foreach (var method in methods)
                {
                    result[method + " " + endpoint.RoutePattern.RawText] = policy;
                }
            }
        }
        return result;
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string token)
    {
        using var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }

    private static Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));
}
