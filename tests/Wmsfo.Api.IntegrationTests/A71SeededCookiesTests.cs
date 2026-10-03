using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Data;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.IntegrationTests;

// Seeded cookies (contracts 4.5 Events, sql.md 3.15 and 8.9a): POST
// /admin/events/{id}/cookies inserts rows with person_id null and seeded_by
// the actor on the live event only, answers the event's whole tally, and the
// rows count in every tally and never toward any person.
public sealed class A71SeededCookiesTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A19Host? _host;

    public A71SeededCookiesTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        await MigrateAndCleanAsync();
        _host = await A19Host.StartAsync(_fixture.ConnectionString);
        await _host.GetService<SnapshotBootstrap>().EnsureVersionOneAsync(default);
        await _host.State.RefreshAsync("test:bootstrap", default);
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

        foreach (var sql in new[]
        {
            "delete from alert_delivery;",
            "delete from outbox;",
            "delete from audit_log;",
            "delete from location;",
            "delete from beacon;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from cookie;",
            "delete from event;",
            "delete from subscriber;",
            "delete from cookie_type;",
            "delete from person;",
        })
        {
            await ExecAsync(sql);
        }
    }

    [Fact]
    public async Task Seeding_the_live_event_inserts_seeded_rows_and_answers_the_whole_tally()
    {
        var eventId = await SeedEventAsync(2040, status: 3, isCurrent: true);
        var a = await SeedCookieTypeAsync("Chip", active: true);
        var b = await SeedCookieTypeAsync("Ginger", active: true);
        await LeaveCookiesAsync(a, 2);

        var response = await SeedAsync(eventId,
            $"{{\"items\":[{{\"cookieTypeId\":{b},\"count\":25}},{{\"cookieTypeId\":{a},\"count\":3}}]}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text);
        Assert.Equal(eventId, doc.RootElement.GetProperty("eventId").GetInt64());
        Assert.Equal(28, doc.RootElement.GetProperty("seeded").GetInt32());
        var tally = doc.RootElement.GetProperty("cookieTally");
        var keys = tally.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(new[] { a.ToString(), b.ToString() }, keys);
        Assert.Equal(5, tally.GetProperty(a.ToString()).GetInt32());
        Assert.Equal(25, tally.GetProperty(b.ToString()).GetInt32());

        Assert.Equal(28L, await CountAsync(
            $"select count(*) from cookie where event_id = {eventId} and person_id is null " +
            $"and seeded_by = 'person:{DevStaticTokens.AdminEmail}' and note is null;"));
        Assert.Equal(2L, await CountAsync(
            $"select count(*) from cookie where event_id = {eventId} and person_id is not null and seeded_by is null;"));
    }

    [Fact]
    public async Task The_live_object_carries_seeded_cookies_after_a_tick()
    {
        var eventId = await SeedEventAsync(2041, status: 3, isCurrent: true);
        var b = await SeedCookieTypeAsync("Sugar", active: true);
        await _host!.State.RefreshAsync("test:before", default);
        Assert.Empty(_host.State.Current.CookieTally);

        var response = await SeedAsync(eventId, $"{{\"items\":[{{\"cookieTypeId\":{b},\"count\":25}}]}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await _host.State.RefreshAsync("test:tick", default);
        await _host.Writer.WriteFromStateAsync("test:tick", default);
        var bytes = await WaitForLiveTallyAsync(b, atLeast: 25);
        using var live = JsonDocument.Parse(bytes);
        Assert.True(live.RootElement.GetProperty("cookieTally").GetProperty(b.ToString()).GetInt32() >= 25);

        // The tick's SQL count is the truth once the node delta is drained.
        await _host.State.RefreshAsync("test:tick2", default);
        Assert.Equal(25, _host.State.Current.CookieTally[b]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task Seeding_an_event_that_is_not_live_is_409_event_not_live(int status)
    {
        var eventId = await SeedEventAsync(2042 + status, status: status, isCurrent: false);
        var a = await SeedCookieTypeAsync("Chip", active: true);
        var response = await SeedAsync(eventId, $"{{\"items\":[{{\"cookieTypeId\":{a},\"count\":1}}]}}");
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("event_not_live", await ReadCodeAsync(response));
        Assert.Equal(0L, await CountAsync("select count(*) from cookie;"));
    }

    [Fact]
    public async Task Seeding_an_unknown_event_is_404()
    {
        var a = await SeedCookieTypeAsync("Chip", active: true);
        var response = await SeedAsync(999999, $"{{\"items\":[{{\"cookieTypeId\":{a},\"count\":1}}]}}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await ReadCodeAsync(response));
    }

    [Fact]
    public async Task Bad_items_are_400_validation_failed_on_the_field()
    {
        var eventId = await SeedEventAsync(2050, status: 3, isCurrent: true);
        var a = await SeedCookieTypeAsync("Chip", active: true);

        await AssertFieldAsync(eventId, $"{{\"items\":[{{\"cookieTypeId\":{a},\"count\":0}}]}}", "items[0].count");
        await AssertFieldAsync(eventId, $"{{\"items\":[{{\"cookieTypeId\":{a},\"count\":101}}]}}", "items[0].count");
        await AssertFieldAsync(eventId,
            $"{{\"items\":[{{\"cookieTypeId\":{a},\"count\":1}},{{\"cookieTypeId\":{a},\"count\":1}}]}}",
            "items[1].cookieTypeId");
        await AssertFieldAsync(eventId, "{\"items\":[]}", "items");
        Assert.Equal(0L, await CountAsync("select count(*) from cookie;"));

        // 100 is the upper bound and is accepted.
        var ok = await SeedAsync(eventId, $"{{\"items\":[{{\"cookieTypeId\":{a},\"count\":100}}]}}");
        Assert.Equal(HttpStatusCode.Created, ok.StatusCode);
    }

    [Fact]
    public async Task An_inactive_type_is_404_naming_it_in_details()
    {
        var eventId = await SeedEventAsync(2051, status: 3, isCurrent: true);
        var a = await SeedCookieTypeAsync("Chip", active: true);
        var off = await SeedCookieTypeAsync("Retired", active: false);
        var response = await SeedAsync(eventId,
            $"{{\"items\":[{{\"cookieTypeId\":{a},\"count\":1}},{{\"cookieTypeId\":{off},\"count\":1}}]}}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement;
        Assert.Equal("not_found", error.GetProperty("code").GetString());
        var ids = error.GetProperty("details").GetProperty("cookieTypeIds").EnumerateArray().Select(e => e.GetInt64()).ToList();
        Assert.Equal(new[] { off }, ids);
        Assert.Equal(0L, await CountAsync("select count(*) from cookie;"));
    }

    [Fact]
    public async Task Seeded_cookies_never_count_toward_a_person()
    {
        var eventId = await SeedEventAsync(2052, status: 3, isCurrent: true);
        var a = await SeedCookieTypeAsync("Chip", active: true);
        await LeaveCookiesAsync(a, 2);
        var seeded = await SeedAsync(eventId, $"{{\"items\":[{{\"cookieTypeId\":{a},\"count\":30}}]}}");
        Assert.Equal(HttpStatusCode.Created, seeded.StatusCode);

        // GET /me/cookies counts only the person's rows.
        using (var req = _host!.PersonRequest(HttpMethod.Get, "/me/cookies"))
        {
            var response = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(2, doc.RootElement.GetProperty("used").GetInt32());
            Assert.Equal(2, doc.RootElement.GetProperty("items").GetArrayLength());
        }

        // The per-person limit ignores the 30 seeded rows.
        await LeaveCookiesAsync(a, 1);

        // GET /admin/people cookieCount sees only the person's rows.
        using (var req = _host.AdminRequest(HttpMethod.Get, "/admin/people"))
        {
            var response = await _host.Client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var person = doc.RootElement.GetProperty("items").EnumerateArray()
                .Single(p => p.GetProperty("email").GetString() == DevStaticTokens.PersonEmail);
            Assert.Equal(3, person.GetProperty("cookieCount").GetInt32());
        }

        // Deleting the person leaves the seeded rows.
        var personId = await CountAsync($"select id from person where email = '{DevStaticTokens.PersonEmail}';");
        using (var del = _host.AdminRequest(HttpMethod.Delete, $"/admin/people/{personId}"))
        {
            var response = await _host.Client.SendAsync(del);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }
        Assert.Equal(30L, await CountAsync($"select count(*) from cookie where event_id = {eventId};"));
    }

    [Fact]
    public async Task Final_cookie_tally_includes_seeded_cookies_on_entry_into_status_4()
    {
        var eventId = await SeedEventAsync(2053, status: 3, isCurrent: true);
        var a = await SeedCookieTypeAsync("Chip", active: true);
        var b = await SeedCookieTypeAsync("Ginger", active: true);
        await LeaveCookiesAsync(a, 2);
        var seeded = await SeedAsync(eventId,
            $"{{\"items\":[{{\"cookieTypeId\":{a},\"count\":4}},{{\"cookieTypeId\":{b},\"count\":7}}]}}");
        Assert.Equal(HttpStatusCode.Created, seeded.StatusCode);

        using var req = _host!.AdminRequest(HttpMethod.Post, $"/admin/events/{eventId}/status");
        req.Content = new StringContent("{\"statusId\":4,\"notify\":false}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select final_cookie_tally::text from event where id = $1;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        using var doc = JsonDocument.Parse((string)(await cmd.ExecuteScalarAsync())!);
        Assert.Equal(6, doc.RootElement.GetProperty(a.ToString()).GetInt32());
        Assert.Equal(7, doc.RootElement.GetProperty(b.ToString()).GetInt32());
    }

    [Fact]
    public async Task The_audit_row_carries_cookies_seeded_with_the_documented_after()
    {
        var eventId = await SeedEventAsync(2054, status: 3, isCurrent: true);
        var a = await SeedCookieTypeAsync("Chip", active: true);
        var b = await SeedCookieTypeAsync("Ginger", active: true);
        var response = await SeedAsync(eventId,
            $"{{\"items\":[{{\"cookieTypeId\":{b},\"count\":25}},{{\"cookieTypeId\":{a},\"count\":2}}]}}");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
select actor, entity, entity_id, before is null, after::text
from audit_log where action = 'cookies_seeded';", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("person:" + DevStaticTokens.AdminEmail, reader.GetString(0));
        Assert.Equal("event", reader.GetString(1));
        Assert.Equal(eventId.ToString(), reader.GetString(2));
        Assert.True(reader.GetBoolean(3));
        using var after = JsonDocument.Parse(reader.GetString(4));
        Assert.Equal(27, after.RootElement.GetProperty("seeded").GetInt32());
        var items = after.RootElement.GetProperty("items").EnumerateArray()
            .Select(e => (e.GetProperty("cookieTypeId").GetInt64(), e.GetProperty("count").GetInt32()))
            .ToList();
        Assert.Equal(new[] { (b, 25), (a, 2) }, items);
        Assert.False(await reader.ReadAsync());
        Assert.Equal(0L, await CountAsync("select count(*) from outbox;"));
    }

    [Fact]
    public async Task The_origin_constraint_refuses_both_columns_and_neither()
    {
        var eventId = await SeedEventAsync(2055, status: 3, isCurrent: true);
        var a = await SeedCookieTypeAsync("Chip", active: true);
        await LeaveCookiesAsync(a, 1);
        var personId = await CountAsync($"select id from person where email = '{DevStaticTokens.PersonEmail}';");

        var both = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(
            $"insert into cookie (event_id, person_id, cookie_type_id, seeded_by) values ({eventId}, {personId}, {a}, 'person:x@example.com');"));
        Assert.Equal("23514", both.SqlState);
        Assert.Equal("cookie_origin_check", both.ConstraintName);

        var neither = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(
            $"insert into cookie (event_id, person_id, cookie_type_id, seeded_by) values ({eventId}, null, {a}, null);"));
        Assert.Equal("23514", neither.SqlState);
        Assert.Equal("cookie_origin_check", neither.ConstraintName);
    }

    // ---------------- helpers ----------------

    private async Task<HttpResponseMessage> SeedAsync(long eventId, string body)
    {
        using var req = _host!.AdminRequest(HttpMethod.Post, $"/admin/events/{eventId}/cookies");
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    private async Task AssertFieldAsync(long eventId, string body, string field)
    {
        var response = await SeedAsync(eventId, body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = doc.RootElement;
        Assert.Equal("validation_failed", error.GetProperty("code").GetString());
        Assert.True(error.GetProperty("details").GetProperty("fields").TryGetProperty(field, out _),
            $"expected field {field} in {doc.RootElement.GetRawText()}");
    }

    private async Task LeaveCookiesAsync(long typeId, int count)
    {
        using var req = _host!.PersonRequest(HttpMethod.Post, "/cookies");
        req.Content = new StringContent(
            $"{{\"items\":[{{\"cookieTypeId\":{typeId},\"count\":{count}}}]}}", Encoding.UTF8, "application/json");
        var response = await _host.Client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("code").GetString();
    }

    private async Task<long> SeedEventAsync(int year, int status, bool isCurrent)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, scheduled_at, created_by, updated_at)
values ($1, $2, $3, $4, now() + interval '1 hour', 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Smallint, Value = (short)status });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = isCurrent });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task<long> SeedCookieTypeAsync(string name, bool active)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into cookie_type (name, icon, sort, active, updated_at)
values ($1, '{""source"":""library"",""id"":""cookie""}'::jsonb, 0, $2, now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = name });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = active });
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private async Task ExecAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> CountAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var r = await cmd.ExecuteScalarAsync();
        return r is null || r is DBNull ? 0L : Convert.ToInt64(r);
    }

    private async Task<byte[]> WaitForLiveTallyAsync(long cookieTypeId, int atLeast)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            byte[]? bytes = null;
            try { bytes = (await _host!.Store.GetObjectAsync(LiveObjectWriter.CdnKey))?.Bytes; }
            catch (IOException) { }
            if (bytes is not null)
            {
                using var doc = JsonDocument.Parse(bytes);
                if (doc.RootElement.TryGetProperty("cookieTally", out var tally)
                    && tally.TryGetProperty(cookieTypeId.ToString(), out var count)
                    && count.GetInt32() >= atLeast)
                {
                    return bytes;
                }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"live object cookie tally never reached {atLeast} for {cookieTypeId}");
    }
}
