using System.Text;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Wmsfo.Api.IntegrationTests;

// api.md 21: Testcontainers when Docker is available, otherwise
// WMSFO_TEST_DB_CONNECTION (the grunt runner's local cluster). Each test
// database is created fresh per collection run, dropped afterwards, so no state
// leaks between runs.
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private string _baseConnectionString = "";
    private string _databaseName = "";
    public string ConnectionString { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _databaseName = "wmsfo_it_" + Guid.NewGuid().ToString("N")[..12];

        var supplied = Environment.GetEnvironmentVariable("WMSFO_TEST_DB_CONNECTION");
        if (!string.IsNullOrEmpty(supplied))
        {
            _baseConnectionString = supplied;
        }
        else
        {
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
                    "Integration tests need Docker (Testcontainers) or WMSFO_TEST_DB_CONNECTION set to a Postgres server the tests can create databases on.",
                    ex);
            }
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
