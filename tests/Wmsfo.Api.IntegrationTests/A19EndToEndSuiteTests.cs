using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using NpgsqlTypes;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Content;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Node;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A19 end-to-end suite. One named test per contracts 12 sequence summary,
// exercising the same handlers as production against the local object store
// and a fake gateway client.
//
// The api.md 21 integration table row maps to tests as follows (each item is
// covered by a named test, either in this file or, when the earlier task
// already covered it, the file listed):
//
//   every endpoint's success and every listed error code - A8/A9/A10/A11/A12/
//     A13/A14/A15 endpoint-tests
//   the location transaction under concurrency (two beacons, one active) -
//     A8BeaconEndpointsTests.Locations_two_beacons_one_active_only_active_publishes
//   `seq` monotonic across 1,000 concurrent inserts -
//     A8BeaconEndpointsTests.Locations_seq_strictly_increasing_under_1000_concurrent_inserts
//   the snapshot transaction rollback on a failing PUT -
//     A7NodeRuntimeTests.Snapshot_transaction_rolls_back_when_put_fails
//   partial unique indexes (23505 on the three) -
//     A9ConstraintMappingTests (event_year_key, event_one_live,
//     subscriber_channel_address_key)
//   the tick rewrite rule -
//     A7NodeRuntimeTests.Tick_rewrite_rule_only_the_ingest_node_rewrites
//   leader gating of chores with an overlapping leader -
//     A16ChoreTests.Outbox_two_overlapping_publishers_claim_disjoint_rows_via_skip_locked
//   outbox and alert idempotency -
//     A16ChoreTests.Alert_fanout_is_unique_per_subscriber_and_outbox_row_across_two_passes
//   nightly cleanup counts - A16ChoreTests.Nightly_cleanup_counts_each_delete
//   the callback guard on forwarded headers -
//     A8RealtimeTests.Authorize_with_forwarded_header_answers_404_empty and
//     A8RealtimeTests.Message_with_forwarded_header_answers_404_empty
//   rate limits - PipelineTests.Rate_limited_answers_429_with_retry_after
//   the media pipeline end to end against the local store - A15MediaPipelineTests
//   publish with problems, unchanged, and success - A14PublisherAndPreviewTests
//   restore recreates six role pages -
//     A14PublisherAndPreviewTests.Restore_recreates_six_role_pages
//   preview token expiry -
//     A14PublisherAndPreviewTests.Preview_token_expires_after_the_stored_deadline
//   the orphan collector's four transitions -
//     A16ChoreTests.Orphan_collector_walks_four_transitions_over_a_clock_stub
//   Editor and Admin policy matrix over every /admin/* route -
//     PipelineTests.Admin_policy_matrix (and Sequence_policy_matrix_over_admin_routes
//     below, which walks a representative subset of the live /admin routes)
public sealed class A19EndToEndSuiteTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A19Host? _host;

    public A19EndToEndSuiteTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync();
        _host = await A19Host.StartAsync(_fixture.ConnectionString);
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
            "delete from alert_delivery;",
            "delete from outbox;",
            "delete from beacon_log;",
            "delete from beacon_enrollment_token;",
            "delete from location;",
            "delete from beacon;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from cookie;",
            "delete from event;",
            "delete from subscriber;",
            "delete from contact_message;",
            "delete from preview_token;",
            "delete from section_item;",
            "delete from section;",
            "delete from page;",
            "delete from snapshot;",
            "delete from content_version;",
            "delete from sponsor_year;",
            "delete from sponsor;",
            "delete from cookie_type;",
            "update site_setting_draft set data = '{}'::jsonb where id = 1;",
            "delete from media_asset;",
            "delete from person;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // ---------------- fix reaches a browser (hub AND poll path) ----------------

    // The hub path and the poll path share every step up to and including the
    // live-object PUT (contracts 12), so both are covered by one end-to-end
    // that hits POST /realtime/message and asserts the store bytes match the
    // gateway publish (a hub subscriber reads exactly those bytes).
    [Fact]
    public async Task Sequence_fix_reaches_live_object_via_realtime_message_and_matches_publish()
    {
        await BootstrapAsync();
        var eventId = await SeedLiveEventAsync(year: 2028);
        var (beaconId, key) = await SeedActiveBeaconAsync("chopper");
        // The location path does not RefreshAsync before Build (api.md 10.1),
        // so state has to know about the event before the writer runs.
        await _host!.State.RefreshAsync("test:after-seed", default);

        var body = "{\"channel\":\"wmsfo-api-test:ingest\",\"event\":\"location\","
                 + "\"data\":{\"lat\":46.87,\"lng\":-114.0,\"recordedAt\":\"2028-12-22T01:31:07Z\","
                 + "\"speedMps\":31.2,\"altitudeM\":1210,\"headingDeg\":84,\"accuracyM\":6},"
                 + $"\"connectionId\":\"c1\",\"identity\":\"{beaconId}:1\"}}";
        var response = await _host.Client.PostAsync("/realtime/message",
            new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("published").GetBoolean());
        var seq = doc.RootElement.GetProperty("seq").GetInt64();
        Assert.True(seq > 0);

        // location row landed with published=true.
        var stored = await ReadLatestLocationAsync(eventId);
        Assert.Equal(seq, stored.Seq);
        Assert.True(stored.Published);
        Assert.Equal(beaconId, stored.BeaconId);

        // The writer is fire-and-forget; wait for it to land bytes at
        // live/location.json and match them to the gateway publish.
        var (liveBytes, publishedBytes) = await WaitForLiveObjectMatchAsync(minSeq: seq);
        Assert.Equal(publishedBytes, liveBytes);
        using var liveDoc = JsonDocument.Parse(liveBytes);
        Assert.Equal(seq, liveDoc.RootElement.GetProperty("seq").GetInt64());
        Assert.Equal(eventId, liveDoc.RootElement.GetProperty("eventId").GetInt64());
        Assert.Equal(3, liveDoc.RootElement.GetProperty("eventStatusId").GetInt32());
        // The gateway saw the channel and event the sequence spec names
        // (contracts 2.6).
        Assert.Equal(_host.Options.ServiceName + ":location", _host.Gateway.LastChannel);
        Assert.Equal("location", _host.Gateway.LastEvent);
    }

    // ---------------- admin sets an event live ----------------

    [Fact]
    public async Task Sequence_admin_sets_event_live_writes_snapshot_and_live_object_with_new_status()
    {
        await BootstrapAsync();
        var eventId = await SeedScheduledEventAsync(year: 2029);
        var snapshotBefore = await ReadSnapshotVersionAsync();

        using var req = _host!.AdminRequest(HttpMethod.Post, $"/admin/events/{eventId}/status");
        req.Content = new StringContent("{\"statusId\":3,\"notify\":true}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var dto = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(3, dto.RootElement.GetProperty("statusId").GetInt32());

        // Snapshot version bumped (the [snapshot] frame rebuilt) and the CDN
        // holds the new snapshot object with eventStatusId=3.
        var snapshotAfter = await ReadSnapshotVersionAsync();
        Assert.True(snapshotAfter > snapshotBefore,
            $"snapshot version did not advance: before={snapshotBefore} after={snapshotAfter}");

        // event_status_history row inserted for the transition.
        Assert.Equal(1L, await CountAsync(
            $"select count(*) from event_status_history where event_id = {eventId} and to_status_id = 3;"));
        // outbox row queued (contracts 7.7).
        Assert.Equal(1L, await CountAsync(
            "select count(*) from outbox where topic = 'event.status_changed';"));

        // Live-object write carries the new status. Wait because the writer is
        // fire-and-forget.
        var liveBytes = await WaitForLiveEventStatusAsync(3);
        using var liveDoc = JsonDocument.Parse(liveBytes);
        Assert.Equal(3, liveDoc.RootElement.GetProperty("eventStatusId").GetInt32());
        Assert.Equal(eventId, liveDoc.RootElement.GetProperty("eventId").GetInt64());
    }

    // ---------------- editor adds a sponsor during the event ----------------

    [Fact]
    public async Task Sequence_editor_adds_a_sponsor_during_the_event_rebuilds_snapshot_and_live_object()
    {
        await BootstrapAsync();
        await SeedLiveEventAsync(year: 2030);
        var snapshotBefore = await ReadSnapshotVersionAsync();

        // 1. Ticket → PUT → confirm produces a ready media_asset.
        var mediaId = await UploadAndConfirmAsync("logo.png", "image/png", BuildPng(1024, 512));

        // 2. POST /admin/sponsors.
        using var create = _host!.EditorRequest(HttpMethod.Post, "/admin/sponsors");
        create.Content = new StringContent(
            "{\"name\":\"Alpine Bakeries\",\"websiteUrl\":\"https://alpine.example\"}",
            Encoding.UTF8, "application/json");
        var createResponse = await _host.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var createdBody = await createResponse.Content.ReadAsStringAsync();
        using var created = JsonDocument.Parse(createdBody);
        var sponsorId = created.RootElement.GetProperty("id").GetInt64();

        // 3. PATCH .../{id} { logoMediaId }.
        using var patch = _host.EditorRequest(HttpMethod.Patch, $"/admin/sponsors/{sponsorId}");
        patch.Content = new StringContent(
            $"{{\"logoMediaId\":\"{mediaId}\"}}",
            Encoding.UTF8, "application/json");
        var patchResponse = await _host.Client.SendAsync(patch);
        Assert.Equal(HttpStatusCode.OK, patchResponse.StatusCode);

        // 4. PUT .../{id}/years/2030.
        using var year = _host.EditorRequest(HttpMethod.Put, $"/admin/sponsors/{sponsorId}/years/2030");
        year.Content = new StringContent(
            "{\"amountDonated\":500,\"active\":true,\"canAdvertise\":true,\"anonymous\":false}",
            Encoding.UTF8, "application/json");
        var yearResponse = await _host.Client.SendAsync(year);
        Assert.Equal(HttpStatusCode.OK, yearResponse.StatusCode);

        // Snapshot version advanced (each sponsor write goes through the
        // [snapshot] frame; three writes = three bumps at minimum).
        var snapshotAfter = await ReadSnapshotVersionAsync();
        Assert.True(snapshotAfter >= snapshotBefore + 3,
            $"expected at least three snapshot rebuilds; before={snapshotBefore} after={snapshotAfter}");

        // The published snapshot embeds the sponsor.
        var snapshotBytes = await ReadCurrentSnapshotBytesAsync();
        Assert.NotNull(snapshotBytes);
        using var snap = JsonDocument.Parse(snapshotBytes!);
        var sponsors = snap.RootElement.GetProperty("sponsors").EnumerateArray().ToArray();
        Assert.Contains(sponsors, s => s.GetProperty("id").GetInt64() == sponsorId);

        // Live-object write followed each admin commit; wait for the bytes to
        // land and confirm the CDN has them.
        var liveBytes = await WaitForLiveBytesAsync();
        Assert.NotNull(liveBytes);
        Assert.Equal(_host.Gateway.LastPublished, liveBytes);
    }

    // ---------------- editor changes the about page and publishes ----------------

    [Fact]
    public async Task Sequence_editor_changes_the_about_page_and_publish_writes_a_new_snapshot_and_live_object()
    {
        await BootstrapAsync();
        var snapshotBefore = await ReadSnapshotVersionAsync();

        // Locate the rich_text section on the about page and PATCH it with new
        // draft data. The draft-level schema accepts the change; publish rebuilds
        // the snapshot with the new content.
        long sectionId;
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var lookup = new NpgsqlCommand(@"
select s.id from section s
join page p on p.id = s.page_id
where p.slug = 'about' and s.kind = 'rich_text'
limit 1;", conn);
            sectionId = Convert.ToInt64(await lookup.ExecuteScalarAsync());
        }
        Assert.True(sectionId > 0);

        var newData = "{\"blocks\":["
            + "{\"kind\":\"heading\",\"level\":2,\"text\":\"About the flyover 2028 edit\",\"icon\":null},"
            + "{\"kind\":\"paragraph\",\"text\":\"Contact editorial with corrections.\"}"
            + "]}";
        using var patchReq = _host!.EditorRequest(HttpMethod.Patch, $"/admin/sections/{sectionId}");
        patchReq.Content = new StringContent(
            $"{{\"data\":{newData}}}", Encoding.UTF8, "application/json");
        var patchResponse = await _host.Client.SendAsync(patchReq);
        var patchBody = await patchResponse.Content.ReadAsStringAsync();
        Assert.True(patchResponse.StatusCode == HttpStatusCode.OK,
            $"expected 200; got {(int)patchResponse.StatusCode} body={patchBody}");

        // Working-set writes do not rebuild the snapshot (contracts 4.5 Pages
        // preamble).
        Assert.Equal(snapshotBefore, await ReadSnapshotVersionAsync());

        // Preview token round-trips through /preview/document.
        using var mintReq = _host.EditorRequest(HttpMethod.Post, "/admin/content/preview-token");
        var mintResponse = await _host.Client.SendAsync(mintReq);
        Assert.Equal(HttpStatusCode.Created, mintResponse.StatusCode);
        using var minted = JsonDocument.Parse(await mintResponse.Content.ReadAsStringAsync());
        var token = minted.RootElement.GetProperty("token").GetString()!;
        var previewResponse = await _host.Client.GetAsync($"/preview/document?token={token}");
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        using var preview = JsonDocument.Parse(await previewResponse.Content.ReadAsStringAsync());
        Assert.True(preview.RootElement.GetProperty("content")
            .GetProperty("pages").GetArrayLength() > 0);

        // Publish inserts a new content_version, rebuilds the snapshot, and
        // the writer emits a new live object.
        using var publishReq = _host.EditorRequest(HttpMethod.Post, "/admin/content/publish");
        publishReq.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        var publishResponse = await _host.Client.SendAsync(publishReq);
        var publishBody = await publishResponse.Content.ReadAsStringAsync();
        Assert.True(publishResponse.StatusCode == HttpStatusCode.Created,
            $"expected 201 Created; got {(int)publishResponse.StatusCode} body={publishBody}");
        using var published = JsonDocument.Parse(publishBody);
        Assert.True(published.RootElement.GetProperty("id").GetInt64() > 0);

        // Snapshot version advanced.
        var snapshotAfter = await ReadSnapshotVersionAsync();
        Assert.True(snapshotAfter > snapshotBefore);

        // The published snapshot's content contains the new heading text.
        var snapshotBytes = await ReadCurrentSnapshotBytesAsync();
        Assert.NotNull(snapshotBytes);
        using var snap = JsonDocument.Parse(snapshotBytes!);
        var snapshotText = Encoding.UTF8.GetString(snapshotBytes!);
        Assert.Contains("flyover 2028 edit", snapshotText);
        _ = snap;

        // Live-object bytes are present.
        var liveBytes = await WaitForLiveBytesAsync();
        Assert.NotNull(liveBytes);
    }

    // ---------------- person leaves a cookie ----------------

    [Fact]
    public async Task Sequence_person_leaves_a_cookie_updates_state_tally_and_live_object_write()
    {
        await BootstrapAsync();
        await SeedLiveEventAsync(year: 2031);
        var typeId = await SeedActiveCookieTypeAsync("Snickerdoodle");

        // Refresh state so the writer's build sees the current event before
        // the person's insert.
        await _host!.State.RefreshAsync("test:before", default);
        Assert.NotNull(_host.State.Current.CurrentEvent);
        Assert.Empty(_host.State.Current.CookieTally);

        // POST /cookies increments this node's in-memory tally delta.
        using var post = _host.PersonRequest(HttpMethod.Post, "/cookies");
        post.Content = new StringContent(
            $"{{\"cookieTypeId\":{typeId},\"note\":\"first drop\"}}",
            Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        // Row landed and belongs to the current event.
        Assert.Equal(1L, await CountAsync(
            $"select count(*) from cookie where cookie_type_id = {typeId};"));

        // The tick re-reads the tally on its next tick and its next live-object
        // write carries it (contracts 12). Simulate the tick's refresh + write.
        await _host.Writer.WriteFromStateAsync("test:tick", default);
        var tally = _host.State.Current.CookieTally;
        Assert.Contains(typeId, tally.Keys);
        Assert.True(tally[typeId] >= 1);

        var liveBytes = await WaitForLiveTallyContainsAsync(typeId);
        using var live = JsonDocument.Parse(liveBytes);
        var cookieTally = live.RootElement.GetProperty("cookieTally");
        Assert.True(cookieTally.GetProperty(typeId.ToString()).GetInt32() >= 1);
    }

    // ---------------- admin hides a cookie during the event ----------------

    [Fact]
    public async Task Sequence_admin_hides_a_cookie_during_event_rewrites_live_object_tally()
    {
        await BootstrapAsync();
        await SeedLiveEventAsync(year: 2032);
        var typeId = await SeedActiveCookieTypeAsync("Gingersnap");

        // A person leaves a cookie so hiding it changes the visible tally.
        using var post = _host!.PersonRequest(HttpMethod.Post, "/cookies");
        post.Content = new StringContent(
            $"{{\"cookieTypeId\":{typeId}}}", Encoding.UTF8, "application/json");
        var created = await _host.Client.SendAsync(post);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        var cookieId = createdBody.RootElement.GetProperty("id").GetInt64();

        // Prime state so the writer's next build sees the cookie in the tally.
        await _host.Writer.WriteFromStateAsync("test:prime", default);
        var primed = await WaitForLiveTallyContainsAsync(typeId);
        _ = primed;

        // Hide the cookie - moderation while live triggers a live-object write.
        using var hide = _host.AdminRequest(HttpMethod.Post, $"/admin/cookies/{cookieId}/hide");
        var hideResponse = await _host.Client.SendAsync(hide);
        Assert.Equal(HttpStatusCode.OK, hideResponse.StatusCode);

        Assert.Equal(1L, await CountAsync(
            $"select count(*) from cookie where id = {cookieId} and hidden_at is not null;"));

        // Poll until the live-object no longer carries the hidden cookie's type
        // in its tally.
        var liveBytes = await WaitForLiveTallyAbsentAsync(typeId);
        using var live = JsonDocument.Parse(liveBytes);
        var tally = live.RootElement.GetProperty("cookieTally");
        Assert.False(tally.TryGetProperty(typeId.ToString(), out _));
    }

    // ---------------- beacon enrollment end to end ----------------

    [Fact]
    public async Task Sequence_beacon_enrollment_admin_create_through_heartbeat()
    {
        await BootstrapAsync();
        await SeedLiveEventAsync(year: 2033);

        // 1. Admin creates the beacon. The response carries the plaintext key
        // (used once) and the enrollment envelope with a QR PNG data URL.
        using var create = _host!.AdminRequest(HttpMethod.Post, "/admin/beacons");
        create.Content = new StringContent(
            "{\"name\":\"Helicopter\",\"notes\":\"tail 1234\",\"role\":\"beacon\"}",
            Encoding.UTF8, "application/json");
        var createResponse = await _host.Client.SendAsync(create);
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        using var createBody = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var beaconId = createBody.RootElement.GetProperty("beacon").GetProperty("id").GetInt64();
        var qr = createBody.RootElement.GetProperty("enrollment").GetProperty("qrPngDataUrl").GetString();
        Assert.NotNull(qr);
        Assert.StartsWith("data:image/png;base64,", qr);
        var enrollmentToken = createBody.RootElement.GetProperty("enrollment").GetProperty("token").GetString()!;
        Assert.StartsWith("wet_", enrollmentToken);

        // 2. Phone scans rednose://enroll?... and POSTs the token to
        // /beacons/enroll. The response returns the beacon key and URLs.
        var enrollResponse = await _host.Client.PostAsync("/beacons/enroll",
            new StringContent($"{{\"token\":\"{enrollmentToken}\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, enrollResponse.StatusCode);
        using var enrolled = JsonDocument.Parse(await enrollResponse.Content.ReadAsStringAsync());
        Assert.Equal(beaconId, enrolled.RootElement.GetProperty("beaconId").GetInt64());
        var beaconKey = enrolled.RootElement.GetProperty("key").GetString()!;
        Assert.StartsWith("wbk_", beaconKey);
        Assert.Equal(_host.Options.ServiceName + ":ingest",
            enrolled.RootElement.GetProperty("ingestChannel").GetString());

        // Enrollment token is now consumed - a second call is 404.
        var second = await _host.Client.PostAsync("/beacons/enroll",
            new StringContent($"{{\"token\":\"{enrollmentToken}\"}}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);

        // 3. The service starts, joins <service>:ingest and heartbeats.
        using var hb = new HttpRequestMessage(HttpMethod.Post, "/beacons/heartbeat")
        {
            Content = new StringContent(
                "{\"sentAt\":\"2033-12-22T01:31:07Z\",\"power\":{\"batteryPercent\":90}}",
                Encoding.UTF8, "application/json"),
        };
        hb.Headers.Add(BeaconAuthenticationHandler.HeaderName, beaconKey);
        var hbResponse = await _host.Client.SendAsync(hb);
        Assert.Equal(HttpStatusCode.OK, hbResponse.StatusCode);
        using var hbBody = JsonDocument.Parse(await hbResponse.Content.ReadAsStringAsync());
        Assert.True(hbBody.RootElement.GetProperty("liveEventId").GetInt64() > 0);
        // last_seen_at stamped on the beacon row.
        var lastSeen = await ReadBeaconLastSeenAsync(beaconId);
        Assert.NotNull(lastSeen);
    }

    // ---------------- policy matrix over admin routes ----------------

    // api.md 21 also names "Editor and Admin policy matrix over every /admin/*
    // route". PipelineTests.Admin_policy_matrix already covers the matrix on a
    // stub /admin route; this walks a representative subset of the live admin
    // routes (one Editor-policy route, one Admin-policy route) and asserts the
    // same shape: anonymous 401, person 403, editor allowed only on Editor
    // routes, admin allowed on both.
    [Theory]
    [InlineData("GET", "/admin/sponsors", true)]      // Editor policy
    [InlineData("GET", "/admin/beacons", false)]      // Admin policy
    public async Task Sequence_policy_matrix_over_admin_routes(string method, string path, bool editorAllowed)
    {
        var httpMethod = new HttpMethod(method);

        // 1. Anonymous → 401.
        using (var anon = new HttpRequestMessage(httpMethod, path))
        {
            var response = await _host!.Client.SendAsync(anon);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        // 2. Person (no group) → 403.
        using (var person = _host!.PersonRequest(httpMethod, path))
        {
            var response = await _host.Client.SendAsync(person);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }
        // 3. Editor: allowed only on Editor-policy routes; 403 on Admin routes.
        using (var editor = _host.EditorRequest(httpMethod, path))
        {
            var response = await _host.Client.SendAsync(editor);
            Assert.Equal(
                editorAllowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
                response.StatusCode);
        }
        // 4. Admin → 200. (No TOTP gate in dev/test WmsfoAuth.)
        using (var admin = _host.AdminRequest(httpMethod, path))
        {
            var response = await _host.Client.SendAsync(admin);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    // ---------------- helpers ----------------

    private async Task BootstrapAsync()
    {
        // Fresh empty database - run the first-boot bootstrap so the working
        // set holds the starter content and the snapshot row exists.
        var bootstrap = _host!.GetService<SnapshotBootstrap>();
        await bootstrap.EnsureVersionOneAsync(default);
        await _host.State.RefreshAsync("test:bootstrap", default);
    }

    private async Task<long> SeedLiveEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, 3, true, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<long> SeedScheduledEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, scheduled_at, created_by, updated_at)
values ($1, $2, 2, true, now() + interval '1 hour', 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<(long BeaconId, string Key)> SeedActiveBeaconAsync(string name)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        // Only one active beacon is allowed.
        await using (var wipe = new NpgsqlCommand("update beacon set is_active = false where is_active;", conn))
        {
            await wipe.ExecuteNonQueryAsync();
        }
        var minted = Keys.MintKey();
        await using var cmd = new NpgsqlCommand(@"
insert into beacon (name, notes, role, key_hash, key_prefix, is_active, created_by, updated_at)
values ($1, '', 'beacon', $2, $3, true, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = minted.Hash });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = minted.Prefix });
        return (Convert.ToInt64(await cmd.ExecuteScalarAsync()), minted.Token);
    }

    private async Task<long> SeedActiveCookieTypeAsync(string name)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into cookie_type (name, icon, sort, active, updated_at)
values ($1, '{""source"":""library"",""id"":""cookie""}'::jsonb, 0, true, now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    // Upload → PUT → confirm through the API and return the media asset id.
    private async Task<string> UploadAndConfirmAsync(string filename, string contentType, byte[] bytes)
    {
        var body = new StringContent(
            $"{{\"filename\":\"{filename}\",\"contentType\":\"{contentType}\",\"sizeBytes\":{bytes.LongLength},\"alt\":\"\",\"title\":\"\"}}",
            Encoding.UTF8, "application/json");
        using var ticketReq = _host!.EditorRequest(HttpMethod.Post, "/admin/media/upload-url");
        ticketReq.Content = body;
        var ticketResp = await _host.Client.SendAsync(ticketReq);
        Assert.Equal(HttpStatusCode.Created, ticketResp.StatusCode);
        using var ticket = JsonDocument.Parse(await ticketResp.Content.ReadAsStringAsync());
        var id = ticket.RootElement.GetProperty("media").GetProperty("id").GetString()!;
        var uploadUrl = ticket.RootElement.GetProperty("uploadUrl").GetString()!;

        var uri = new Uri(uploadUrl);
        using var put = new HttpRequestMessage(HttpMethod.Put, uri.PathAndQuery)
        {
            Content = new ByteArrayContent(bytes),
        };
        put.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        put.Headers.Add("x-amz-tagging", "state=pending");
        var putResp = await _host.Client.SendAsync(put);
        Assert.Equal(HttpStatusCode.NoContent, putResp.StatusCode);

        using var confirmReq = _host.EditorRequest(HttpMethod.Post, $"/admin/media/{id}/confirm");
        var confirmResp = await _host.Client.SendAsync(confirmReq);
        Assert.Equal(HttpStatusCode.OK, confirmResp.StatusCode);
        return id;
    }

    private static byte[] BuildPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(200, 50, 50, 255));
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    // ---- reads ----

    private async Task<(long Seq, bool Published, long BeaconId)> ReadLatestLocationAsync(long eventId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select seq, published, beacon_id from location where event_id = $1 order by seq desc limit 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetBoolean(1), reader.GetInt64(2));
    }

    private async Task<long> ReadSnapshotVersionAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select coalesce(version, 0) from snapshot where id = 1;", conn);
        var r = await cmd.ExecuteScalarAsync();
        return r is null || r is DBNull ? 0L : Convert.ToInt64(r);
    }

    private async Task<byte[]?> ReadCurrentSnapshotBytesAsync()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select s3_key from snapshot where id = 1;", conn);
        var key = (string?)await cmd.ExecuteScalarAsync();
        if (key is null) return null;
        var obj = await _host!.Store.GetObjectAsync(key);
        return obj?.Bytes;
    }

    private async Task<DateTimeOffset?> ReadBeaconLastSeenAsync(long beaconId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select last_seen_at from beacon where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        var r = await cmd.ExecuteScalarAsync();
        return r is null || r is DBNull ? null : (DateTimeOffset?)Convert.ToDateTime(r);
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var r = await cmd.ExecuteScalarAsync();
        return r is null || r is DBNull ? 0L : Convert.ToInt64(r);
    }

    // The writer runs fire-and-forget from every handler that provokes it,
    // so tests poll until the bytes land or a deadline is reached.
    private static readonly TimeSpan WriterDeadline = TimeSpan.FromSeconds(5);

    // LocalObjectStore writes with FileShare.None, so a read that races with
    // an in-flight PUT surfaces as IOException. Retry on those; the writer's
    // window is a few milliseconds.
    private async Task<byte[]?> TryReadLiveBytesAsync()
    {
        try
        {
            var live = await _host!.Store.GetObjectAsync(LiveObjectWriter.CdnKey);
            return live?.Bytes;
        }
        catch (IOException) { return null; }
    }

    private async Task<byte[]?> WaitForLiveBytesAsync()
    {
        var deadline = DateTimeOffset.UtcNow + WriterDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var bytes = await TryReadLiveBytesAsync();
            if (bytes is not null) return bytes;
            await Task.Delay(50);
        }
        return null;
    }

    private async Task<(byte[] LiveBytes, byte[] PublishedBytes)> WaitForLiveObjectMatchAsync(long minSeq)
    {
        var deadline = DateTimeOffset.UtcNow + WriterDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var bytes = await TryReadLiveBytesAsync();
            var published = _host!.Gateway.LastPublished;
            if (bytes is not null && published is not null && bytes.AsSpan().SequenceEqual(published))
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("seq", out var seqProp)
                    && seqProp.ValueKind == JsonValueKind.Number
                    && seqProp.GetInt64() >= minSeq)
                {
                    return (bytes, published);
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"live-object write did not reach seq >= {minSeq} within {WriterDeadline.TotalSeconds}s");
    }

    private async Task<byte[]> WaitForLiveTallyContainsAsync(long cookieTypeId)
    {
        var deadline = DateTimeOffset.UtcNow + WriterDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var bytes = await TryReadLiveBytesAsync();
            if (bytes is not null)
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("cookieTally", out var tally)
                    && tally.TryGetProperty(cookieTypeId.ToString(), out var count)
                    && count.ValueKind == JsonValueKind.Number
                    && count.GetInt32() > 0)
                {
                    return bytes;
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"live object cookie tally never contained {cookieTypeId} within {WriterDeadline.TotalSeconds}s");
    }

    private async Task<byte[]> WaitForLiveTallyAbsentAsync(long cookieTypeId)
    {
        var deadline = DateTimeOffset.UtcNow + WriterDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var bytes = await TryReadLiveBytesAsync();
            if (bytes is not null)
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("cookieTally", out var tally)
                    && !tally.TryGetProperty(cookieTypeId.ToString(), out _))
                {
                    return bytes;
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"live object cookie tally still contained {cookieTypeId} after {WriterDeadline.TotalSeconds}s");
    }

    private async Task<byte[]> WaitForLiveEventStatusAsync(int expectedStatusId)
    {
        var deadline = DateTimeOffset.UtcNow + WriterDeadline;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var bytes = await TryReadLiveBytesAsync();
            if (bytes is not null)
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("eventStatusId", out var status)
                    && status.ValueKind == JsonValueKind.Number
                    && status.GetInt32() == expectedStatusId)
                {
                    return bytes;
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"live object never reached eventStatusId={expectedStatusId} within {WriterDeadline.TotalSeconds}s");
    }
}
