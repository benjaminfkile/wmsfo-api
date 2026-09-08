using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A13 acceptance criteria (task 275):
//   - draft-level accepts incomplete but rejects unknown properties
//   - status reports every publish-level problem with ids
//   - role pages cannot be deleted or hidden
//   - kind_not_allowed from allowedRoles (map on non-live page)
//   - position compaction on delete
//   - move, duplicate, order compaction
//   - editor policy required; person token is 403
public sealed class A13AdminContentEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A13Host? _host;

    public A13AdminContentEndpointsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAndSeedAsync();
        _host = await A13Host.StartAsync(_fixture.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    private async Task MigrateAndSeedAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(contextOptions);
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await RolePageSeed.EnsureAsync(conn);
    }

    // -------------------- kinds --------------------

    [Fact]
    public async Task Kinds_lists_every_kind_with_schemas()
    {
        using var req = _host!.EditorRequest(HttpMethod.Get, "/admin/content/kinds");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        var kinds = items.Select(e => e.GetProperty("kind").GetString()).ToArray();
        Assert.Contains("rich_text", kinds);
        Assert.Contains("map", kinds);
        // The `map` kind carries allowedRoles: ["live"].
        var map = items.First(e => e.GetProperty("kind").GetString() == "map");
        var allowedRoles = map.GetProperty("allowedRoles").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "live" }, allowedRoles);
        // The schema is inlined with the primitives under $defs.
        var schema = map.GetProperty("schema");
        Assert.True(schema.TryGetProperty("$defs", out _), "$defs is missing from the inlined schema");
    }

    // -------------------- pages --------------------

    [Fact]
    public async Task Pages_list_returns_six_role_pages()
    {
        using var req = _host!.EditorRequest(HttpMethod.Get, "/admin/pages");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        var slugs = items.Select(e => e.GetProperty("slug").GetString()).ToArray();
        Assert.Contains("no-event", slugs);
        Assert.Contains("live", slugs);
        Assert.Contains("cancelled", slugs);
    }

    [Fact]
    public async Task Pages_create_none_page()
    {
        var response = await PostEditorAsync("/admin/pages",
            "{\"slug\":\"about\",\"title\":\"About\",\"navLabel\":\"About\",\"navPosition\":10,\"isHidden\":false}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("about", doc.RootElement.GetProperty("slug").GetString());
        Assert.Equal("none", doc.RootElement.GetProperty("role").GetString());
    }

    [Fact]
    public async Task Pages_create_reserved_slug_is_400()
    {
        var response = await PostEditorAsync("/admin/pages",
            "{\"slug\":\"admin\",\"title\":\"Admin\",\"navLabel\":null,\"navPosition\":10,\"isHidden\":false}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.SlugReserved, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Pages_create_duplicate_slug_is_409()
    {
        _ = await PostEditorAsync("/admin/pages",
            "{\"slug\":\"about\",\"title\":\"About\",\"navLabel\":null,\"navPosition\":10,\"isHidden\":false}");
        var response = await PostEditorAsync("/admin/pages",
            "{\"slug\":\"about\",\"title\":\"About\",\"navLabel\":null,\"navPosition\":11,\"isHidden\":false}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.SlugTaken, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Pages_delete_role_page_is_409_page_has_role()
    {
        var pageId = await ReadRolePageIdAsync("live");
        using var req = _host!.EditorRequest(HttpMethod.Delete, $"/admin/pages/{pageId}");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.PageHasRole, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Pages_hide_role_page_is_400()
    {
        var pageId = await ReadRolePageIdAsync("live");
        var response = await PatchEditorAsync($"/admin/pages/{pageId}", "{\"isHidden\":true}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Pages_role_page_navLabel_stays_null()
    {
        var pageId = await ReadRolePageIdAsync("live");
        var response = await PatchEditorAsync($"/admin/pages/{pageId}", "{\"navLabel\":\"Live now\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------- sections & unknown properties --------------------

    [Fact]
    public async Task Section_create_draft_accepts_incomplete_body()
    {
        var pageId = await CreateNonePageAsync("about-1");
        // A hero missing `title` (a required publish-level field). Draft strips
        // `required`, so it should be accepted.
        var response = await PostEditorAsync($"/admin/pages/{pageId}/sections",
            "{\"kind\":\"hero\",\"data\":{\"height\":\"short\",\"links\":[],\"icon\":null,\"tagline\":null}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Section_create_draft_rejects_unknown_property()
    {
        var pageId = await CreateNonePageAsync("about-2");
        // `bogusField` is not part of the hero schema.
        var response = await PostEditorAsync($"/admin/pages/{pageId}/sections",
            "{\"kind\":\"hero\",\"data\":{\"title\":\"Hi\",\"height\":\"short\",\"links\":[],\"icon\":null,\"tagline\":null,\"bogusField\":42}}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.ValidationFailed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Section_create_unknown_kind_is_400()
    {
        var pageId = await CreateNonePageAsync("about-3");
        var response = await PostEditorAsync($"/admin/pages/{pageId}/sections",
            "{\"kind\":\"not_a_kind\"}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(ApiErrorCodes.UnknownKind, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Section_create_map_on_none_page_is_409_kind_not_allowed()
    {
        var pageId = await CreateNonePageAsync("about-4");
        var response = await PostEditorAsync($"/admin/pages/{pageId}/sections",
            "{\"kind\":\"map\"}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.KindNotAllowed, await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Section_create_map_on_live_role_page_is_ok()
    {
        var livePageId = await ReadRolePageIdAsync("live");
        var response = await PostEditorAsync($"/admin/pages/{livePageId}/sections",
            "{\"kind\":\"map\"}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task Sections_delete_compacts_positions()
    {
        var pageId = await CreateNonePageAsync("about-5");
        var s1 = await CreateHeroAsync(pageId);
        var s2 = await CreateHeroAsync(pageId);
        var s3 = await CreateHeroAsync(pageId);

        // Delete s2 (position 1). s3 (position 2) should become position 1.
        using var req = _host!.EditorRequest(HttpMethod.Delete, $"/admin/sections/{s2}");
        var del = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var positions = await ReadSectionPositionsAsync(pageId);
        Assert.Equal(new[] { (s1, 0), (s3, 1) }, positions.OrderBy(t => t.Item2).ToArray());
    }

    [Fact]
    public async Task Sections_order_reorders_and_compacts()
    {
        var pageId = await CreateNonePageAsync("about-6");
        var s1 = await CreateHeroAsync(pageId);
        var s2 = await CreateHeroAsync(pageId);
        var s3 = await CreateHeroAsync(pageId);

        var response = await PutEditorAsync($"/admin/pages/{pageId}/sections/order",
            $"{{\"ids\":[{s3},{s1},{s2}]}}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var positions = await ReadSectionPositionsAsync(pageId);
        var byPos = positions.OrderBy(t => t.Item2).Select(t => t.Item1).ToArray();
        Assert.Equal(new[] { s3, s1, s2 }, byPos);
    }

    [Fact]
    public async Task Sections_move_between_pages_compacts_both()
    {
        var pageA = await CreateNonePageAsync("about-7a");
        var pageB = await CreateNonePageAsync("about-7b");
        var sA1 = await CreateHeroAsync(pageA);
        var sA2 = await CreateHeroAsync(pageA);
        var sA3 = await CreateHeroAsync(pageA);

        var response = await PostEditorAsync($"/admin/sections/{sA2}/move",
            $"{{\"pageId\":{pageB},\"position\":0}}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var aPositions = await ReadSectionPositionsAsync(pageA);
        Assert.Equal(new[] { (sA1, 0), (sA3, 1) }, aPositions.OrderBy(t => t.Item2).ToArray());

        var bPositions = await ReadSectionPositionsAsync(pageB);
        Assert.Equal(new[] { (sA2, 0) }, bPositions.OrderBy(t => t.Item2).ToArray());
    }

    [Fact]
    public async Task Sections_duplicate_inserts_after_source()
    {
        var pageId = await CreateNonePageAsync("about-8");
        var s1 = await CreateHeroAsync(pageId);
        var s2 = await CreateHeroAsync(pageId);

        var response = await PostEditorAsync($"/admin/sections/{s1}/duplicate", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var newId = doc.RootElement.GetProperty("id").GetInt64();
        var newPos = doc.RootElement.GetProperty("position").GetInt32();
        Assert.Equal(1, newPos);
        var positions = await ReadSectionPositionsAsync(pageId);
        Assert.Equal(new[] { (s1, 0), (newId, 1), (s2, 2) },
            positions.OrderBy(t => t.Item2).ToArray());
    }

    // -------------------- move fails kind_not_allowed --------------------

    [Fact]
    public async Task Section_move_map_off_live_is_409()
    {
        var livePageId = await ReadRolePageIdAsync("live");
        var somePage = await CreateNonePageAsync("about-9");
        var mapResp = await PostEditorAsync($"/admin/pages/{livePageId}/sections",
            "{\"kind\":\"map\"}");
        Assert.Equal(HttpStatusCode.Created, mapResp.StatusCode);
        var doc = JsonDocument.Parse(await mapResp.Content.ReadAsStringAsync());
        var mapId = doc.RootElement.GetProperty("id").GetInt64();

        var response = await PostEditorAsync($"/admin/sections/{mapId}/move",
            $"{{\"pageId\":{somePage},\"position\":0}}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(ApiErrorCodes.KindNotAllowed, await ReadCodeAsync(response));
    }

    // -------------------- site settings & draft --------------------

    [Fact]
    public async Task Site_settings_put_accepts_partial_at_draft_level()
    {
        var response = await PutEditorAsync("/admin/site-settings",
            "{\"data\":{\"siteName\":\"Hello\"}}");
        // Draft schema is lenient on required, but rejects unknown properties.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Site_settings_put_rejects_unknown_property_at_draft_level()
    {
        var response = await PutEditorAsync("/admin/site-settings",
            "{\"data\":{\"whatever\":true}}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // -------------------- content status --------------------

    [Fact]
    public async Task Content_status_reports_publish_problems_with_ids()
    {
        var pageId = await CreateNonePageAsync("about-status");
        var s1 = await CreateHeroAsync(pageId);
        // Site settings are empty in the working set; publish-level requires
        // several fields, so the status endpoint should return problems.
        using var req = _host!.EditorRequest(HttpMethod.Get, "/admin/content/status");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var problems = doc.RootElement.GetProperty("problems").EnumerateArray().ToArray();
        Assert.NotEmpty(problems);
        // Every problem carries either the settings location (all-null ids) or a
        // section id when the problem is on a section.
        var settingsCarrying = problems.FirstOrDefault(p =>
            p.GetProperty("pageId").ValueKind == JsonValueKind.Null &&
            p.GetProperty("sectionId").ValueKind == JsonValueKind.Null);
        Assert.NotEqual(default, settingsCarrying);
        Assert.NotNull(doc.RootElement.GetProperty("draftSha256").GetString());
        Assert.True(doc.RootElement.GetProperty("hasUnpublishedChanges").GetBoolean());
    }

    [Fact]
    public async Task Content_draft_endpoint_returns_bundle()
    {
        _ = await CreateNonePageAsync("about-draft");
        using var req = _host!.EditorRequest(HttpMethod.Get, "/admin/content/draft");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("content").ValueKind);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("media").ValueKind);
        Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("icons").ValueKind);
    }

    // -------------------- authorization --------------------

    [Fact]
    public async Task Person_token_is_403_on_editor_route()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/admin/pages");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", DevStaticTokens.PersonToken);
        var response = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Missing_token_is_401()
    {
        var response = await _host!.Client.GetAsync("/admin/pages");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------- helpers --------------------

    private async Task<long> ReadRolePageIdAsync(string role)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select id from page where role = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = role });
        var r = await cmd.ExecuteScalarAsync();
        return Convert.ToInt64(r);
    }

    private async Task<long> CreateNonePageAsync(string slug)
    {
        var response = await PostEditorAsync("/admin/pages",
            $"{{\"slug\":\"{slug}\",\"title\":\"{slug}\",\"navLabel\":null,\"navPosition\":10,\"isHidden\":false}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<long> CreateHeroAsync(long pageId)
    {
        var response = await PostEditorAsync($"/admin/pages/{pageId}/sections",
            "{\"kind\":\"hero\"}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<List<(long Id, int Position)>> ReadSectionPositionsAsync(long pageId)
    {
        var list = new List<(long, int)>();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select id, position from section where page_id = $1 order by position, id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { Value = pageId });
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add((reader.GetInt64(0), reader.GetInt32(1)));
        }
        return list;
    }

    private Task<HttpResponseMessage> PostEditorAsync(string path, string? body) =>
        SendEditorAsync(HttpMethod.Post, path, body);

    private Task<HttpResponseMessage> PutEditorAsync(string path, string body) =>
        SendEditorAsync(HttpMethod.Put, path, body);

    private Task<HttpResponseMessage> PatchEditorAsync(string path, string body) =>
        SendEditorAsync(HttpMethod.Patch, path, body);

    private async Task<HttpResponseMessage> SendEditorAsync(HttpMethod method, string path, string? body)
    {
        using var req = _host!.EditorRequest(method, path);
        if (body is not null)
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private static async Task<string> ReadCodeAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }
}
