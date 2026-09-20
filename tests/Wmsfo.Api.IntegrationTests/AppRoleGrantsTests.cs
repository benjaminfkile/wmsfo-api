using Npgsql;

namespace Wmsfo.Api.IntegrationTests;

// A46 acceptance: PostgresFixture wires every test database with the two
// roles of sql.md 12. The application role cannot create temporary tables
// (42501), cannot create tables in the public schema (42501), and can select
// from a migrated table; the migrate role can create a table in public.
public sealed class AppRoleGrantsTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _fixture;

    public AppRoleGrantsTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task App_role_cannot_create_a_temporary_table()
    {
        await using var conn = new NpgsqlConnection(_fixture.AppConnectionString);
        await conn.OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("create temp table t (id int)", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task App_role_cannot_create_a_public_table()
    {
        await using var conn = new NpgsqlConnection(_fixture.AppConnectionString);
        await conn.OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("create table public.t (id int)", conn);
            await cmd.ExecuteNonQueryAsync();
        });
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task App_role_can_select_from_a_migrated_table()
    {
        await using var conn = new NpgsqlConnection(_fixture.AppConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("select 1 from event limit 1", conn);
        _ = await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Migrate_role_can_create_a_public_table()
    {
        await using var conn = new NpgsqlConnection(_fixture.MigrateConnectionString);
        await conn.OpenAsync();
        await using (var create = new NpgsqlCommand("create table public.a46_probe (id int)", conn))
        {
            await create.ExecuteNonQueryAsync();
        }
        await using var drop = new NpgsqlCommand("drop table public.a46_probe", conn);
        await drop.ExecuteNonQueryAsync();
    }
}
