using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.IntegrationTests;

// A33 integration coverage: QR and place endpoints, the scan beacon, and the
// snapshot's qrCodes block (contracts 4.5a).
public sealed class A33QrEndpointsTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A19Host? _host;

    public A33QrEndpointsTests(PostgresFixture fixture) => _fixture = fixture;

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
            "delete from qr_scan;",
            "delete from qr_attachment;",
            "delete from qr_code;",
            "delete from place;",
            "delete from audit_log;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from location;",
            "delete from event;",
            "delete from sponsor_year;",
            "delete from sponsor;",
            "delete from cookie_type;",
            "delete from subscriber;",
            "delete from person;",
            "delete from section_item;",
            "delete from section;",
            "delete from page;",
            "delete from site_setting_draft;",
            "delete from content_version;",
            "delete from api_key;",
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

    // Tag minting: sequential batch numbers, sequential tags starting at qr-001,
    // one batch per POST.
    [Fact]
    public async Task Mint_produces_sequential_batch_and_tags()
    {
        var r1 = await SendAsync(HttpMethod.Post, "/admin/qr-codes", "{\"count\":3}", DevStaticTokens.CanvasserToken);
        Assert.Equal(HttpStatusCode.Created, r1.StatusCode);
        var d1 = await ReadJsonAsync(r1);
        var items1 = d1.RootElement.GetProperty("items");
        Assert.Equal(3, items1.GetArrayLength());
        Assert.Equal("qr-001", items1[0].GetProperty("tag").GetString());
        Assert.Equal("qr-002", items1[1].GetProperty("tag").GetString());
        Assert.Equal("qr-003", items1[2].GetProperty("tag").GetString());
        Assert.Equal(1, items1[0].GetProperty("batchNo").GetInt32());

        var r2 = await SendAsync(HttpMethod.Post, "/admin/qr-codes", "{\"count\":2}", DevStaticTokens.CanvasserToken);
        Assert.Equal(HttpStatusCode.Created, r2.StatusCode);
        var d2 = await ReadJsonAsync(r2);
        var items2 = d2.RootElement.GetProperty("items");
        Assert.Equal("qr-004", items2[0].GetProperty("tag").GetString());
        Assert.Equal("qr-005", items2[1].GetProperty("tag").GetString());
        Assert.Equal(2, items2[0].GetProperty("batchNo").GetInt32());
    }

    // Places tree in order: parents before children, siblings by name.
    [Fact]
    public async Task Places_tree_orders_parents_before_children_and_siblings_by_name()
    {
        var mall = await CreatePlaceAsync(null, "Southgate Mall");
        var b = await CreatePlaceAsync(mall, "Beta Wing");
        var a = await CreatePlaceAsync(mall, "Alpha Wing");
        var alpha1 = await CreatePlaceAsync(a, "Store 1");
        var alpha2 = await CreatePlaceAsync(a, "Store 2");

        var listResp = await SendAsync(HttpMethod.Get, "/admin/places", null, DevStaticTokens.CanvasserToken);
        Assert.Equal(HttpStatusCode.OK, listResp.StatusCode);
        var doc = await ReadJsonAsync(listResp);
        var items = doc.RootElement.GetProperty("items");
        var names = items.EnumerateArray().Select(e => e.GetProperty("name").GetString()).ToList();
        var idx = new Dictionary<string, int>();
        for (var i = 0; i < names.Count; i++) idx[names[i]!] = i;
        Assert.True(idx["Southgate Mall"] < idx["Alpha Wing"]);
        Assert.True(idx["Alpha Wing"] < idx["Beta Wing"]);
        Assert.True(idx["Alpha Wing"] < idx["Store 1"]);
        Assert.True(idx["Store 1"] < idx["Store 2"]);
    }

    // Cycle rule: a place cannot move under itself or its descendant.
    [Fact]
    public async Task Place_patch_refuses_move_under_descendant_with_place_cycle()
    {
        var parent = await CreatePlaceAsync(null, "Parent");
        var child = await CreatePlaceAsync(parent, "Child");
        // Move `parent` under `child` -> 400 place_cycle.
        var req = _host!.CanvasserRequest(HttpMethod.Patch, $"/admin/places/{parent}");
        req.Content = new StringContent($"{{\"parentId\":{child}}}", Encoding.UTF8, "application/json");
        var resp = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("place_cycle", body);
    }

    // Sibling name conflict: 409 place_name_taken; case-insensitive.
    [Fact]
    public async Task Sibling_name_conflict_answers_409_place_name_taken()
    {
        var parent = await CreatePlaceAsync(null, "Parent");
        await CreatePlaceAsync(parent, "One");
        var req = _host!.CanvasserRequest(HttpMethod.Post, "/admin/places");
        req.Content = new StringContent($"{{\"parentId\":{parent},\"name\":\"ONE\",\"description\":\"\"}}", Encoding.UTF8, "application/json");
        var resp = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Contains("place_name_taken", await resp.Content.ReadAsStringAsync());
    }

    // Delete refusals: 409 place_has_children and 409 place_has_codes.
    [Fact]
    public async Task Delete_place_refuses_when_children_or_codes_present()
    {
        var parent = await CreatePlaceAsync(null, "Parent");
        var child = await CreatePlaceAsync(parent, "Child");
        // Case 1: children present.
        var r1 = await SendAsync(HttpMethod.Delete, $"/admin/places/{parent}", null, DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.Conflict, r1.StatusCode);
        Assert.Contains("place_has_children", await r1.Content.ReadAsStringAsync());
        // Delete the child first, then attach a code to a fresh place.
        await SendAsync(HttpMethod.Delete, $"/admin/places/{child}", null, DevStaticTokens.AdminToken);
        var solo = await CreatePlaceAsync(null, "Solo");
        var mint = await SendAsync(HttpMethod.Post, "/admin/qr-codes", "{\"count\":1}", DevStaticTokens.CanvasserToken);
        var mdoc = await ReadJsonAsync(mint);
        var codeId = mdoc.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64();
        await SendAsync(HttpMethod.Post, $"/admin/qr-codes/{codeId}/attach",
            $"{{\"placeId\":{solo}}}", DevStaticTokens.CanvasserToken);
        var r2 = await SendAsync(HttpMethod.Delete, $"/admin/places/{solo}", null, DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.Conflict, r2.StatusCode);
        Assert.Contains("place_has_codes", await r2.Content.ReadAsStringAsync());
    }

    // Attach folds the last hour's unattached scans onto the new attachment.
    [Fact]
    public async Task Attach_folds_last_hour_unattached_scans()
    {
        var place = await CreatePlaceAsync(null, "PlaceForFold");
        var mint = await SendAsync(HttpMethod.Post, "/admin/qr-codes", "{\"count\":1}", DevStaticTokens.CanvasserToken);
        var mdoc = await ReadJsonAsync(mint);
        var codeId = mdoc.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64();
        var tag = mdoc.RootElement.GetProperty("items")[0].GetProperty("tag").GetString()!;
        // Public scan (no attachment yet).
        var scanReq = new HttpRequestMessage(HttpMethod.Post, $"/qr-codes/{tag}/scans");
        scanReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var scanResp = await _host!.Client.SendAsync(scanReq);
        Assert.Equal(HttpStatusCode.NoContent, scanResp.StatusCode);
        // Attach the code to a place.
        var attachResp = await SendAsync(HttpMethod.Post, $"/admin/qr-codes/{codeId}/attach",
            $"{{\"placeId\":{place}}}", DevStaticTokens.CanvasserToken);
        Assert.Equal(HttpStatusCode.OK, attachResp.StatusCode);
        // Verify: the earlier scan was folded onto the new attachment.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var check = new NpgsqlCommand(@"
select count(*) from qr_scan s
join qr_attachment a on a.id = s.attachment_id
where s.qr_code_id = $1 and a.place_id = $2;", conn);
        check.Parameters.Add(new NpgsqlParameter { Value = codeId });
        check.Parameters.Add(new NpgsqlParameter { Value = place });
        var count = Convert.ToInt64(await check.ExecuteScalarAsync());
        Assert.True(count >= 1, "expected the earlier scan to fold onto the new attachment");
    }

    // The public scan beacon: always 204, unknown tags are dropped silently.
    [Fact]
    public async Task Scan_beacon_is_204_for_unknown_tag()
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/qr-codes/qr-nope/scans");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var resp = await _host!.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
    }

    // Repeat detection: a second scan from the same IP/UA within 10 seconds is flagged.
    [Fact]
    public async Task Repeat_scan_within_ten_seconds_is_flagged()
    {
        var mint = await SendAsync(HttpMethod.Post, "/admin/qr-codes", "{\"count\":1}", DevStaticTokens.CanvasserToken);
        var mdoc = await ReadJsonAsync(mint);
        var codeId = mdoc.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64();
        var tag = mdoc.RootElement.GetProperty("items")[0].GetProperty("tag").GetString()!;
        // Two rapid scans with the same UA.
        for (var i = 0; i < 2; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"/qr-codes/{tag}/scans");
            req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            req.Headers.UserAgent.ParseAdd("Mozilla/5.0 IntegrationTest");
            var resp = await _host!.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var check = new NpgsqlCommand(
            "select count(*) filter (where is_repeat = true), count(*) from qr_scan where qr_code_id = $1;", conn);
        check.Parameters.Add(new NpgsqlParameter { Value = codeId });
        await using var reader = await check.ExecuteReaderAsync();
        await reader.ReadAsync();
        var repeats = reader.GetInt64(0);
        var total = reader.GetInt64(1);
        Assert.Equal(2, total);
        Assert.True(repeats >= 1, "expected the second scan within 10s to be flagged as repeat");
    }

    // The map aggregation: counts people under each pin, plus unattached total.
    [Fact]
    public async Task Places_map_counts_people_under_pins()
    {
        var pinned = await CreatePlaceAsync(null, "Pinned");
        // Set a pin on the place.
        await SendAsync(HttpMethod.Put, $"/admin/places/{pinned}/location",
            "{\"lat\":46.9,\"lng\":-114.0,\"accuracyM\":50,\"source\":\"phone\"}",
            DevStaticTokens.CanvasserToken);
        var mint = await SendAsync(HttpMethod.Post, "/admin/qr-codes", "{\"count\":1}", DevStaticTokens.CanvasserToken);
        var mdoc = await ReadJsonAsync(mint);
        var codeId = mdoc.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64();
        var tag = mdoc.RootElement.GetProperty("items")[0].GetProperty("tag").GetString()!;
        await SendAsync(HttpMethod.Post, $"/admin/qr-codes/{codeId}/attach",
            $"{{\"placeId\":{pinned}}}", DevStaticTokens.CanvasserToken);
        // A public scan lands.
        var req = new HttpRequestMessage(HttpMethod.Post, $"/qr-codes/{tag}/scans");
        req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        req.Headers.UserAgent.ParseAdd("Mozilla/5.0 MapTest");
        await _host!.Client.SendAsync(req);

        var mapResp = await SendAsync(HttpMethod.Get, "/admin/places/map", null, DevStaticTokens.CanvasserToken);
        Assert.Equal(HttpStatusCode.OK, mapResp.StatusCode);
        var mapDoc = await ReadJsonAsync(mapResp);
        var pins = mapDoc.RootElement.GetProperty("items");
        Assert.True(pins.GetArrayLength() >= 1);
        var pin = pins[0];
        Assert.Equal(pinned, pin.GetProperty("placeId").GetInt64());
        Assert.True(pin.GetProperty("people").GetInt32() >= 1);
    }

    // Snapshot resolves a code through its place's ancestor: mint one, attach
    // to a child of a place whose forward or page routes to a sponsors page,
    // then verify the snapshot's qrCodes block resolves the tag to that slug.
    [Fact]
    public async Task Snapshot_resolves_code_through_ancestor_place()
    {
        // Create a page with slug "sponsors" and role "none". A role-page seed
        // exists already; we insert an additional none-page here so the code's
        // opens can name it.
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        long pageId;
        await using (var insert = new NpgsqlCommand(@"
insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by)
values ('sponsors', 'Sponsors', 'Sponsors', 20, false, 'none', 'test', 'test') returning id;", conn))
        {
            pageId = (long)(await insert.ExecuteScalarAsync() ?? 0L);
        }

        var mall = await CreatePlaceAsync(null, "AncestorMall");
        // Set the ancestor's opens to the sponsors page.
        await SendAsync(HttpMethod.Patch, $"/admin/places/{mall}",
            $"{{\"opensPageId\":{pageId}}}", DevStaticTokens.CanvasserToken);
        var wing = await CreatePlaceAsync(mall, "AncestorWing");
        // Mint a code and attach it to the wing.
        var mint = await SendAsync(HttpMethod.Post, "/admin/qr-codes", "{\"count\":1}", DevStaticTokens.CanvasserToken);
        var mdoc = await ReadJsonAsync(mint);
        var codeId = mdoc.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64();
        var tag = mdoc.RootElement.GetProperty("items")[0].GetProperty("tag").GetString()!;
        await SendAsync(HttpMethod.Post, $"/admin/qr-codes/{codeId}/attach",
            $"{{\"placeId\":{wing}}}", DevStaticTokens.CanvasserToken);

        // Rebuild the snapshot to see the qrCodes block.
        var rebuildResp = await SendAsync(HttpMethod.Post, "/admin/snapshot/rebuild", null, DevStaticTokens.AdminToken);
        Assert.Equal(HttpStatusCode.OK, rebuildResp.StatusCode);
        var rebuildDoc = await ReadJsonAsync(rebuildResp);
        var url = rebuildDoc.RootElement.GetProperty("url").GetString()!;
        // Read the snapshot bytes from the local object store.
        var s3Key = rebuildDoc.RootElement.GetProperty("s3Key").GetString()!;
        var snapshotBytes = await _host!.Store.GetObjectAsync(s3Key, default);
        Assert.NotNull(snapshotBytes);
        using var snap = JsonDocument.Parse(snapshotBytes!.Bytes);
        var qrCodes = snap.RootElement.GetProperty("qrCodes");
        Assert.True(qrCodes.TryGetProperty(tag, out var entry));
        Assert.Equal("sponsors", entry.GetProperty("pageSlug").GetString());
    }

    // Audit rows are recorded on every write: create, update, attach, detach,
    // location, delete.
    [Fact]
    public async Task Every_qr_write_records_an_audit_row()
    {
        var mint = await SendAsync(HttpMethod.Post, "/admin/qr-codes", "{\"count\":1}", DevStaticTokens.CanvasserToken);
        var mdoc = await ReadJsonAsync(mint);
        var codeId = mdoc.RootElement.GetProperty("items")[0].GetProperty("id").GetInt64();
        var place = await CreatePlaceAsync(null, "AuditPlace");
        var attachResp = await SendAsync(HttpMethod.Post, $"/admin/qr-codes/{codeId}/attach",
            $"{{\"placeId\":{place}}}", DevStaticTokens.CanvasserToken);
        Assert.Equal(HttpStatusCode.OK, attachResp.StatusCode);
        var patchResp = await SendAsync(HttpMethod.Patch, $"/admin/qr-codes/{codeId}",
            "{\"note\":\"n1\"}", DevStaticTokens.CanvasserToken);
        Assert.Equal(HttpStatusCode.OK, patchResp.StatusCode);
        var detachResp = await SendAsync(HttpMethod.Post, $"/admin/qr-codes/{codeId}/detach", null, DevStaticTokens.CanvasserToken);
        Assert.Equal(HttpStatusCode.OK, detachResp.StatusCode);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        var actions = new HashSet<string>();
        await using (var cmd = new NpgsqlCommand(
            "select action from audit_log where entity = 'qr_code' and entity_id = $1;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = codeId.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) actions.Add(reader.GetString(0));
        }
        Assert.Contains("create", actions);
        Assert.Contains("update", actions);
        Assert.Contains("attach", actions);
        Assert.Contains("detach", actions);
    }

    // ------------ helpers ------------

    private async Task<long> CreatePlaceAsync(long? parentId, string name)
    {
        var body = parentId is null
            ? $"{{\"name\":\"{name}\",\"description\":\"\"}}"
            : $"{{\"parentId\":{parentId.Value},\"name\":\"{name}\",\"description\":\"\"}}";
        var resp = await SendAsync(HttpMethod.Post, "/admin/places", body, DevStaticTokens.CanvasserToken);
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var doc = await ReadJsonAsync(resp);
        return doc.RootElement.GetProperty("id").GetInt64();
    }

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
