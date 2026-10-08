using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data;
using Wmsfo.Api.Email;
using Wmsfo.Api.Help;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Node;

// api.md 3 step 5: after MigrateAsync runs the first-boot steps of sql.md 8.16:
//   1. Starter content - A14
//   2. Icon library - A3 (IconLibrary.EnsureWrittenAsync); we skip when the library
//      is unavailable (test hosts don't ship the icons folder)
//   2a. Help topics (HelpTopics.EnsureWrittenAsync); skipped when the seed is
//      not registered
//   3. Content version 1 - A14 (SnapshotBootstrap uses a fixture stand-in)
//   4. Snapshot version 1 - this task
// Between steps 2a and 4 it writes the email logo (contracts 7.8).
// The migrator holds the advisory lock across the hook.
public sealed class FleetFirstBootHook : IFirstBootHook
{
    private readonly IServiceProvider _services;
    private readonly ILogger<FleetFirstBootHook> _logger;

    public FleetFirstBootHook(IServiceProvider services, ILogger<FleetFirstBootHook> logger)
    {
        _services = services;
        _logger = logger;
    }

    public async Task EnsureAsync(CancellationToken ct)
    {
        using var scope = _services.CreateScope();

        // 2. Icon library - best effort. When the compiled library is not available
        // (dev host without icons/), skip; snapshots use whatever IconLibrary is
        // registered.
        var iconLibrary = scope.ServiceProvider.GetService<IconLibrary>();
        var store = scope.ServiceProvider.GetService<IObjectStore>();
        if (iconLibrary is not null && store is not null)
        {
            var db = scope.ServiceProvider.GetRequiredService<WmsfoDbContext>();
            try
            {
                var wrote = await iconLibrary.EnsureWrittenAsync(store, db, ct).ConfigureAwait(false);
                if (wrote)
                {
                    // api.md 16 / platform.md 10: fires once per deploy when the
                    // compiled library hash differs from icon_library_state.
                    _logger.LogInformation(
                        "icon library written hash={Hash}; marker={Marker}",
                        iconLibrary.LibraryHash, LogMarkers.IconLibraryWritten);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "icon library ensure failed (non-fatal for tests)");
            }
        }

        // 2a. Help topics: bring help_topic in line with help/topics.json. A
        // failure here fails the boot, which retries (sql.md 8.16).
        var helpTopics = scope.ServiceProvider.GetService<HelpTopics>();
        var connections = scope.ServiceProvider.GetService<WmsfoConnectionStrings>();
        if (helpTopics is not null && connections is not null)
        {
            await using var conn = new NpgsqlConnection(connections.Migrate);
            await conn.OpenAsync(ct).ConfigureAwait(false);
            var result = await helpTopics.EnsureWrittenAsync(conn, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "help topics ensured inserted={Inserted} updated={Updated} unchanged={Unchanged} deleted={Deleted}",
                result.Inserted, result.Updated, result.Unchanged, result.Deleted);
        }

        // The bundled email images: templates/email/logo.png, ornaments.png,
        // and lights.png, each at email/{sha256}.png, written when that key is
        // absent (platform.md 1.2). Skipped when the templates are not
        // registered (test hosts without templates/email/).
        var emailTemplates = scope.ServiceProvider.GetService<EmailTemplates>();
        if (emailTemplates is not null && store is not null)
        {
            foreach (var image in emailTemplates.BundledImages)
            {
                try
                {
                    if (await image.EnsureWrittenAsync(store, ct).ConfigureAwait(false))
                    {
                        _logger.LogInformation("email image written key={Key}", image.Key);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "email image ensure failed (non-fatal for tests)");
                }
            }
        }

        // 4. Snapshot version 1 (with the fixture content_version as a stand-in
        // for A14). Skipped when the object store or icon library are unavailable
        // in a test host without the writer wired up.
        var bootstrap = scope.ServiceProvider.GetService<SnapshotBootstrap>();
        if (bootstrap is null) return;
        await bootstrap.EnsureVersionOneAsync(ct).ConfigureAwait(false);
    }
}
