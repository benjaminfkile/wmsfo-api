using Npgsql;

namespace Wmsfo.Api.Config;

// sql.md 13: two connection strings built on top of the secret values with pool
// parameters set in code so the secret stays minimal.
public sealed class WmsfoConnectionStrings
{
    public string App { get; }
    public string Migrate { get; }

    private WmsfoConnectionStrings(string app, string migrate)
    {
        App = app;
        Migrate = migrate;
    }

    public static WmsfoConnectionStrings Build(WmsfoOptions options)
    {
        var app = new NpgsqlConnectionStringBuilder(options.DbConnection)
        {
            ApplicationName = options.ServiceName,
            MaxPoolSize = 10,
            MinPoolSize = 1,
            ConnectionIdleLifetime = 300,
            ConnectionPruningInterval = 10,
            Timeout = 5,
            CommandTimeout = 10,
            KeepAlive = 30,
            MaxAutoPrepare = 0,
        };

        var migrate = new NpgsqlConnectionStringBuilder(options.DbMigrationConnection)
        {
            ApplicationName = options.ServiceName + "-migrate",
            MaxPoolSize = 2,
            MinPoolSize = 0,
            Timeout = 15,
            CommandTimeout = 300,
        };

        return new WmsfoConnectionStrings(app.ConnectionString, migrate.ConnectionString);
    }
}
