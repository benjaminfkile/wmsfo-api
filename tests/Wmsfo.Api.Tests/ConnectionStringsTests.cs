using Npgsql;
using Wmsfo.Api.Config;

namespace Wmsfo.Api.Tests;

// sql.md 13: two application strings on the same credentials. `App` serves
// visitors, admins, chores, and the health probe with a pool of 4; `Beacon`
// serves the beacon paths with a pool of 3 and its own application name.
public class ConnectionStringsTests
{
    private static WmsfoConnectionStrings Build() => WmsfoConnectionStrings.Build(new WmsfoOptions
    {
        ServiceName = "wmsfo-api-dev",
        DbConnection = "Host=db.example;Port=5432;Database=wmsfo_dev;Username=wmsfo_app_dev;Password=x",
        DbMigrationConnection = "Host=db.example;Port=5432;Database=wmsfo_dev;Username=wmsfo_migrate_dev;Password=y",
    });

    [Fact]
    public void App_pool_is_four_with_the_service_name()
    {
        var app = new NpgsqlConnectionStringBuilder(Build().App);
        Assert.Equal("wmsfo-api-dev", app.ApplicationName);
        Assert.Equal(4, app.MaxPoolSize);
        Assert.Equal(1, app.MinPoolSize);
    }

    [Fact]
    public void Beacon_pool_is_three_with_the_beacon_name_and_the_app_parameters()
    {
        var strings = Build();
        var app = new NpgsqlConnectionStringBuilder(strings.App);
        var beacon = new NpgsqlConnectionStringBuilder(strings.Beacon);

        Assert.Equal("wmsfo-api-dev-beacon", beacon.ApplicationName);
        Assert.Equal(3, beacon.MaxPoolSize);
        Assert.Equal(1, beacon.MinPoolSize);

        // Everything but the name and the maximum matches `App`.
        beacon.ApplicationName = app.ApplicationName;
        beacon.MaxPoolSize = app.MaxPoolSize;
        Assert.Equal(app.ConnectionString, beacon.ConnectionString);
        Assert.Equal("wmsfo_app_dev", beacon.Username);
    }

    [Fact]
    public void Migrate_string_is_unchanged()
    {
        var migrate = new NpgsqlConnectionStringBuilder(Build().Migrate);
        Assert.Equal("wmsfo-api-dev-migrate", migrate.ApplicationName);
        Assert.Equal("wmsfo_migrate_dev", migrate.Username);
        Assert.Equal(2, migrate.MaxPoolSize);
    }
}
