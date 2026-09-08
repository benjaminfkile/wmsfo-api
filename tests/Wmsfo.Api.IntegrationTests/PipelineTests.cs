using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A5 acceptance criterion 745: every pipeline-owned code in contracts 4.7 is
// produced by a test — validation_failed, unauthenticated, forbidden,
// mfa_required, payload_too_large, unsupported_media_type, rate_limited,
// internal_error, unavailable. Plus the specific asserts named in A5:
// both-headers 400, policy matrix, rate-limited 429 with header, forwarded
// headers with two hops.
public sealed class PipelineTests
{
    private const string BeaconKey = "wbk_" + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";  // 47 chars
    private const string AdminBeaconKey = "wbk_" + "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static async Task<PipelineHost> StartAsync()
    {
        var host = await PipelineHost.StartAsync();
        host.Beacons.Add(BeaconKey, id: 1);
        host.Beacons.Add(AdminBeaconKey, id: 2, role: BeaconClaims.RoleAdmin);
        return host;
    }

    // --- code: unauthenticated ---
    [Fact]
    public async Task Beacon_missing_header_is_401_unauthenticated()
    {
        await using var host = await StartAsync();
        using var response = await host.Client.GetAsync("/test/beacon");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await ReadErrorAsync(response);
        Assert.Equal(ApiErrorCodes.Unauthenticated, body.Code);
    }

