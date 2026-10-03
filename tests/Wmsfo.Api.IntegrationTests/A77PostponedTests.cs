using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Content;
using Wmsfo.Api.Data;
using Wmsfo.Api.Email;

namespace Wmsfo.Api.IntegrationTests;

// A77 acceptance: status 6 is postponed.
//   - a change to 6 with notify true succeeds, and the queued delivery
//     renders event_postponed with the stock paragraph
//   - status 7 is 400
public sealed class A77PostponedStatusTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;
    private A30Host? _host;
    private EmailTemplates? _templates;

    public A77PostponedStatusTests(PostgresFixture fixture) { _fixture = fixture; }

    public async Task InitializeAsync()
    {
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(opts))
            await db.Database.MigrateAsync();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from alert_delivery;",
            "delete from outbox;",
            "delete from cookie;",
            "delete from location;",
            "delete from event_status_history;",
            "delete from event_message;",
            "delete from event;",
            "delete from subscriber;",
            "delete from person;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        await SnapshotSeed.EnsureAsync(conn);
        _host = await A30Host.StartAsync(_fixture.ConnectionString);
        _templates = EmailTemplates.Load(TestPaths.EmailTemplatesDir);
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.DisposeAsync();
    }

    [Fact]
    public async Task Change_to_postponed_with_notify_sends_the_postponed_template_with_the_stock_paragraph()
    {
        var id = await CreateEventAsync(2201);
        const string address = "postponed@wmsfo.test";
        await CreateVerifiedSubscriberAsync(await CreatePersonAsync(address), address);

        var response = await SendAdminAsync($"/admin/events/{id}/status", "{\"statusId\":6,\"notify\":true}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(6, Convert.ToInt32(await ScalarAsync("select status_id from event where id = $1;", id)));

        await _host!.Outbox.RunOnceAsync(CancellationToken.None);
        await _host.Alerts.RunOnceAsync(CancellationToken.None);
        var sent = Assert.Single(_host.Sender.Sent);
        Assert.Equal(EmailTemplates.EventPostponed, sent.TemplateName);
        var stock = EmailTemplates.StockParagraph(6, "Event 2201", null);
        Assert.Equal("Event 2201 is postponed. A new time will be announced when it is known.", stock);
        Assert.Equal(stock, sent.Values["customMessage"]);
        var rendered = _templates!.Render(sent.TemplateName, sent.Values);
        Assert.Equal("Santa's flight is postponed", rendered.Subject);
        Assert.Contains(stock, rendered.Text);
    }

    [Fact]
    public async Task Status_seven_is_400()
    {
        var id = await CreateEventAsync(2202);
        var response = await SendAdminAsync($"/admin/events/{id}/status", "{\"statusId\":7,\"notify\":false}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync("select status_id from event where id = $1;", id)));
    }

    private async Task<HttpResponseMessage> SendAdminAsync(string path, string body)
    {
        using var req = _host!.AdminRequest(HttpMethod.Post, path);
        req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return await _host.Client.SendAsync(req);
    }

    // A current event in status 1.
    private async Task<long> CreateEventAsync(int year)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using (var clr = new NpgsqlCommand("update event set is_current = false where is_current;", conn))
            await clr.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into event (year, name, status_id, is_current, created_by, updated_at)
values ($1, $2, 1, true, 'seed', now()) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = year });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = $"Event {year}" });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task<long> CreatePersonAsync(string email)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "insert into person (cognito_sub, email) values (gen_random_uuid(), $1) returning id;", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
        return (long)(await cmd.ExecuteScalarAsync() ?? 0L);
    }

    private async Task CreateVerifiedSubscriberAsync(long personId, string address)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(@"
insert into subscriber (person_id, channel, address, unsubscribe_token, verified_at)
values ($1, 'email', $2, $3, now());", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = address });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = "wsu_" + Guid.NewGuid().ToString("N") });
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<object> ScalarAsync(string sql, long param)
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = param });
        return await cmd.ExecuteScalarAsync() ?? DBNull.Value;
    }
}

// A77 role page: a fresh database gets seven role pages from the first boot
// seed; the migration adds the postponed page, once, to a working set that
// has the six older role pages.
public sealed class A77PostponedPageTests : IClassFixture<PostgresFixture>
{
    private const string BeforeA77 = "20261003153449_A74StatusMessageRef";
    private readonly PostgresFixture _fixture;

    public A77PostponedPageTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Fresh_database_seeds_seven_role_pages_and_migration_adds_postponed_once()
    {
        var opts = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.MigrateConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(opts);
        var migrator = db.GetService<IMigrator>();
        await db.Database.MigrateAsync();

        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();

        // A fresh database: the migration adds no page, and first boot seeds seven.
        await ExecAsync(conn, "delete from page;");
        await migrator.MigrateAsync(BeforeA77);
        await migrator.MigrateAsync();
        Assert.Equal(0L, await CountAsync(conn, "select count(*) from page;"));
        await new StarterContent(NullLogger<StarterContent>.Instance).EnsureSeededAsync(conn, default);
        Assert.Equal(7L, await CountAsync(conn, "select count(*) from page where role <> 'none';"));
        Assert.Equal(1L, await CountAsync(conn, "select count(*) from page where role = 'postponed' and slug = 'postponed';"));
        Assert.Equal("postponed", await ScalarAsync(conn, "select name from event_status where id = 6;"));

        // A deployment with the six older role pages gets the seventh, once.
        await migrator.MigrateAsync(BeforeA77);
        Assert.Equal(6L, await CountAsync(conn, "select count(*) from page where role <> 'none';"));
        Assert.Equal(0L, await CountAsync(conn, "select count(*) from event_status where id = 6;"));
        await migrator.MigrateAsync();
        await migrator.MigrateAsync();

        Assert.Equal(7L, await CountAsync(conn, "select count(*) from page where role <> 'none';"));
        await using (var cmd = new NpgsqlCommand(@"
select slug, title, nav_label, is_hidden, created_by from page where role = 'postponed';", conn))
        await using (var reader = await cmd.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.Equal("postponed", reader.GetString(0));
            Assert.Equal("Postponed", reader.GetString(1));
            Assert.True(reader.IsDBNull(2));
            Assert.False(reader.GetBoolean(3));
            Assert.Equal("seed", reader.GetString(4));
            Assert.False(await reader.ReadAsync());
        }
        Assert.Equal(6L, await CountAsync(conn,
            "select count(*) from section s join page p on p.id = s.page_id where p.role = 'postponed';"));
        Assert.Equal("Santa's flight is postponed", await ScalarAsync(conn, @"
select s.data->>'title' from section s join page p on p.id = s.page_id
where p.role = 'postponed' and s.kind = 'hero';"));
        Assert.Equal("A new time will be announced here and by email.", await ScalarAsync(conn, @"
select s.data->>'tagline' from section s join page p on p.id = s.page_id
where p.role = 'postponed' and s.kind = 'hero';"));
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountAsync(NpgsqlConnection conn, string sql) =>
        Convert.ToInt64(await ScalarAsync(conn, sql));

    private static async Task<object> ScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync() ?? DBNull.Value;
    }
}
