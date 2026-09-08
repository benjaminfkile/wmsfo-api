using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Data;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.IntegrationTests;

// A9 acceptance: the constraint-name mapping of sql.md 4.3. Fire real
// PostgresException raised by real constraints (event_year_key,
// route_s3_key_key, event_one_live, subscriber_channel_address_key) and assert
// the returned ApiException matches the mapping.
public sealed class A9ConstraintMappingTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _fixture;

    public A9ConstraintMappingTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(_fixture.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var db = new WmsfoDbContext(options);
        await db.Database.MigrateAsync();
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
        {
            "delete from event;",
            "delete from route;",
            "delete from subscriber;",
            "delete from person;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // Real 23505 raised by event_year_key ends up as a PostgresException the
    // mapper transforms into a 409 year_taken ApiException.
    [Fact]
    public async Task Real_event_year_key_conflict_maps_to_year_taken()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await Insert("insert into event (year, name, status_id, created_by, updated_at) values (1999, 'x', 1, 'seed', now());");
        var thrown = await Assert.ThrowsAsync<PostgresException>(async () =>
            await Insert("insert into event (year, name, status_id, created_by, updated_at) values (1999, 'y', 1, 'seed', now());"));
        Assert.Equal("23505", thrown.SqlState);
        Assert.Equal(ConstraintErrorMapping.EventYearKey, thrown.ConstraintName);
        var mapped = ConstraintErrorMapping.Map(thrown);
        Assert.NotNull(mapped);
        Assert.Equal("year_taken", mapped!.Code);

        async Task Insert(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // Real 23505 raised by event_one_live maps to another_event_live.
    [Fact]
    public async Task Real_event_one_live_conflict_maps_to_another_event_live()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await Insert("insert into event (year, name, status_id, created_by, updated_at) values (1997, 'a', 3, 'seed', now());");
        var thrown = await Assert.ThrowsAsync<PostgresException>(async () =>
            await Insert("insert into event (year, name, status_id, created_by, updated_at) values (1998, 'b', 3, 'seed', now());"));
        Assert.Equal("23505", thrown.SqlState);
        Assert.Equal(ConstraintErrorMapping.EventOneLive, thrown.ConstraintName);
        var mapped = ConstraintErrorMapping.Map(thrown);
        Assert.NotNull(mapped);
        Assert.Equal("another_event_live", mapped!.Code);

        async Task Insert(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // Real 23505 raised by route_s3_key_key does NOT map (the upload handler
    // catches it explicitly and returns 200 with the existing row).
    [Fact]
    public async Task Real_route_s3_key_conflict_is_not_mapped()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        await Insert("insert into route (name, s3_key, url, sha256, point_count, uploaded_by) values ('r','routes/a.json','u','" + new string('a', 64) + "', 2, 'seed');");
        var thrown = await Assert.ThrowsAsync<PostgresException>(async () =>
            await Insert("insert into route (name, s3_key, url, sha256, point_count, uploaded_by) values ('r2','routes/a.json','u2','" + new string('b', 64) + "', 2, 'seed');"));
        Assert.Equal("23505", thrown.SqlState);
        Assert.Equal(ConstraintErrorMapping.RouteS3KeyKey, thrown.ConstraintName);
        Assert.Null(ConstraintErrorMapping.Map(thrown));

        async Task Insert(string sql)
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    // Real 23505 raised by subscriber_channel_address_key maps to address_taken.
    [Fact]
    public async Task Real_subscriber_channel_address_conflict_maps_to_address_taken()
    {
        await using var conn = new NpgsqlConnection(_fixture.ConnectionString);
        await conn.OpenAsync();
        long personId;
        await using (var p = new NpgsqlCommand(
            "insert into person (cognito_sub, email) values (gen_random_uuid(), 'p@x') returning id;", conn))
        {
            personId = (long)(await p.ExecuteScalarAsync() ?? 0L);
        }
        await using (var s1 = new NpgsqlCommand(
            "insert into subscriber (person_id, channel, address, unsubscribe_token) values ($1, 'email', 'a@x', 'wsu_" + new string('a', 43) + "');", conn))
        {
            s1.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
            await s1.ExecuteNonQueryAsync();
        }
        var thrown = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var s2 = new NpgsqlCommand(
                "insert into subscriber (person_id, channel, address, unsubscribe_token) values ($1, 'email', 'a@x', 'wsu_" + new string('b', 43) + "');", conn);
            s2.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = personId });
            await s2.ExecuteNonQueryAsync();
        });
        Assert.Equal(ConstraintErrorMapping.SubscriberChannelAddressKey, thrown.ConstraintName);
        var mapped = ConstraintErrorMapping.Map(thrown);
        Assert.NotNull(mapped);
        Assert.Equal("address_taken", mapped!.Code);
    }

}
