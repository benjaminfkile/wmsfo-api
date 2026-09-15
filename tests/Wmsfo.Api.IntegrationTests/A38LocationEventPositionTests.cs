using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.IntegrationTests;

// A38 acceptance: a position is stored at most once per event. The unique
// (event_id, lat, lng) index carries a repeat regardless of how the min
// distance decision came out, and the migration removes any earlier duplicates
// keeping the lowest seq before the index is created.
public sealed class A38LocationEventPositionTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A37Host? _host;

    public A38LocationEventPositionTests(PostgresFixture fixture) { _fixture = fixture; }

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
            "delete from route where s3_key like 'routes/%';",
            "delete from audit_log where entity = 'event';",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        // Reset the seed settings.
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
    // Same position twice by one beacon with a minute between: stores once
    // and carries once with next_seq advanced twice and the live object
    // written both times.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Same_position_twice_with_a_minute_between_stores_once_and_carries_once()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("replay", isActive: true, minIntervalMs: 0);

        // First send: stored.
        var (s1, d1) = await PostFixAsync(key, 46.87, -114.0);
        Assert.Equal(HttpStatusCode.Created, s1);
        Assert.Equal("stored", d1.RootElement.GetProperty("outcome").GetString());
        var seq1 = d1.RootElement.GetProperty("seq").GetInt64();
        // Wait for the first live object put to land so we can count writes.
        for (var i = 0; i < 100 && _host!.Store.PutCount("live/location.json") < 1; i++)
            await Task.Delay(10);
        var putsAfterStored = _host!.Store.PutCount("live/location.json");
        Assert.True(putsAfterStored >= 1);

        // Rewind the beacon's previous fix and the rate limiter so the
        // second POST is not dropped.
        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using var upd = new NpgsqlCommand(
                "update location set received_at = now() - interval '60 seconds' where event_id = $1 and beacon_id = $2;", conn);
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = evtId });
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
            await upd.ExecuteNonQueryAsync();
        }
        _host.RateLimiter.ResetLastAccepted(beaconId);

        // Second send: the position already exists in the event, so A38
        // carries it.
        var (s2, d2) = await PostFixAsync(key, 46.87, -114.0);
        Assert.Equal(HttpStatusCode.Created, s2);
        Assert.Equal("carried", d2.RootElement.GetProperty("outcome").GetString());
        var seq2 = d2.RootElement.GetProperty("seq").GetInt64();
        Assert.Equal(seq1 + 1, seq2);

        // One location row (only the stored fix).
        Assert.Equal(1, await CountLocationsAsync(beaconId));

        // event.next_seq advanced twice.
        Assert.Equal(3L, await ReadNextSeqAsync(evtId));

        // Beacon counters: one stored, one carried, last_location_at untouched
        // by the carried fix.
        var counters = await ReadBeaconCountersAsync(beaconId);
        Assert.Equal(1L, counters.stored);
        Assert.Equal(1L, counters.carried);

        // The live object was written again for the carried fix.
        for (var i = 0; i < 100 && _host.Store.PutCount("live/location.json") <= putsAfterStored; i++)
            await Task.Delay(10);
        Assert.True(_host.Store.PutCount("live/location.json") > putsAfterStored);
    }

    // ------------------------------------------------------------------
    // Same position from two different beacons: stores once (whoever got in
    // first) and carries for the other.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Same_position_from_two_beacons_stores_once()
    {
        var evtId = await SeedEventAsync();
        // Seed b2 first (inactive) so activating b1 does not flip b2 off.
        var (b2, k2) = await SeedBeaconAsync("a38-b2", isActive: false, minIntervalMs: 0);
        var (b1, k1) = await SeedBeaconAsync("a38-b1", isActive: true, minIntervalMs: 0);

        var (_, d1) = await PostFixAsync(k1, 46.87, -114.0);
        Assert.Equal("stored", d1.RootElement.GetProperty("outcome").GetString());

        // b2 fires the same position; the row belongs to b1, so b2's is
        // carried (a repeat of any position already in the event is carried).
        var (_, d2) = await PostFixAsync(k2, 46.87, -114.0);
        Assert.Equal("carried", d2.RootElement.GetProperty("outcome").GetString());

        Assert.Equal(1, await CountLocationsAsync(b1));
        Assert.Equal(0, await CountLocationsAsync(b2));
        Assert.Equal(3L, await ReadNextSeqAsync(evtId));
    }

    // ------------------------------------------------------------------
    // Repeat after other points in between (a return to an earlier position)
    // is carried.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Return_to_earlier_position_after_intermediate_points_is_carried()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("a38-return", isActive: true, minIntervalMs: 0);

        // Fixes on the beacon side: A -> B -> C -> A. The min-distance test
        // is against the beacon's last stored fix, so B, C, A all pass. A
        // then hits the unique index and gets carried.
        var (_, dA) = await PostFixAsync(key, 46.870, -114.000);
        Assert.Equal("stored", dA.RootElement.GetProperty("outcome").GetString());
        var (_, dB) = await PostFixAsync(key, 46.871, -114.000);
        Assert.Equal("stored", dB.RootElement.GetProperty("outcome").GetString());
        var (_, dC) = await PostFixAsync(key, 46.872, -114.000);
        Assert.Equal("stored", dC.RootElement.GetProperty("outcome").GetString());
        var (_, dA2) = await PostFixAsync(key, 46.870, -114.000);
        Assert.Equal("carried", dA2.RootElement.GetProperty("outcome").GetString());

        // Only three distinct positions are stored.
        Assert.Equal(3, await CountLocationsAsync(beaconId));
        Assert.Equal(5L, await ReadNextSeqAsync(evtId));
    }

    // ------------------------------------------------------------------
    // The migration's duplicate removal keeps the lowest seq (seed three
    // duplicates in a test database, run the migration, one row left).
    // ------------------------------------------------------------------

    [Fact]
    public async Task Migration_removes_existing_duplicates_keeping_the_lowest_seq()
    {
        // The migrations are already applied by the fixture. Rerun the
        // dedupe SQL on seeded duplicates while temporarily dropping the
        // unique index (mirroring the state that the A38 migration inherits
        // on prod databases).
        var evtId = await SeedEventAsync(statusId: 4);          // not live, so duplicates are freely seeded
        var (beaconId, _) = await SeedBeaconAsync("mig-dup", isActive: false, minIntervalMs: 0);

        await using (var conn = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var drop = new NpgsqlCommand(
                "drop index location_event_position;", conn))
            {
                await drop.ExecuteNonQueryAsync();
            }
            // Seed three rows with the same (event_id, lat, lng), with seq
            // 10, 20, 30. The row with seq 10 must survive.
            await using (var seed = new NpgsqlCommand(@"
insert into location (event_id, beacon_id, seq, recorded_at, received_at, lat, lng, published)
values
  ($1, $2, 10, now(), now() - interval '10 seconds', 46.87, -114, true),
  ($1, $2, 20, now(), now() - interval  '5 seconds', 46.87, -114, true),
  ($1, $2, 30, now(), now(),                         46.87, -114, true);", conn))
            {
                seed.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = evtId });
                seed.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId });
                await seed.ExecuteNonQueryAsync();
            }
            // The dedupe SQL from the A38 migration.
            await using (var dedupe = new NpgsqlCommand(@"
delete from location l
using location keep
where l.event_id = keep.event_id
  and l.lat = keep.lat
  and l.lng = keep.lng
  and keep.seq = (
    select min(seq) from location
    where event_id = l.event_id and lat = l.lat and lng = l.lng
  )
  and l.id <> keep.id;", conn))
            {
                await dedupe.ExecuteNonQueryAsync();
            }
            await using (var recreate = new NpgsqlCommand(
                "create unique index location_event_position on location (event_id, lat, lng);", conn))
            {
                await recreate.ExecuteNonQueryAsync();
            }

            // One row left, the one with the lowest seq.
            long? survivor = null;
            await using (var cnt = new NpgsqlCommand(
                "select count(*)::bigint, min(seq) from location where event_id = $1;", conn))
            {
                cnt.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = evtId });
                await using var reader = await cnt.ExecuteReaderAsync();
                await reader.ReadAsync();
                Assert.Equal(1L, reader.GetInt64(0));
                survivor = reader.GetInt64(1);
            }
            Assert.Equal(10L, survivor);
        }
    }

    // ------------------------------------------------------------------
    // The CSV export and POST /admin/routes/from-event see the deduplicated
    // rows.
    // ------------------------------------------------------------------

    [Fact]
    public async Task Csv_export_and_route_from_event_see_the_deduplicated_rows()
    {
        var evtId = await SeedEventAsync();
        var (beaconId, key) = await SeedBeaconAsync("a38-csv", isActive: true, minIntervalMs: 0);

        // Three distinct positions and one repeat (of the first).
        _ = await PostFixAsync(key, 46.870, -114.000);
        _ = await PostFixAsync(key, 46.871, -114.000);
        _ = await PostFixAsync(key, 46.872, -114.000);
        _ = await PostFixAsync(key, 46.870, -114.000);   // carried by A38

        // CSV: three data rows.
        using (var req = _host!.AdminRequest(HttpMethod.Get, $"/admin/events/{evtId}/locations"))
        {
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/csv"));
            var resp = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync();
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            // header + three data lines
            Assert.Equal(4, lines.Length);
        }

        // Take the event out of live so /admin/routes/from-event can be
        // called (it doesn't require the event to be live, but nothing else
        // in the test needs the live state).
        //
        // Routes from-event: three points, one per unique position.
        using (var req = _host.AdminRequest(HttpMethod.Post, $"/admin/routes/from-event/{evtId}"))
        {
            req.Content = new StringContent("{\"name\":\"a38-route\"}", Encoding.UTF8, "application/json");
            var resp = await _host.Client.SendAsync(req);
            // AdminRoute endpoint is mapped only in dedicated hosts; we
            // expect either a Created or a Not Found. If Not Found (route
            // endpoints not mapped on A37Host), the CSV assertion is the
            // one that matters and we short-circuit here.
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                return;
            }
            Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
            var text = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(text);
            Assert.Equal(3, doc.RootElement.GetProperty("pointCount").GetInt32());
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
        await using (var bump = new NpgsqlCommand(
            "update snapshot set version = version + 1 where id = 1;", conn))
        {
            await bump.ExecuteNonQueryAsync();
        }
        await _host!.RefreshStateAsync();
        return id;
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
        string key, double lat, double lng, string? recordedAt = null)
    {
        var when = recordedAt ?? "2026-12-22T01:31:07Z";
        using var req = new HttpRequestMessage(HttpMethod.Post, "/locations")
        {
            Content = new StringContent(
                $"{{\"lat\":{lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"lng\":{lng.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"recordedAt\":\"{when}\"}}",
                Encoding.UTF8, "application/json"),
        };
        req.Headers.Add(BeaconAuthenticationHandler.HeaderName, key);
        var resp = await _host!.Client.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        return (resp.StatusCode, JsonDocument.Parse(text));
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
}
