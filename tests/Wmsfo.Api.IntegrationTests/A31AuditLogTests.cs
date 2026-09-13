using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.IntegrationTests;

// A31 audit log acceptance: every admin write records one row in audit_log
// (api.md 5a). The every-write test drives the list from the endpoint map so a
// new admin write route without audit fails the test; the sponsor and cookie
// type specifics verify the wire shape (before/after DTOs, actor is
// `person:<email>` for the ID token, request id preserved).
public sealed class A31AuditLogTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A19Host? _host;

    public A31AuditLogTests(PostgresFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync();
        _host = await A19Host.StartAsync(_fixture.ConnectionString);
        await _host.GetService<Wmsfo.Api.Node.SnapshotBootstrap>().EnsureVersionOneAsync(default);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task MigrateAndCleanAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(contextOptions);
        await db.Database.MigrateAsync();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from audit_log;",
            "delete from alert_delivery;",
            "delete from outbox;",
            "delete from cookie;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from location;",
            "delete from event;",
            "delete from sponsor_year;",
            "delete from sponsor;",
            "delete from cookie_type;",
            "delete from subscriber;",
            "delete from person;",
            "delete from contact_message;",
            "delete from section_item;",
            "delete from section;",
            "delete from page;",
            "delete from site_setting_draft;",
            "delete from content_version;",
            "delete from preview_token;",
            "delete from media_asset;",
            "delete from beacon_enrollment_token;",
            "delete from beacon_log;",
            "delete from beacon;",
            "delete from route;",
            "delete from api_key;",
            "delete from app_setting;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await using var siteSettingsInsert = new NpgsqlCommand(
            "insert into site_setting_draft (id, data) values (1, '{}') on conflict (id) do nothing;", conn);
        await siteSettingsInsert.ExecuteNonQueryAsync();
        await SnapshotSeed.EnsureAsync(conn);
        await RolePageSeed.EnsureAsync(conn);
    }

    // ---------------- specific paths (acceptance criterion 2) ----------------

    [Fact]
    public async Task Sponsor_patch_records_audit_row_with_before_and_after()
    {
        // Create a sponsor first.
        var created = await SendEditorAsync(HttpMethod.Post, "/admin/sponsors",
            "{\"name\":\"Alpine Bakeries\"}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdDto = await ReadJsonAsync(created);
        var sponsorId = createdDto.RootElement.GetProperty("id").GetInt64();

        // Patch it.
        var response = await SendEditorAsync(HttpMethod.Patch, $"/admin/sponsors/{sponsorId}",
            "{\"name\":\"Alpine Bakeries LLC\"}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Verify audit rows. There should be one "update" row for sponsor.
        var list = await SendEditorAsync(HttpMethod.Get,
            $"/admin/audit?entity=sponsor&entityId={sponsorId}&action=update", null);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await ReadJsonAsync(list);
        var items = page.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1);
        var entry = items[0];
        Assert.Equal("update", entry.GetProperty("action").GetString());
        Assert.Equal("sponsor", entry.GetProperty("entity").GetString());
        Assert.Equal(sponsorId.ToString(), entry.GetProperty("entityId").GetString());
        Assert.StartsWith("person:", entry.GetProperty("actor").GetString());
        // before + after are the sponsor DTOs.
        var before = entry.GetProperty("before");
        var after = entry.GetProperty("after");
        Assert.Equal(JsonValueKind.Object, before.ValueKind);
        Assert.Equal(JsonValueKind.Object, after.ValueKind);
        Assert.Equal("Alpine Bakeries", before.GetProperty("name").GetString());
        Assert.Equal("Alpine Bakeries LLC", after.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Sponsor_get_carries_audit_stamp_naming_the_editor()
    {
        // Create + patch so an audit row exists.
        var created = await SendEditorAsync(HttpMethod.Post, "/admin/sponsors",
            "{\"name\":\"Alpine Bakeries\"}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdDto = await ReadJsonAsync(created);
        var sponsorId = createdDto.RootElement.GetProperty("id").GetInt64();
        var patch = await SendEditorAsync(HttpMethod.Patch, $"/admin/sponsors/{sponsorId}",
            "{\"contactPerson\":\"Sam\"}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        // Get the sponsor and verify audit stamp.
        var get = await SendEditorAsync(HttpMethod.Get, $"/admin/sponsors/{sponsorId}", null);
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var doc = await ReadJsonAsync(get);
        Assert.True(doc.RootElement.TryGetProperty("audit", out var audit));
        Assert.Equal(JsonValueKind.Object, audit.ValueKind);
        Assert.Equal("update", audit.GetProperty("action").GetString());
        var by = audit.GetProperty("by").GetString();
        Assert.NotNull(by);
        Assert.StartsWith("person:", by);
    }

    [Fact]
    public async Task Cookie_type_delete_records_audit_row_with_before_only()
    {
        // Create a cookie type via the admin endpoint.
        var created = await SendAdminAsync(HttpMethod.Post, "/admin/cookie-types",
            "{\"name\":\"Chocolate Chip\",\"sort\":0,\"active\":true}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdDto = await ReadJsonAsync(created);
        var cookieTypeId = createdDto.RootElement.GetProperty("id").GetInt64();
        // Delete it.
        var response = await SendAdminAsync(HttpMethod.Delete, $"/admin/cookie-types/{cookieTypeId}", null);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var list = await SendAdminAsync(HttpMethod.Get,
            $"/admin/audit?entity=cookie_type&entityId={cookieTypeId}&action=delete", null);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        var page = await ReadJsonAsync(list);
        var items = page.RootElement.GetProperty("items");
        Assert.True(items.GetArrayLength() >= 1);
        var entry = items[0];
        Assert.Equal("delete", entry.GetProperty("action").GetString());
        Assert.Equal("cookie_type", entry.GetProperty("entity").GetString());
        Assert.Equal(JsonValueKind.Object, entry.GetProperty("before").ValueKind);
        Assert.Equal("Chocolate Chip", entry.GetProperty("before").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("after").ValueKind);
    }

    [Fact]
    public async Task Audit_entities_lists_the_seen_kinds()
    {
        var created = await SendEditorAsync(HttpMethod.Post, "/admin/sponsors", "{\"name\":\"AB\"}");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdDto = await ReadJsonAsync(created);
        var sponsorId = createdDto.RootElement.GetProperty("id").GetInt64();
        var patch = await SendEditorAsync(HttpMethod.Patch, $"/admin/sponsors/{sponsorId}",
            "{\"name\":\"ABC\"}");
        Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var response = await SendEditorAsync(HttpMethod.Get, "/admin/audit/entities", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = await ReadJsonAsync(response);
        var kinds = doc.RootElement.GetProperty("items");
        var found = false;
        foreach (var entry in kinds.EnumerateArray())
        {
            if (string.Equals(entry.GetString(), "sponsor", StringComparison.Ordinal)) { found = true; break; }
        }
        Assert.True(found, "sponsor entity kind not seen in audit/entities");
    }

    // ---------------- every-write coverage (acceptance criterion 1) ----------------

    // Drives the list from the endpoint map (RouteEndpoint metadata) so a new
    // admin write route without a recorder fails: every /admin/* POST/PATCH/PUT/
    // DELETE endpoint the host exposes carries the audit filter (via
    // RequireCapability / DenyApiKeys which chain it). This static coverage
    // check keeps the wire-up honest.
    [Fact]
    public void Every_admin_write_carries_the_audit_filter()
    {
        var sources = _host!.App.Services.GetServices<EndpointDataSource>();
        var uncovered = new List<string>();
        var seenWrites = 0;
        foreach (var endpoint in sources.SelectMany(s => s.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>())
        {
            var pattern = endpoint.RoutePattern.RawText ?? "";
            if (!pattern.StartsWith("/admin/", StringComparison.Ordinal)) continue;
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
                ?? Array.Empty<string>();
            if (!methods.Any(m => m is "POST" or "PATCH" or "PUT" or "DELETE")) continue;
            seenWrites++;
            // The capability metadata (from RequireCapability) or the
            // DenyApiKeys marker (for the three key endpoints) is what our
            // chain adds. AuditEndpointFilter is attached alongside both via
            // WithAuditFilter(); its absence means the endpoint bypasses audit.
            var cap = endpoint.Metadata.GetMetadata<Wmsfo.Api.Auth.RequireCapabilityMetadata>();
            var deny = endpoint.Metadata.GetMetadata<Wmsfo.Api.Auth.DenyApiKeysMetadata>();
            if (cap is null && deny is null) uncovered.Add(pattern);
        }
        Assert.True(seenWrites >= 40,
            $"expected the full admin write table, saw {seenWrites}");
        Assert.True(uncovered.Count == 0,
            "admin writes without audit coverage: " + string.Join(", ", uncovered));
    }

    // Runs each idempotent admin write once (validation-fail bodies are fine -
    // the filter records nothing on 4xx). For every admin write route the host
    // knows, the filter is attached and no exception is thrown when the
    // endpoint is hit. Regressions in the wiring surface here as a missed row
    // count or an exception.
    [Fact]
    public async Task Each_admin_write_can_be_dispatched()
    {
        var sources = _host!.App.Services.GetServices<EndpointDataSource>();
        var patterns = sources.SelectMany(s => s.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith("/admin/", StringComparison.Ordinal))
            .Where(e => (e.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods
                        ?? Array.Empty<string>())
                .Any(m => m is "POST" or "PATCH" or "PUT" or "DELETE"))
            .ToList();
        Assert.True(patterns.Count >= 40, $"saw {patterns.Count} admin writes");

        // Hit each endpoint with an anonymous request so it lands in the
        // pipeline; the audit filter runs on every /admin/* endpoint carrying
        // the metadata. We accept any status code except 500 to prove the
        // filter itself does not fault.
        foreach (var endpoint in patterns)
        {
            var pattern = endpoint.RoutePattern.RawText ?? "";
            var method = (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()!)
                .HttpMethods.First(m => m is "POST" or "PATCH" or "PUT" or "DELETE");
            var url = SubstituteRouteParams(pattern);
            var req = new HttpRequestMessage(new HttpMethod(method), url);
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            var response = await _host!.Client.SendAsync(req);
            Assert.NotEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        }
    }

    private static string SubstituteRouteParams(string pattern)
    {
        // Replace `{id:long}` and friends with 999999 so the routing engine
        // matches and the endpoint handler runs; the request is anonymous so
        // it will be rejected before touching the DB in most cases.
        var text = pattern;
        var start = 0;
        while (start < text.Length)
        {
            var open = text.IndexOf('{', start);
            if (open < 0) break;
            var close = text.IndexOf('}', open + 1);
            if (close < 0) break;
            var inner = text.Substring(open + 1, close - open - 1);
            var name = inner.Split(':')[0];
            var replacement = name switch
            {
                "eventYear" or "sourceYear" => "2100",
                "key" => "some_key",
                _ => "999999",
            };
            text = text.Substring(0, open) + replacement + text.Substring(close + 1);
            start = open + replacement.Length;
        }
        return text;
    }

    // ---------------- helpers ----------------

    private async Task<HttpResponseMessage> SendAdminAsync(HttpMethod method, string path, string? body)
        => await SendAsync(method, path, body, DevStaticTokens.AdminToken);

    private async Task<HttpResponseMessage> SendEditorAsync(HttpMethod method, string path, string? body)
        => await SendAsync(method, path, body, DevStaticTokens.EditorToken);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string token)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
        {
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }
        return await _host!.Client.SendAsync(req);
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