    [Fact]
    public async Task Beacon_malformed_key_is_401_unauthenticated()
    {
        await using var host = await StartAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/beacon");
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, "not-a-valid-key");
        using var response = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await ReadErrorAsync(response);
        Assert.Equal(ApiErrorCodes.Unauthenticated, body.Code);
    }

    [Fact]
    public async Task Beacon_revoked_is_401_unauthenticated()
    {
        await using var host = await StartAsync();
        const string revoked = "wbk_" + "ccccccccccccccccccccccccccccccccccccccccccc";
        host.Beacons.Add(revoked, id: 9, revokedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/beacon");
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, revoked);
        using var response = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthenticated, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task Beacon_valid_key_succeeds()
    {
        await using var host = await StartAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/beacon");
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, BeaconKey);
        using var response = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Cognito_missing_token_is_401_unauthenticated()
    {
        await using var host = await StartAsync();
        using var response = await host.Client.GetAsync("/test/person");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Unauthenticated, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task Cognito_valid_token_succeeds()
    {
        await using var host = await StartAsync();
        using var response = await SendWithBearerAsync(host, HttpMethod.Get, "/test/person", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- code: forbidden (both scheme families) ---
    [Fact]
    public async Task BeaconAdmin_role_beacon_is_403_forbidden()
    {
        await using var host = await StartAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/beacon-admin");
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, BeaconKey);  // role=beacon
        using var response = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task Person_without_editor_is_forbidden_on_editor_route()
    {
        await using var host = await StartAsync();
        using var response = await SendWithBearerAsync(host, HttpMethod.Get, "/test/editor", DevStaticTokens.PersonToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Forbidden, (await ReadErrorAsync(response)).Code);
    }

    // --- code: mfa_required ---
    [Fact]
    public async Task Admin_without_totp_is_mfa_required()
    {
        await using var host = await StartAsync();
        using var response = await SendWithBearerAsync(host, HttpMethod.Get, "/admin/test", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(ApiErrorCodes.MfaRequired, (await ReadErrorAsync(response)).Code);
    }

    [Fact]
    public async Task Admin_with_totp_passes_the_gate()
    {
        await using var host = await StartAsync();
        var checker = (ScriptedAdminTotpChecker)host.App.Services.GetRequiredService<IAdminTotpChecker>();
        checker.SetEnabled(DevStaticTokens.AdminSub);
        using var response = await SendWithBearerAsync(host, HttpMethod.Get, "/admin/test", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // --- code: validation_failed (both-headers and generic validator) ---
    [Fact]
    public async Task Both_auth_headers_is_400_validation_failed()
    {
        await using var host = await StartAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/beacon");
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, BeaconKey);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        using var response = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadErrorAsync(response);
        Assert.Equal(ApiErrorCodes.ValidationFailed, body.Code);

        using var doc = JsonDocument.Parse(body.Raw);
        var fields = doc.RootElement.GetProperty("details").GetProperty("fields");
        Assert.True(fields.TryGetProperty("authorization", out _));
        Assert.True(fields.TryGetProperty("xBeaconKey", out _));
    }

    [Fact]
    public async Task Validation_failed_details_fields_shape()
    {
        await using var host = await StartAsync();
        using var response = await host.Client.PostAsync("/test/validate", new StringContent("{}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadErrorAsync(response);
        Assert.Equal(ApiErrorCodes.ValidationFailed, body.Code);

        using var doc = JsonDocument.Parse(body.Raw);
        var fields = doc.RootElement.GetProperty("details").GetProperty("fields");
        Assert.Equal("must be between -90 and 90", fields.GetProperty("lat").GetString());
        Assert.Equal("must be between -180 and 180", fields.GetProperty("lng").GetString());
    }

    // --- code: payload_too_large ---
    [Fact]
    public async Task Body_over_route_limit_is_413_payload_too_large()
    {
        await using var host = await StartAsync();
        var large = new byte[BodyLimits.Heartbeat + 1];
        using var content = new ByteArrayContent(large);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await host.Client.PostAsync("/test/tiny", content);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Equal(ApiErrorCodes.PayloadTooLarge, (await ReadErrorAsync(response)).Code);
    }

    // --- code: unsupported_media_type ---
    [Fact]
    public async Task Wrong_content_type_is_415_unsupported_media_type()
    {
        await using var host = await StartAsync();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");
        using var response = await host.Client.PostAsync("/test/plain", content);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal(ApiErrorCodes.UnsupportedMediaType, (await ReadErrorAsync(response)).Code);
    }

    // --- code: rate_limited ---
    [Fact]
    public async Task Rate_limited_answers_429_with_retry_after()
    {
        await using var host = await StartAsync();
        // Heartbeat bucket: 1/s replenishment, burst 5. Six back-to-back
        // requests exhaust the bucket and the sixth answers 429.
        HttpResponseMessage? last = null;
        for (var i = 0; i < 6; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/test/rate-limited");
            req.Headers.Add(BeaconAuthenticationHandler.HeaderName, BeaconKey);
            last?.Dispose();
            last = await host.Client.SendAsync(req);
        }
        Assert.NotNull(last);
        Assert.Equal(HttpStatusCode.TooManyRequests, last!.StatusCode);
        Assert.True(last.Headers.TryGetValues("Retry-After", out var values));
        Assert.NotEmpty(values!);

        var body = await ReadErrorAsync(last);
        Assert.Equal(ApiErrorCodes.RateLimited, body.Code);
        using var doc = JsonDocument.Parse(body.Raw);
        var retryAfterSeconds = doc.RootElement.GetProperty("details").GetProperty("retryAfterSeconds").GetInt32();
        Assert.True(retryAfterSeconds >= 1);
        last.Dispose();
    }

    // --- code: internal_error ---
    [Fact]
    public async Task Unhandled_exception_is_500_internal_error()
    {
        await using var host = await StartAsync();
        using var response = await host.Client.GetAsync("/test/boom");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(ApiErrorCodes.InternalError, (await ReadErrorAsync(response)).Code);
    }

    // --- code: unavailable ---
    [Fact]
    public async Task Readiness_gate_answers_503_unavailable()
    {
        await using var host = await StartAsync();
        host.Ready = false;
        using var response = await host.Client.GetAsync("/test/beacon");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(ApiErrorCodes.Unavailable, (await ReadErrorAsync(response)).Code);

        // /api/health is the one path that bypasses the readiness gate so the
        // gateway's health prober keeps working during startup.
        using var health = await host.Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // --- Policy matrix over a stub /admin/* route ---
    [Fact]
    public async Task Admin_policy_matrix()
    {
        await using var host = await StartAsync();
        var checker = (ScriptedAdminTotpChecker)host.App.Services.GetRequiredService<IAdminTotpChecker>();
        checker.SetEnabled(DevStaticTokens.AdminSub);
        checker.SetEnabled(DevStaticTokens.EditorSub);

        // 1. Anonymous -> 401.
        Assert.Equal(HttpStatusCode.Unauthorized, (await host.Client.GetAsync("/admin/test")).StatusCode);
        // 2. Person (no group) -> 403 forbidden (policy denial).
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendWithBearerAsync(host, HttpMethod.Get, "/admin/test", DevStaticTokens.PersonToken)).StatusCode);
        // 3. Editor -> 403 forbidden (Admin policy excludes editors).
        Assert.Equal(HttpStatusCode.Forbidden,
            (await SendWithBearerAsync(host, HttpMethod.Get, "/admin/test", DevStaticTokens.EditorToken)).StatusCode);
        // 4. Admin with TOTP -> 200.
        Assert.Equal(HttpStatusCode.OK,
            (await SendWithBearerAsync(host, HttpMethod.Get, "/admin/test", DevStaticTokens.AdminToken)).StatusCode);
        // 5. Admin without TOTP -> 403 mfa_required. The 5-minute cache is
        // dropped explicitly so the scripted checker's next answer wins.
        checker.SetDisabled(DevStaticTokens.AdminSub);
        host.TotpGate.ClearCache();
        var r5 = await SendWithBearerAsync(host, HttpMethod.Get, "/admin/test", DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.Forbidden, r5.StatusCode);
        Assert.Equal(ApiErrorCodes.MfaRequired, (await ReadErrorAsync(r5)).Code);
    }

    // --- Forwarded headers: two hops ---
    [Fact]
    public async Task Forwarded_headers_two_hops_extract_client_ip()
    {
        await using var host = await StartAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/ip");
        // Two hops match the fleet layout: client -> load balancer -> gateway
        // -> app. Each proxy appends the incoming source IP to X-Forwarded-For,
        // so the header the app sees is "<client>, <lb>". TrustedProxyHops = 2
        // peels both entries; RemoteIpAddress ends up at the leftmost entry.
        req.Headers.Add("X-Forwarded-For", "203.0.113.9, 198.51.100.1");
        using var response = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("203.0.113.9", doc.RootElement.GetProperty("ip").GetString());
    }

    [Fact]
    public async Task Realtime_paths_bypass_forwarded_headers_middleware()
    {
        // api.md 5 step 2: /realtime/* must see the raw X-Forwarded-For (the
        // callback guard uses it). We echo the header value verbatim; the
        // middleware would consume it and hand us "" instead.
        await using var host = await StartAsync();
        using var req = new HttpRequestMessage(HttpMethod.Get, "/realtime/echo");
        req.Headers.Add("X-Forwarded-For", "203.0.113.9, 198.51.100.1");
        using var response = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("203.0.113.9, 198.51.100.1", doc.RootElement.GetProperty("xff").GetString());
    }

    // --- Cache-Control: no-store on every response ---
    [Fact]
    public async Task Every_response_has_no_store_cache_control()
    {
        await using var host = await StartAsync();
        using var response = await host.Client.GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var value = string.Join(",", response.Headers.CacheControl?.ToString() ?? "");
        if (string.IsNullOrEmpty(value) && response.Headers.TryGetValues("Cache-Control", out var raw))
        {
            value = string.Join(",", raw);
        }
        Assert.Contains("no-store", value);
    }

    // --- serverTime is stamped on beacon endpoints ---
    [Fact]
    public async Task Beacon_endpoints_stamp_server_time()
    {
        await using var host = await StartAsync();
        host.Clock.Now = new DateTimeOffset(2026, 12, 22, 1, 31, 7, TimeSpan.Zero);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/test/beacon-stamped");
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, BeaconKey);
        using var response = await host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("2026-12-22T01:31:07.000Z", doc.RootElement.GetProperty("serverTime").GetString());
    }

    private static async Task<HttpResponseMessage> SendWithBearerAsync(PipelineHost host, HttpMethod method, string path, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await host.Client.SendAsync(req);
    }

    private static async Task<ErrorBody> ReadErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        var code = doc.RootElement.GetProperty("code").GetString() ?? "";
        return new ErrorBody(code, text);
    }

    private sealed record ErrorBody(string Code, string Raw);
}
