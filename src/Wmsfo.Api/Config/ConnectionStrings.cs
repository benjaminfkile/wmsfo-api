using Npgsql;

namespace Wmsfo.Api.Config;

// sql.md 13: three connection strings built on top of the secret values with
// pool parameters set in code so the secret stays minimal. `App` and `Beacon`
// share the application credentials and differ in `ApplicationName` and pool
// size: the beacon paths (api.md 5) open connections from `Beacon` only, so
// visitor and admin traffic on `App` never starves the location writes.
public class WmsfoConnectionStrings
{
    public string App { get; }
    public string Beacon { get; }
    public string Migrate { get; }

    protected internal WmsfoConnectionStrings(string app, string beacon, string migrate)
    {
        App = app;
        Beacon = beacon;
        Migrate = migrate;
    }

    // Test-only constructor: keeps the pooling defaults out of a shared fixture
    // string and lets unit tests pass a single Postgres address for every role.
    public static WmsfoConnectionStrings ForTests(string appAndMigrate) =>
        new(appAndMigrate, appAndMigrate, appAndMigrate);

    public static WmsfoConnectionStrings Build(WmsfoOptions options)
    {
        var app = AppBuilder(options.DbConnection, options.ServiceName, maxPoolSize: 4);
        var beacon = AppBuilder(options.DbConnection, options.ServiceName + "-beacon", maxPoolSize: 3);

        var migrate = new NpgsqlConnectionStringBuilder(options.DbMigrationConnection)
        {
            ApplicationName = options.ServiceName + "-migrate",
            MaxPoolSize = 2,
            MinPoolSize = 0,
            Timeout = 15,
            CommandTimeout = 300,
        };

        return new WmsfoConnectionStrings(app.ConnectionString, beacon.ConnectionString, migrate.ConnectionString);
    }

    private static NpgsqlConnectionStringBuilder AppBuilder(string connection, string applicationName, int maxPoolSize) =>
        new(connection)
        {
            ApplicationName = applicationName,
            MaxPoolSize = maxPoolSize,
            MinPoolSize = 1,
            ConnectionIdleLifetime = 300,
            ConnectionPruningInterval = 10,
            Timeout = 5,
            CommandTimeout = 10,
            KeepAlive = 30,
            MaxAutoPrepare = 0,
        };
}
