using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Node;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Realtime;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A37 acceptance: the decision table, derived speed, counters, the message
// path outcomes, CSV headers, PATCH minIntervalMs validation, clear recording
// (204, per-beacon form, 409 event_live, 404, audit row, impact preview counts
// and the live block).
public sealed class A37LocationIngestTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A37Host? _host;

    public A37LocationIngestTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        var contextOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString).UseSnakeCaseNamingConvention().Options;
        await using (var db = new WmsfoDbContext(contextOptions))
            await db.Database.MigrateAsync();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from beacon_log;",
            "delete from location;",
            "delete from beacon_enrollment_token;",
            "delete from beacon;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from cookie;",
            "delete from event;",
            "delete from audit_log where entity = 'event';",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        // Restore the seed settings to their defaults (a previous test may
        // have overridden them).
        await using (var upd = new NpgsqlCommand(@"
insert into app_setting (key, value, updated_by) values
  ('location_min_interval_ms', '250', 'seed'),
  ('location_min_distance_m',  '0',   'seed')
on conflict (key) do update set value = excluded.value, updated_by = 'seed', updated_at = now();", conn))
        {
            await upd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A37Host.StartAsync(_fixture.ConnectionString);
        await _host.RefreshStateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    // ------------------------------------------------------------------
    // The decision table (contracts 7.2)
    // ------------------------------------------------------------------

    [Fact]
    public async Task No_previous_fix_is_stored_with_outcome_stored()
    {
        await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("no-prev", isActive: true, minIntervalMs: 0);
        var (status, doc) = await PostFixAsync(key, 46.87, -114.0);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("stored", doc.RootElement.GetProperty("outcome").GetString());
        Assert.Equal(1, await CountLocationsAsync(beaconId));
    }

    [Fact]
    public async Task Exact_repeat_at_min_distance_zero_is_carried()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("exact", isActive: true, minIntervalMs: 0);
        var (s1, d1) = await PostFixAsync(key, 46.87, -114.0);
        var (s2, d2) = await PostFixAsync(key, 46.87, -114.0);
        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.Created, s2);
        Assert.Equal("stored", d1.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("carried", d2.RootElement.GetProperty("outcome").GetString());
        // next_seq advances on both; the carried fix's seq comes back too.
        Assert.Equal(d1.RootElement.GetProperty("seq").GetInt64() + 1,
                     d2.RootElement.GetProperty("seq").GetInt64());
        // one location row (from the stored fix); a carried fix writes no row.
        Assert.Equal(1, await CountLocationsAsync(beaconId));
        // beacon.fixes_carried is 1
        var (stored, carried, _) = await ReadBeaconCountersAsync(beaconId);
        Assert.Equal(1L, stored);
        Assert.Equal(1L, carried);
        // event.next_seq advanced twice
        Assert.Equal(3, await ReadNextSeqAsync(evtId));
    }

    [Fact]
    public async Task One_metre_move_is_carried_at_min_distance_five()
    {
        await SetSettingAsync("location_min_distance_m", "5");
        await _host!.RefreshStateAsync();

        await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("one-metre", isActive: true, minIntervalMs: 0);
        // First fix always stored.
        _ = await PostFixAsync(key, 46.870000, -114.0);
        // Move about 1.1 metres: dlat = 0.00001 (about 1.11 metres)
        var (_, doc) = await PostFixAsync(key, 46.870010, -114.0);
        Assert.Equal("carried", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task One_metre_move_is_stored_at_min_distance_zero()
    {
        // Distance 0 means "carry ONLY exact repeats" - a different lat/lng
        // is always stored.
        await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("one-metre-zero", isActive: true, minIntervalMs: 0);
        _ = await PostFixAsync(key, 46.870000, -114.0);
        var (_, doc) = await PostFixAsync(key, 46.870010, -114.0);
        Assert.Equal("stored", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Move_inside_min_distance_is_carried_regardless_of_elapsed_time()
    {
        // A39: without the max-gap rule, a fix that moved less than min_distance
        // from the beacon's last stored fix is always carried, whatever the
        // time between the two.
        await SetSettingAsync("location_min_distance_m", "50");
        await _host!.RefreshStateAsync();

        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("gap", isActive: true, minIntervalMs: 0);
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            // Seed a previous stored fix long enough ago that the old max-gap
            // rule would have stored the follow-up. Without the rule, min
            // distance alone decides.
            await using (var cmd = new NpgsqlCommand(@"
insert into location (event_id, beacon_id, seq, recorded_at, received_at, lat, lng, published)
values ($1, $2, 1, now(), now() - interval '31 seconds', 46.87, -114.0, true);", conn))
            {
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = evtId });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                await cmd.ExecuteNonQueryAsync();
            }
            await using (var upd = new NpgsqlCommand("update event set next_seq = 2 where id = $1;", conn))
            {
                upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = evtId });
                await upd.ExecuteNonQueryAsync();
            }
        }
        // ~1.1 m move, inside min_distance 50.
        var (_, doc) = await PostFixAsync(key, 46.870010, -114.0);
        Assert.Equal("carried", doc.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Override_zero_accepts_back_to_back_fixes_while_setting_is_250()
    {
        await SeedEventAsync();
        // beacon.min_interval_ms = 0 (override); setting stays at the default 250.
        var (beaconId, key) = await SeedBeaconAsync("override0", isActive: true, minIntervalMs: 0);
        // Two back-to-back fixes should both be stored (or one carried; neither dropped).
        var (s1, d1) = await PostFixAsync(key, 46.87, -114.0);
        var (s2, d2) = await PostFixAsync(key, 46.88, -114.0);
        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal(HttpStatusCode.Created, s2);
        Assert.NotEqual("dropped", d1.RootElement.GetProperty("outcome").GetString());
        Assert.NotEqual("dropped", d2.RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Fix_inside_the_interval_is_dropped()
    {
        await SeedEventAsync();
        // No override; the setting default is 250 ms.
        var (beaconId, key) = await SeedBeaconAsync("drop", isActive: true, minIntervalMs: null);
        var (s1, d1) = await PostFixAsync(key, 46.87, -114.0);
        Assert.Equal("stored", d1.RootElement.GetProperty("outcome").GetString());
        // Immediately fire a second fix; the interval hasn't elapsed.
        var (s2, d2) = await PostFixAsync(key, 46.88, -114.0);
        Assert.Equal(HttpStatusCode.Created, s2);
        Assert.Equal("dropped", d2.RootElement.GetProperty("outcome").GetString());
        // Nothing was written for the drop (still 1 row).
        Assert.Equal(1, await CountLocationsAsync(beaconId));
    }

    [Fact]
    public async Task Rate_limit_flush_lands_in_fixes_rate_limited()
    {
        await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("flush", isActive: true, minIntervalMs: null);
        _ = await PostFixAsync(key, 46.87, -114.0);
        // Force a flush window elapsed by rewinding the state.
        var limiter = _host!.RateLimiter;
        // The private LastFlushAt starts at MinValue so the first drop flushes.
        var (_, dropDoc) = await PostFixAsync(key, 46.87, -114.0);
        Assert.Equal("dropped", dropDoc.RootElement.GetProperty("outcome").GetString());
        // The flush is async but runs inside HandleAsync before the response;
        // read the column now.
        var (_, _, rateLimited) = await ReadBeaconCountersAsync(beaconId);
        Assert.Equal(1L, rateLimited);
    }

    // ------------------------------------------------------------------
    // Derived speed (contracts 1.2, 7.2)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Derived_speed_within_one_percent_on_two_point_fixture()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("speed-derived", isActive: true, minIntervalMs: 0);
        // Two fixes 60 s apart, ~50 metres apart: 50/60 ≈ 0.833 m/s.
        var t0 = "2026-12-22T01:31:00Z";
        var t1 = "2026-12-22T01:32:00Z";
        _ = await PostFixAsync(key, 46.870000, -114.0, recordedAt: t0);
        var (_, d2) = await PostFixAsync(key, 46.870449, -114.0, recordedAt: t1);
        Assert.Equal("stored", d2.RootElement.GetProperty("outcome").GetString());
        // Verify the persisted row: speed_source and value.
        var (speedMps, speedSource) = await ReadLatestSpeedAsync(beaconId);
        Assert.Equal("derived", speedSource);
        // Distance is ~49.95 m over 60 s → ~0.833 m/s. Allow 1 percent slack.
        var expected = 49.95 / 60.0;
        Assert.InRange(speedMps ?? -1, expected * 0.99, expected * 1.01);
    }

    [Fact]
    public async Task Beacon_speed_is_kept_when_present()
    {
        await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("speed-beacon", isActive: true, minIntervalMs: 0);
        _ = await PostFixAsync(key, 46.87, -114.0, recordedAt: "2026-12-22T01:31:07Z", speed: 31.2);
        var (speedMps, speedSource) = await ReadLatestSpeedAsync(beaconId);
        Assert.Equal("beacon", speedSource);
        Assert.Equal(31.2, speedMps);
    }

    // ------------------------------------------------------------------
    // Carried fix produces a live object (contracts 1.8, 7.2)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Carried_fix_advances_next_seq_and_publishes_live_object()
    {
        await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("carry-pub", isActive: true, minIntervalMs: 0);
        var (_, d1) = await PostFixAsync(key, 46.87, -114.0);
        var (_, d2) = await PostFixAsync(key, 46.87, -114.0);   // carried
        Assert.Equal("carried", d2.RootElement.GetProperty("outcome").GetString());
        var seqCarried = d2.RootElement.GetProperty("seq").GetInt64();
        Assert.Equal(d1.RootElement.GetProperty("seq").GetInt64() + 1, seqCarried);
        // The writer was invoked; wait briefly for the async publish.
        for (var i = 0; i < 50 && _host!.Store.PutCount("live/location.json") == 0; i++)
            await Task.Delay(10);
        Assert.True(_host!.Store.PutCount("live/location.json") >= 1);
        // The last written live object should carry the carried fix's seq.
        var writer = _host.Writer;
        Assert.NotNull(writer.LastWrittenObject);
        Assert.Equal(seqCarried, writer.LastWrittenObject!.Seq);
    }

    // ------------------------------------------------------------------
    // Message path outcomes (contracts 2.5)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Message_path_gives_the_same_outcomes()
    {
        await SeedEventAsync();
        var (beaconId, _) = await SeedBeaconAsync("msg", isActive: true, minIntervalMs: 0);
        // First: stored
        var response = await PostRealtimeMessageAsync(beaconId, keyVersion: 1, "46.87", "-114.0");
        Assert.Equal(HttpStatusCode.OK, response.status);
        Assert.Equal("stored", response.doc.RootElement.GetProperty("outcome").GetString());
        // Second (exact repeat): carried
        var response2 = await PostRealtimeMessageAsync(beaconId, keyVersion: 1, "46.87", "-114.0");
        Assert.Equal(HttpStatusCode.OK, response2.status);
        Assert.Equal("carried", response2.doc.RootElement.GetProperty("outcome").GetString());
    }

    // ------------------------------------------------------------------
    // CSV header carries speedSource (contracts 4.5, 5)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Csv_export_header_carries_speedSource_column()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("csv-a37", isActive: true, minIntervalMs: 0);
        _ = await PostFixAsync(key, 46.87, -114.0);

        using var req = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{evtId}/locations");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "seq,beaconId,published,recordedAt,receivedAt,lat,lng,speedMps,speedSource,altitudeM,headingDeg,accuracyM",
            lines[0]);
    }

    // ------------------------------------------------------------------
    // PATCH minIntervalMs validation (contracts 4.5)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Patch_minIntervalMs_number_updates_and_null_clears()
    {
        var (beaconId, _) = await SeedBeaconAsync("patch", isActive: false, minIntervalMs: null);

        using (var req = _host!.AdminRequest(HttpMethod.Patch, $"/admin/beacons/{beaconId}"))
        {
            req.Content = new StringContent("{\"minIntervalMs\": 500}", Encoding.UTF8, "application/json");
            var resp = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(500, doc.RootElement.GetProperty("minIntervalMs").GetInt32());
        }

        using (var req = _host!.AdminRequest(HttpMethod.Patch, $"/admin/beacons/{beaconId}"))
        {
            req.Content = new StringContent("{\"minIntervalMs\": null}", Encoding.UTF8, "application/json");
            var resp = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("minIntervalMs").ValueKind);
        }
    }

    [Fact]
    public async Task Patch_minIntervalMs_out_of_range_is_400()
    {
        var (beaconId, _) = await SeedBeaconAsync("patch-oor", isActive: false, minIntervalMs: null);
        using var req = _host!.AdminRequest(HttpMethod.Patch, $"/admin/beacons/{beaconId}");
        req.Content = new StringContent("{\"minIntervalMs\": 60001}", Encoding.UTF8, "application/json");
        var resp = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // ------------------------------------------------------------------
    // Clear recording (contracts 4.5)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Delete_locations_204_and_rows_gone()
    {
        var evtId = await SeedEventAsync(statusId: 1);  // not live so we can delete
        var (b1, k1) = await SeedBeaconAsync("del-b1", isActive: false, minIntervalMs: 0);
        var (b2, k2) = await SeedBeaconAsync("del-b2", isActive: false, minIntervalMs: 0);
        await SetEventStatusAsync(evtId, 3);
        _ = await PostFixAsync(k1, 46.87, -114.0);
        _ = await PostFixAsync(k2, 47.0, -114.0);
        await SetEventStatusAsync(evtId, 4);          // ended

        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/events/{evtId}/locations");
        var resp = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(0, await CountLocationsAsync(b1));
        Assert.Equal(0, await CountLocationsAsync(b2));
        // audit row landed
        var auditAction = await ReadLatestAuditActionAsync(evtId);
        Assert.Equal("locations_cleared", auditAction);
    }

    [Fact]
    public async Task Delete_locations_per_beacon_leaves_the_other_beacon_alone()
    {
        var evtId = await SeedEventAsync(statusId: 1);
        var (b1, k1) = await SeedBeaconAsync("d1-b1", isActive: false, minIntervalMs: 0);
        var (b2, k2) = await SeedBeaconAsync("d1-b2", isActive: false, minIntervalMs: 0);
        await SetEventStatusAsync(evtId, 3);
        _ = await PostFixAsync(k1, 46.87, -114.0);
        _ = await PostFixAsync(k2, 47.0, -114.0);
        await SetEventStatusAsync(evtId, 4);

        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/events/{evtId}/locations?beaconId={b1}");
        var resp = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        Assert.Equal(0, await CountLocationsAsync(b1));
        Assert.Equal(1, await CountLocationsAsync(b2));
    }

    [Fact]
    public async Task Delete_locations_while_live_is_409_event_live()
    {
        var evtId = await SeedEventAsync(statusId: 1);
        var (b1, k1) = await SeedBeaconAsync("live-b", isActive: true, minIntervalMs: 0);
        await SetEventStatusAsync(evtId, 3);
        _ = await PostFixAsync(k1, 46.87, -114.0);

        using var req = _host!.AdminRequest(HttpMethod.Delete, $"/admin/events/{evtId}/locations");
        var resp = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        var doc = await ReadJsonAsync(resp);
        Assert.Equal("event_live", doc.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Delete_locations_unknown_event_is_404()
    {
        using var req = _host!.AdminRequest(HttpMethod.Delete, "/admin/events/999999/locations");
        var resp = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Impact_preview_per_beacon_counts_and_live_block()
    {
        var evtId = await SeedEventAsync(statusId: 1);
        var (b1, k1) = await SeedBeaconAsync("imp-b1", isActive: false, minIntervalMs: 0);
        var (b2, k2) = await SeedBeaconAsync("imp-b2", isActive: false, minIntervalMs: 0);
        await SetEventStatusAsync(evtId, 3);
        _ = await PostFixAsync(k1, 46.87, -114.0);
        _ = await PostFixAsync(k1, 46.88, -114.0);
        _ = await PostFixAsync(k2, 47.0, -114.0);

        // Impact preview while live shows counts AND the blocked banner.
        using (var req = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{evtId}/locations/impact"))
        {
            var resp = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            Assert.Equal("This event is live. End it first.", doc.RootElement.GetProperty("blocked").GetString());
            var groups = doc.RootElement.GetProperty("deletes");
            Assert.Equal(2, groups.GetArrayLength());
        }
        // End the event and the block goes away.
        await SetEventStatusAsync(evtId, 4);
        using (var req = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{evtId}/locations/impact"))
        {
            var resp = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var doc = await ReadJsonAsync(resp);
            var blocked = doc.RootElement.GetProperty("blocked");
            Assert.Equal(JsonValueKind.Null, blocked.ValueKind);
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task<long> SeedEventAsync(int statusId = 3)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var wipe = new NpgsqlCommand("delete from event where status_id = 3;", conn))
            await wipe.ExecuteNonQueryAsync();
        await using (var clr = new NpgsqlCommand("update event set is_current = false where is_current;", conn))
            await clr.ExecuteNonQueryAsync();
        var year = 3000 + Random.Shared.Next(1, 90);
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, $3, true, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)statusId });
        var id = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        // Bump snapshot version so the next RefreshAsync re-reads state.
        await using (var bump = new NpgsqlCommand(
            "update snapshot set version = version + 1 where id = 1;", conn))
        {
            await bump.ExecuteNonQueryAsync();
        }
        await _host!.RefreshStateAsync();
        return id;
    }

    private async Task SetEventStatusAsync(long eventId, int statusId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        if (statusId == 3)
        {
            await using var wipe = new NpgsqlCommand("update event set status_id = 4, updated_at = now() where status_id = 3 and id <> $1;", conn);
            wipe.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            await wipe.ExecuteNonQueryAsync();
        }
        await using (var upd = new NpgsqlCommand("update event set status_id = $1, updated_at = now() where id = $2;", conn))
        {
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)statusId });
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            await upd.ExecuteNonQueryAsync();
        }
    }

    private async Task<(long id, string key)> SeedBeaconAsync(string name, bool isActive, int? minIntervalMs)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        if (isActive)
        {
            await using var wipe = new NpgsqlCommand("update beacon set is_active = false where is_active;", conn);
            await wipe.ExecuteNonQueryAsync();
        }
        var key = Keys.MintKey();
        await using var cmd = new NpgsqlCommand(@"
insert into beacon (name, key_hash, key_prefix, is_active, min_interval_ms, created_by, updated_at)
values ($1, $2, $3, $4, $5, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = key.Hash });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key.Prefix });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = isActive });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = (object?)minIntervalMs ?? DBNull.Value });
        var id = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        return (id, key.Token);
    }

    private async Task<(HttpStatusCode status, JsonDocument doc)> PostFixAsync(
        string key, double lat, double lng, string? recordedAt = null, double? speed = null)
    {
        var when = recordedAt ?? "2026-12-22T01:31:07Z";
        var speedField = speed is null ? "" : $",\"speedMps\":{speed.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        using var req = new HttpRequestMessage(HttpMethod.Post, "/locations")
        {
            Content = new StringContent(
                $"{{\"lat\":{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"lng\":{lng.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"recordedAt\":\"{when}\"{speedField}}}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var resp = await _host!.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text));
    }

    private async Task<(HttpStatusCode status, JsonDocument doc)> PostRealtimeMessageAsync(long beaconId, int keyVersion, string lat, string lng)
    {
        var body = new
        {
            channel = "wmsfo-api-test:ingest",
            @event = "location",
            data = new
            {
                lat = double.Parse(lat, System.Globalization.CultureInfo.InvariantCulture),
                lng = double.Parse(lng, System.Globalization.CultureInfo.InvariantCulture),
                recordedAt = "2026-12-22T01:31:07Z",
            },
            connectionId = "abc",
            identity = $"{beaconId}:{keyVersion}",
        };
        var json = JsonSerializer.Serialize(body);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/realtime/message")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var resp = await _host!.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text));
    }

    private async Task SetSettingAsync(string key, string valueJson)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var cmd = new NpgsqlCommand(@"
insert into app_setting (key, value, updated_by) values ($1, $2::jsonb, 'seed')
on conflict (key) do update set value = excluded.value, updated_by = 'seed', updated_at = now();", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = valueJson });
            await cmd.ExecuteNonQueryAsync();
        }
        // A settings change is a [snapshot] write in production; bump the
        // snapshot row so the next RefreshAsync re-reads settings (api.md 9).
        await using (var bump = new NpgsqlCommand(
            "update snapshot set version = version + 1 where id = 1;", conn))
        {
            await bump.ExecuteNonQueryAsync();
        }
    }

    private async Task<int> CountLocationsAsync(long beaconId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*)::int from location where beacon_id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<long> ReadNextSeqAsync(long eventId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select next_seq from event where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<(long stored, long carried, long rateLimited)> ReadBeaconCountersAsync(long beaconId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select fixes_stored, fixes_carried, fixes_rate_limited from beacon where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private async Task<(double? speed, string? source)> ReadLatestSpeedAsync(long beaconId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select speed_mps, speed_source from location where beacon_id = $1 order by seq desc limit 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        await using var reader = await cmd.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (
            reader.IsDBNull(0) ? null : reader.GetDouble(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private async Task<string?> ReadLatestAuditActionAsync(long eventId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "select action from audit_log where entity = 'event' and entity_id like $1 || '%' order by id desc limit 1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = eventId.ToString() });
        var r = await cmd.ExecuteScalarAsync();
        return r is null || r is DBNull ? null : (string)r;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return JsonDocument.Parse(text);
    }
}
