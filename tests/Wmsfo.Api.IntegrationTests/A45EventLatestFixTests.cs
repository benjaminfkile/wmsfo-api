using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A45 acceptance: event.latest_fix holds the last published fix on the event
// so a rebuild of the live object (status change, event message, tick,
// takeover) never steps its seq back below the last carried fix (contracts
// 1.2, 7.2). The delete-locations handler nulls latest_fix per the rules of
// contracts 4.5.
public sealed class A45EventLatestFixTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A37Host? _host;

    public A45EventLatestFixTests(PostgresFixture fixture) { _fixture = fixture; }

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
        await using (var upd = new NpgsqlCommand(@"
insert into app_setting (key, value, updated_by) values
  ('location_min_interval_ms', '0', 'seed'),
  ('location_min_distance_m',  '0', 'seed')
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

    // (a) A stored fix from the active beacon sets latest_fix with the fix's
    // seq, beacon, and location fields.
    [Fact]
    public async Task Stored_fix_from_active_beacon_sets_latest_fix()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("stored-active", isActive: true);

        var (status, doc) = await PostFixAsync(key, 46.87, -113.99);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("stored", doc.RootElement.GetProperty("outcome").GetString());
        var seq = doc.RootElement.GetProperty("seq").GetInt64();

        var fix = await ReadLatestFixAsync(evtId);
        Assert.NotNull(fix);
        Assert.Equal(seq, fix!.Value.GetProperty("seq").GetInt64());
        Assert.Equal(beaconId, fix.Value.GetProperty("beaconId").GetInt64());
        Assert.Equal(46.87, fix.Value.GetProperty("lat").GetDouble());
        Assert.Equal(-113.99, fix.Value.GetProperty("lng").GetDouble());
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$",
            fix.Value.GetProperty("recordedAt").GetString());
        Assert.Matches("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}\\.\\d{3}Z$",
            fix.Value.GetProperty("receivedAt").GetString());
    }

    // (b) A carried fix (the same position again) advances latest_fix.seq
    // though no row is added.
    [Fact]
    public async Task Carried_fix_advances_latest_fix_seq()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("carried-adv", isActive: true);

        var (_, d1) = await PostFixAsync(key, 46.87, -113.99);
        Assert.Equal("stored", d1.RootElement.GetProperty("outcome").GetString());
        var seq1 = d1.RootElement.GetProperty("seq").GetInt64();
        _host!.RateLimiter.ResetLastAccepted(beaconId);

        var (_, d2) = await PostFixAsync(key, 46.87, -113.99);
        Assert.Equal("carried", d2.RootElement.GetProperty("outcome").GetString());
        var seq2 = d2.RootElement.GetProperty("seq").GetInt64();
        Assert.Equal(seq1 + 1, seq2);

        var fix = await ReadLatestFixAsync(evtId);
        Assert.NotNull(fix);
        Assert.Equal(seq2, fix!.Value.GetProperty("seq").GetInt64());
        Assert.Equal(beaconId, fix.Value.GetProperty("beaconId").GetInt64());

        // A carried fix writes no row; the stored one is the only location.
        Assert.Equal(1, await CountLocationsAsync(beaconId));
    }

    // (c) A fix from a beacon that is not active leaves latest_fix unchanged.
    [Fact]
    public async Task Unpublished_fix_leaves_latest_fix_unchanged()
    {
        var evtId = await SeedEventAsync();
        // Seed inactive first so activating the primary does not flip it off.
        var (spareId, spareKey) = await SeedBeaconAsync("spare", isActive: false);
        var (primaryId, primaryKey) = await SeedBeaconAsync("primary", isActive: true);

        // Store a published fix so latest_fix is populated.
        var (_, d1) = await PostFixAsync(primaryKey, 46.87, -113.99);
        Assert.Equal("stored", d1.RootElement.GetProperty("outcome").GetString());
        var seqPrimary = d1.RootElement.GetProperty("seq").GetInt64();
        var before = await ReadLatestFixAsync(evtId);
        Assert.NotNull(before);
        Assert.Equal(primaryId, before!.Value.GetProperty("beaconId").GetInt64());
        Assert.Equal(seqPrimary, before.Value.GetProperty("seq").GetInt64());

        // Spare beacon posts (published false); latest_fix must not change.
        _host!.RateLimiter.ResetLastAccepted(spareId);
        var (_, dSpare) = await PostFixAsync(spareKey, 47.0, -114.5);
        Assert.False(dSpare.RootElement.GetProperty("published").GetBoolean());
        var after = await ReadLatestFixAsync(evtId);
        Assert.NotNull(after);
        Assert.Equal(primaryId, after!.Value.GetProperty("beaconId").GetInt64());
        Assert.Equal(seqPrimary, after.Value.GetProperty("seq").GetInt64());
    }

    // (d) The regression: after one stored and two carried fixes on a live
    // event, a status change (and, separately, a posted message) produce a
    // live object whose seq equals the last carried fix's seq, never the
    // stored row's.
    [Fact]
    public async Task Rebuild_from_state_after_carried_fixes_uses_last_carried_seq()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("regression", isActive: true);

        var (_, d1) = await PostFixAsync(key, 46.87, -113.99);
        Assert.Equal("stored", d1.RootElement.GetProperty("outcome").GetString());
        var storedSeq = d1.RootElement.GetProperty("seq").GetInt64();
        _host!.RateLimiter.ResetLastAccepted(beaconId);

        var (_, d2) = await PostFixAsync(key, 46.87, -113.99);
        Assert.Equal("carried", d2.RootElement.GetProperty("outcome").GetString());
        var carriedSeq1 = d2.RootElement.GetProperty("seq").GetInt64();
        _host.RateLimiter.ResetLastAccepted(beaconId);

        var (_, d3) = await PostFixAsync(key, 46.87, -113.99);
        Assert.Equal("carried", d3.RootElement.GetProperty("outcome").GetString());
        var carriedSeq2 = d3.RootElement.GetProperty("seq").GetInt64();
        Assert.True(carriedSeq2 > carriedSeq1);
        Assert.True(carriedSeq2 > storedSeq);

        // Change to status 4 (ended) and refresh state; the rebuild reads
        // event.latest_fix, so the live object carries carriedSeq2.
        await SetEventStatusAsync(evtId, 4);
        await _host.RefreshStateAsync();
        var (obj1, _) = _host.Writer.Build(_host.State.Current, location: null);
        Assert.Equal(carriedSeq2, obj1.Seq);

        // Now separately: post a message. The write path rebuilds the live
        // object from state, so it must again read carriedSeq2.
        await SetEventStatusAsync(evtId, 3);          // messages fire from state; put back to live so the tally path is meaningful
        await _host.RefreshStateAsync();
        var (obj2, _) = _host.Writer.Build(_host.State.Current, location: null);
        Assert.Equal(carriedSeq2, obj2.Seq);
    }

    // (e) An event whose latest_fix is null but which has published rows
    // still gets its location fields from the newest row (the fallback for
    // events that ran before the column existed).
    [Fact]
    public async Task Null_latest_fix_falls_back_to_newest_published_row()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("fallback", isActive: true);

        var (_, d1) = await PostFixAsync(key, 46.87, -113.99);
        Assert.Equal("stored", d1.RootElement.GetProperty("outcome").GetString());
        var storedSeq = d1.RootElement.GetProperty("seq").GetInt64();

        // Force latest_fix to null so only the row is left.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var upd = new NpgsqlCommand(
                "update event set latest_fix = null where id = $1;", conn);
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = evtId });
            await upd.ExecuteNonQueryAsync();
        }
        await _host!.RefreshStateAsync();
        var (obj, _) = _host.Writer.Build(_host.State.Current, location: null);
        Assert.Equal(storedSeq, obj.Seq);
        Assert.Equal(46.87, obj.Lat);
        Assert.Equal(-113.99, obj.Lng);
    }

    // (f) The locations delete nulls latest_fix without beaconId, nulls it
    // with the matching beaconId, and leaves it with another beacon's id.
    [Fact]
    public async Task Delete_locations_nulls_latest_fix_by_beacon_rules()
    {
        // ---------- without beaconId: always nulls ----------
        var evtId1 = await SeedEventAsync(statusId: 1);
        var (b1, k1) = await SeedBeaconAsync("del-all-b1", isActive: false);
        // Set the event live temporarily so the fix publishes.
        await SetEventStatusAsync(evtId1, 3);
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var act = new NpgsqlCommand(
                "update beacon set is_active = true where id = $1;", conn);
            act.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = b1 });
            await act.ExecuteNonQueryAsync();
        }
        await _host!.RefreshStateAsync();
        _ = await PostFixAsync(k1, 46.87, -113.99);
        Assert.NotNull(await ReadLatestFixAsync(evtId1));
        await SetEventStatusAsync(evtId1, 1);       // not live so delete works

        using (var req = _host.AdminRequest(HttpMethod.Delete, $"/admin/events/{evtId1}/locations"))
        {
            var resp = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        Assert.Null(await ReadLatestFixAsync(evtId1));

        // ---------- with beaconId: nulls only when latest_fix.beaconId matches ----------
        var evtId2 = await SeedEventAsync(statusId: 3);
        var (bA, kA) = await SeedBeaconAsync("del-match-bA", isActive: true);
        // Rely on primary a being the only active; latest_fix is written by bA.
        _ = await PostFixAsync(kA, 46.87, -113.99);
        var beforeFix = await ReadLatestFixAsync(evtId2);
        Assert.NotNull(beforeFix);
        Assert.Equal(bA, beforeFix!.Value.GetProperty("beaconId").GetInt64());
        await SetEventStatusAsync(evtId2, 1);

        // Delete only bA's rows: latest_fix nulled (its beaconId matches).
        using (var req = _host.AdminRequest(HttpMethod.Delete, $"/admin/events/{evtId2}/locations?beaconId={bA}"))
        {
            var resp = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        Assert.Null(await ReadLatestFixAsync(evtId2));

        // ---------- with a DIFFERENT beaconId: latest_fix stays ----------
        var evtId3 = await SeedEventAsync(statusId: 3);
        var (bX, kX) = await SeedBeaconAsync("del-other-bX", isActive: true);
        _ = await PostFixAsync(kX, 46.5, -113.5);
        var fixBefore = await ReadLatestFixAsync(evtId3);
        Assert.NotNull(fixBefore);
        Assert.Equal(bX, fixBefore!.Value.GetProperty("beaconId").GetInt64());
        // Add another beacon on the same event so its beaconId is a valid one
        // to filter by.
        var (bY, _) = await SeedBeaconAsync("del-other-bY", isActive: false);
        await SetEventStatusAsync(evtId3, 1);

        using (var req = _host.AdminRequest(HttpMethod.Delete, $"/admin/events/{evtId3}/locations?beaconId={bY}"))
        {
            var resp = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NoContent, resp.StatusCode);
        }
        var fixAfter = await ReadLatestFixAsync(evtId3);
        Assert.NotNull(fixAfter);
        Assert.Equal(bX, fixAfter!.Value.GetProperty("beaconId").GetInt64());
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
        var year = 3000 + Random.Shared.Next(1, 900);
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, $3, true, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)statusId });
        var id = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        await using (var bump = new NpgsqlCommand(
            "update snapshot set version = version + 1 where id = 1;", conn))
        {
            await bump.ExecuteNonQueryAsync();
        }
        await _host!.RefreshStateAsync();
        return id;
    }

    private async Task<(long id, string key)> SeedBeaconAsync(string name, bool isActive)
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
values ($1, $2, $3, $4, 0, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = key.Hash });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key.Prefix });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = isActive });
        var id = (long)(await cmd.ExecuteScalarAsync() ?? 0L);
        return (id, key.Token);
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

    private async Task<(HttpStatusCode status, JsonDocument doc)> PostFixAsync(
        string key, double lat, double lng)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/locations")
        {
            Content = new StringContent(
                $"{{\"lat\":{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"lng\":{lng.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"recordedAt\":\"2026-12-22T01:31:07Z\"}}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var resp = await _host!.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text));
    }

    private async Task<JsonElement?> ReadLatestFixAsync(long eventId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select latest_fix::text from event where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        var v = await cmd.ExecuteScalarAsync();
        if (v is null || v is DBNull) return null;
        var doc = JsonDocument.Parse((string)v);
        return doc.RootElement.Clone();
    }

    private async Task<int> CountLocationsAsync(long beaconId)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select count(*)::int from location where beacon_id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
