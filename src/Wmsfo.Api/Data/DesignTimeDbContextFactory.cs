using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Wmsfo.Api.Data;

// Used by `dotnet ef` so the tooling never touches the runtime configuration.
// A minimal connection string is enough because the tools only need the provider
// wired up; nothing is opened.
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WmsfoDbContext>
{
    public WmsfoDbContext CreateDbContext(string[] args)
    {
        var builder = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql("Host=localhost;Database=wmsfo_design_time;Username=none")
            .UseSnakeCaseNamingConvention();
        return new WmsfoDbContext(builder.Options);
    }
}
