using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Tests;

// A119: every rate limit policy partitions on a person, an API key, a beacon,
// a token hash, or a constant; the per-node caps are the global limiter's route
// groups; and the limiter source never reads the caller's address.
public class RateLimitKeysTests
{
    private static DefaultHttpContext Context(string path = "/", params Claim[] claims)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(claims, claims.Length > 0 ? "test" : null));
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
        return ctx;
    }

    [Fact]
    public void Person_key_is_the_person_id_when_attached()
    {
        var ctx = Context("/me/cookies", new Claim(PersonClaims.PersonId, "42"), new Claim(PersonClaims.Sub, "s-1"));
        Assert.Equal("person:42", RateLimitKeys.Person(ctx));
    }

    [Fact]
    public void Person_key_is_the_sub_before_the_upsert()
    {
        var ctx = Context("/me/cookies", new Claim(PersonClaims.Sub, "s-1"));
        Assert.Equal("person-sub:s-1", RateLimitKeys.Person(ctx));
        var other = Context("/me/cookies", new Claim(PersonClaims.Sub, "s-2"));
        Assert.NotEqual(RateLimitKeys.Person(ctx), RateLimitKeys.Person(other));
    }

    [Fact]
    public void Person_key_is_the_api_key_id_for_an_api_key()
    {
        var ctx = Context("/admin/events", new Claim(ApiKeyClaims.ApiKeyId, "9"));
        Assert.Equal("api-key:9", RateLimitKeys.Person(ctx));
    }

    [Fact]
    public void Person_key_without_a_person_is_one_constant_partition()
    {
        Assert.Equal(RateLimitKeys.NoPerson, RateLimitKeys.Person(Context("/me/cookies")));
    }

    [Fact]
    public void Beacon_key_is_the_beacon_id_else_one_constant_partition()
    {
        Assert.Equal("beacon:5", RateLimitKeys.Beacon(Context("/locations", new Claim(BeaconClaims.BeaconId, "5"))));
        Assert.Equal(RateLimitKeys.NoBeacon, RateLimitKeys.Beacon(Context("/locations")));
    }

    [Fact]
    public void Subscription_token_key_is_the_captured_hash_else_one_constant_partition()
    {
        var ctx = Context("/subscriptions/verify");
        Assert.Equal(RateLimitKeys.NoToken, RateLimitKeys.SubscriptionToken(ctx));
        ctx.Items[RateLimitKeys.TokenHashItem] = RateLimitKeys.HashToken("wsv_abc");
        Assert.Equal("token:" + RateLimitKeys.HashToken("wsv_abc"), RateLimitKeys.SubscriptionToken(ctx));
    }

    [Fact]
    public void Preview_key_is_the_hash_of_the_query_token()
    {
        var ctx = Context("/preview/document");
        Assert.Equal(RateLimitKeys.NoToken, RateLimitKeys.PreviewToken(ctx));
        ctx.Request.QueryString = new QueryString("?token=wpv_xyz");
        Assert.Equal("token:" + RateLimitKeys.HashToken("wpv_xyz"), RateLimitKeys.PreviewToken(ctx));
    }

    [Fact]
    public void Token_hash_is_lowercase_sha256_hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", RateLimitKeys.HashToken("abc"));
    }

    [Fact]
    public void Constant_key_ignores_the_request()
    {
        Assert.Equal(RateLimitKeys.Node, RateLimitKeys.Constant(Context("/contact")));
        Assert.Equal(RateLimitKeys.Node, RateLimitKeys.Constant(Context("/contact", new Claim(PersonClaims.Sub, "s-1"))));
    }

    [Theory]
    [InlineData("/me/cookies", RateLimitKeys.MeGroup)]
    [InlineData("/me", RateLimitKeys.MeGroup)]
    [InlineData("/me/preferences", RateLimitKeys.MeGroup)]
    [InlineData("/me/seen", RateLimitKeys.MeGroup)]
    [InlineData("/me/subscriptions", RateLimitKeys.MeGroup)]
    [InlineData("/me/subscriptions/3/resend-verification", RateLimitKeys.MeGroup)]
    [InlineData("/cookies", RateLimitKeys.MeGroup)]
    [InlineData("/subscriptions/verify", RateLimitKeys.SubscriptionsGroup)]
    [InlineData("/subscriptions/unsubscribe", RateLimitKeys.SubscriptionsGroup)]
    [InlineData("/media", null)]
    [InlineData("/contact", null)]
    [InlineData("/admin/events", null)]
    [InlineData("/api/health", null)]
    public void Node_group_follows_the_route(string path, string? group)
    {
        Assert.Equal(group, RateLimitKeys.NodeGroup(Context(path)));
    }

    [Fact]
    public void Node_cap_is_shared_by_every_route_in_a_group_and_absent_elsewhere()
    {
        var table = new RateLimitTable { MeNode = new TokenBucketRate(1, TimeSpan.FromHours(1), 3) };
        using var limiter = RateLimitPipeline.CreateNodeLimiter(table);

        Assert.True(limiter.AttemptAcquire(Context("/me/cookies", new Claim(PersonClaims.Sub, "a"))).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Context("/me/subscriptions", new Claim(PersonClaims.Sub, "b"))).IsAcquired);
        Assert.True(limiter.AttemptAcquire(Context("/cookies", new Claim(PersonClaims.Sub, "c"))).IsAcquired);
        using var rejected = limiter.AttemptAcquire(Context("/me/cookies", new Claim(PersonClaims.Sub, "d")));
        Assert.False(rejected.IsAcquired);
        Assert.True(rejected.TryGetMetadata(MetadataName.RetryAfter, out _));

        // Other groups and other routes are untouched.
        Assert.True(limiter.AttemptAcquire(Context("/subscriptions/verify")).IsAcquired);
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(limiter.AttemptAcquire(Context("/contact")).IsAcquired);
        }
    }

    [Fact]
    public void Default_table_is_the_contracts_4_0_table()
    {
        var t = new RateLimitTable();
        var s = TimeSpan.FromSeconds(1);
        var min = TimeSpan.FromMinutes(1);
        Assert.Equal(new TokenBucketRate(10, s, 20), t.Locations);
        Assert.Equal(new TokenBucketRate(1, s, 5), t.Heartbeat);
        Assert.Equal(new TokenBucketRate(1, min, 2), t.BeaconLogs);
        Assert.Equal(new TokenBucketRate(1, s, 5), t.BeaconMe);
        Assert.Equal(new TokenBucketRate(10, s, 20), t.BeaconStateRead);
        Assert.Equal(new TokenBucketRate(10, s, 20), t.BeaconStateWrite);
        Assert.Equal(new TokenBucketRate(10, min, 10), t.Enroll);
        Assert.Equal(new TokenBucketRate(20, s, 20), t.Contact);
        Assert.Equal(new TokenBucketRate(200, s, 400), t.QrScans);
        Assert.Equal(new TokenBucketRate(2, s, 5), t.Subscriptions);
        Assert.Equal(new TokenBucketRate(100, s, 200), t.SubscriptionsNode);
        Assert.Equal(new TokenBucketRate(240, min, 60), t.Preview);
        Assert.Equal(new TokenBucketRate(2, s, 5), t.MeReads);
        Assert.Equal(new TokenBucketRate(1, s, 3), t.Cookies);
        Assert.Equal(new TokenBucketRate(5, TimeSpan.FromHours(1), 5), t.MeSubscribe);
        Assert.Equal(new TokenBucketRate(1, s, 3), t.MeSeen);
        Assert.Equal(new TokenBucketRate(1, s, 3), t.MePreferences);
        Assert.Equal(new TokenBucketRate(200, s, 400), t.MeNode);
        Assert.Equal(new TokenBucketRate(30, min, 30), t.MediaUploadUrl);
        Assert.Equal(new TokenBucketRate(20, s, 40), t.Admin);
    }

    [Fact]
    public void Rate_limit_source_never_reads_the_client_address()
    {
        var path = Path.Combine(ContractsPaths.RepoRoot, "src", "Wmsfo.Api", "Http", "RateLimits.cs");
        var source = File.ReadAllText(path);
        Assert.DoesNotContain("RemoteIpAddress", source, StringComparison.Ordinal);
    }
}
