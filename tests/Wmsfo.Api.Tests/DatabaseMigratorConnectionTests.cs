using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Wmsfo.Api.Data;

namespace Wmsfo.Api.Tests;

// sql.md 12 and 13: migrations run as the migrate role, requests as the app role.
// The migrator builds its own context from the migrate connection string; it must
// never pick up the shared DbContext registration, which serves the app connection.
public class DatabaseMigratorConnectionTests
{
    private sealed class NoBoot : IFirstBootHook
    {
        public Task EnsureAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public void Migration_context_opens_on_the_migrate_role()
    {
        const string migrate = "Host=db.example;Port=5432;Database=wmsfo_dev;Username=wmsfo_migrate_dev;Password=x;Application Name=wmsfo-api-dev-migrate";
        var migrator = new DatabaseMigrator(migrate, new NoBoot(), NullLogger<DatabaseMigrator>.Instance);

        using var db = migrator.CreateMigrationContext();
        var actual = new NpgsqlConnectionStringBuilder(db.Database.GetConnectionString());

        Assert.Equal("wmsfo_migrate_dev", actual.Username);
        Assert.Equal("wmsfo-api-dev-migrate", actual.ApplicationName);
    }
}
