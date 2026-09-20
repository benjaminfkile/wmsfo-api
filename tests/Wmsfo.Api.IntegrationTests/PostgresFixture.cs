using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.IntegrationTests;

// api.md 21: `WMSFO_TEST_DB_CONNECTION` when supplied wins; otherwise, when
// libpq's `PGHOST` is set (the grunt runner's local cluster on 127.0.0.1:5432
// with trust auth), the fixture builds a connection string from `PGHOST` /
// `PGPORT` / `PGUSER` / `PGPASSWORD` / `PGDATABASE`; otherwise Testcontainers.
// Each test database is created fresh per collection run and dropped
// afterwards, so no state leaks between runs. sql.md 12: the fixture also
// creates a migrate role that owns the database and an application role
// limited to select, insert, update, and delete on tables, unique per fixture
// instance, and drops them on dispose. `ConnectionString` stays the superuser
// connection to the same database so tests set up and assert freely; the
// API and its migrator receive `AppConnectionString` and
// `MigrateConnectionString` through `TestConnections.For`.
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _baseConnectionString = "";
    private string _databaseName = "";
    private string _migrateRole = "";
    private string _appRole = "";
    public string ConnectionString { get; private set; } = "";
    public string AppConnectionString { get; private set; } = "";
    public string MigrateConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        _databaseName = "wmsfo_it_" + suffix;
        _migrateRole = "wmsfo_t_migrate_" + suffix;
        _appRole = "wmsfo_t_app_" + suffix;

        var choice = IntegrationDatabaseSelector.Choose(Environment.GetEnvironmentVariable);
        switch (choice.Source)
        {
            case IntegrationDatabaseSource.SuppliedConnectionString:
            case IntegrationDatabaseSource.LibpqEnvironment:
                _baseConnectionString = choice.ConnectionString!;
                break;
            case IntegrationDatabaseSource.Testcontainers:
                try
                {
                    _container = new PostgreSqlBuilder("postgres:16-alpine")
                        .Build();
                    await _container.StartAsync();
                    _baseConnectionString = _container.GetConnectionString();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        "Integration tests need one of: `WMSFO_TEST_DB_CONNECTION` pointing at a Postgres server, libpq's `PGHOST` (the grunt runner exports this), or Docker for Testcontainers.",
                        ex);
                }
                break;
        }

        var migratePassword = RandomPassword();
        var appPassword = RandomPassword();
        await CreateFreshDatabaseAsync(migratePassword, appPassword);

        AppConnectionString = BuildRoleConnectionString(_appRole, appPassword);
        MigrateConnectionString = BuildRoleConnectionString(_migrateRole, migratePassword);

        // sql.md 12: migrations run as the migrate role so every table is owned
        // by that role and the default privileges grant select, insert, update,
        // delete on it to the application role. Tests may still call
        // `MigrateAsync` on `ConnectionString`; EF sees the history table
        // populated and does nothing.
        var options = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(MigrateConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        await using (var db = new WmsfoDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        TestConnections.Register(_databaseName, AppConnectionString, MigrateConnectionString);
    }

    public async Task DisposeAsync()
    {
        TestConnections.Unregister(_databaseName);

        if (!string.IsNullOrEmpty(ConnectionString))
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                await using var conn = new NpgsqlConnection(_baseConnectionString);
                await conn.OpenAsync();
                await using (var drop = new NpgsqlCommand($"drop database if exists \"{_databaseName}\" with (force)", conn))
                {
                    await drop.ExecuteNonQueryAsync();
                }
                foreach (var role in new[] { _appRole, _migrateRole })
                {
                    try
                    {
                        await using var dropRole = new NpgsqlCommand($"drop role if exists \"{role}\"", conn);
                        await dropRole.ExecuteNonQueryAsync();
                    }
                    catch
                    {
                        // best effort
                    }
                }
            }
            catch
            {
                // best effort
            }
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private async Task CreateFreshDatabaseAsync(string migratePassword, string appPassword)
    {
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await using (var create = new NpgsqlCommand($"create database \"{_databaseName}\"", conn))
        {
            await create.ExecuteNonQueryAsync();
        }

        await Exec(conn, $"create role \"{_migrateRole}\" login password {Literal(migratePassword)} nosuperuser nocreatedb nocreaterole noinherit");
        await Exec(conn, $"create role \"{_appRole}\" login password {Literal(appPassword)} nosuperuser nocreatedb nocreaterole noinherit");
        await Exec(conn, $"alter database \"{_databaseName}\" owner to \"{_migrateRole}\"");
        await Exec(conn, $"revoke all on database \"{_databaseName}\" from public");
        await Exec(conn, $"grant connect on database \"{_databaseName}\" to \"{_migrateRole}\", \"{_appRole}\"");
        await Exec(conn, $"alter role \"{_migrateRole}\" in database \"{_databaseName}\" set timezone = 'UTC'");
        await Exec(conn, $"alter role \"{_appRole}\" in database \"{_databaseName}\" set timezone = 'UTC'");

        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            Database = _databaseName,
        };
        ConnectionString = builder.ConnectionString;

        await using var dbConn = new NpgsqlConnection(ConnectionString);
        await dbConn.OpenAsync();
        await Exec(dbConn, $"grant usage, create on schema public to \"{_migrateRole}\"");
        await Exec(dbConn, "revoke create on schema public from public");
        await Exec(dbConn, $"grant usage on schema public to \"{_appRole}\"");
        await Exec(dbConn,
            $"alter default privileges for role \"{_migrateRole}\" in schema public " +
            $"grant select, insert, update, delete on tables to \"{_appRole}\"");
    }

    private string BuildRoleConnectionString(string role, string password)
    {
        // Bounded pool sizes so a suite that spins up many fixtures at once
        // does not exhaust the cluster's non-superuser connection slots.
        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            Database = _databaseName,
            Username = role,
            Password = password,
            MaxPoolSize = 8,
            MinPoolSize = 0,
        };
        return builder.ConnectionString;
    }

    private static string RandomPassword()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string Literal(string value) =>
        "'" + value.Replace("'", "''") + "'";

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
