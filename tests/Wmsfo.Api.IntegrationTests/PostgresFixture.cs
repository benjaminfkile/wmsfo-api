using Npgsql;
using Testcontainers.PostgreSql;

namespace Wmsfo.Api.IntegrationTests;

// api.md 21: `WMSFO_TEST_DB_CONNECTION` when supplied wins; otherwise, when
// libpq's `PGHOST` is set (the grunt runner's local cluster on 127.0.0.1:5432
// with trust auth), the fixture builds a connection string from `PGHOST` /
// `PGPORT` / `PGUSER` / `PGPASSWORD` / `PGDATABASE`; otherwise Testcontainers.
// Each test database is created fresh per collection run and dropped
// afterwards, so no state leaks between runs.
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _baseConnectionString = "";
    private string _databaseName = "";
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _databaseName = "wmsfo_it_" + Guid.NewGuid().ToString("N")[..12];

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

        await CreateFreshDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        if (!string.IsNullOrEmpty(ConnectionString))
        {
            NpgsqlConnection.ClearAllPools();
            try
            {
                await using var conn = new NpgsqlConnection(_baseConnectionString);
                await conn.OpenAsync();
                await using var drop = new NpgsqlCommand($"drop database if exists \"{_databaseName}\" with (force)", conn);
                await drop.ExecuteNonQueryAsync();
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

    private async Task CreateFreshDatabaseAsync()
    {
        await using var conn = new NpgsqlConnection(_baseConnectionString);
        await conn.OpenAsync();
        await using (var create = new NpgsqlCommand($"create database \"{_databaseName}\"", conn))
        {
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_baseConnectionString)
        {
            Database = _databaseName,
        };
        ConnectionString = builder.ConnectionString;
    }
}
