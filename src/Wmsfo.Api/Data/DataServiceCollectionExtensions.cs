using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wmsfo.Api.Node;

namespace Wmsfo.Api.Data;

// api.md 3 step 2 and step 5: the context registrations and the boot migrator,
// shared by Program.cs and the integration hosts.
public static class DataServiceCollectionExtensions
{
    // Both registrations share one DbContextOptions<WmsfoDbContext>, so they must
    // agree: both serve requests on the app connection. Migrations build their own
    // context on the migrate connection inside DatabaseMigrator. The factory
    // registers the options as a singleton; the scoped context registers its
    // options configuration as a singleton too, so the singleton options never
    // reach a scoped service from the root provider (the Development host
    // validates scopes).
    public static IServiceCollection AddWmsfoDbContexts(this IServiceCollection services, string appConnectionString)
    {
        services.AddDbContextFactory<WmsfoDbContext>(o => o
            .UseNpgsql(appConnectionString)
            .UseSnakeCaseNamingConvention());
        services.AddDbContext<WmsfoDbContext>(o => o
            .UseNpgsql(appConnectionString)
            .UseSnakeCaseNamingConvention(),
            optionsLifetime: ServiceLifetime.Singleton);
        return services;
    }

    // The migrator runs on the migrate connection with the registered first boot
    // hook, started by MigrationHostedService.
    public static IServiceCollection AddWmsfoMigrator(this IServiceCollection services, string migrateConnectionString)
    {
        services.AddSingleton<IFirstBootHook, FleetFirstBootHook>();
        services.AddScoped<DatabaseMigrator>(sp => new DatabaseMigrator(
            migrateConnectionString,
            sp.GetRequiredService<IFirstBootHook>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<DatabaseMigrator>()));
        services.AddHostedService<MigrationHostedService>();
        return services;
    }
}
