using System.Collections.Concurrent;
using Npgsql;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.IntegrationTests;

// api.md 21: integration tests receive the fixture's application and migrate
// connection strings through this map instead of reusing the superuser
// connection. `PostgresFixture` registers a row when it initializes and
// removes it when it disposes; `For` reads the database name from the
// superuser connection string and answers the two role connection strings.
public static class TestConnections
{
    private static readonly ConcurrentDictionary<string, Entry> Entries = new();

    private sealed record Entry(string App, string Migrate);

    private sealed class RoleConnectionStrings : WmsfoConnectionStrings
    {
        public RoleConnectionStrings(string app, string migrate) : base(app, migrate) { }
    }

    internal static void Register(string databaseName, string appConnectionString, string migrateConnectionString)
    {
        Entries[databaseName] = new Entry(appConnectionString, migrateConnectionString);
    }

    internal static void Unregister(string databaseName)
    {
        Entries.TryRemove(databaseName, out _);
    }

    public static WmsfoConnectionStrings For(string superuserConnectionString)
    {
        var databaseName = new NpgsqlConnectionStringBuilder(superuserConnectionString).Database;
        if (string.IsNullOrEmpty(databaseName) || !Entries.TryGetValue(databaseName, out var entry))
        {
            throw new InvalidOperationException(
                $"No PostgresFixture is registered for database '{databaseName}'. Call `TestConnections.For(_fixture.ConnectionString)` after the fixture initializes.");
        }
        return new RoleConnectionStrings(entry.App, entry.Migrate);
    }
}
