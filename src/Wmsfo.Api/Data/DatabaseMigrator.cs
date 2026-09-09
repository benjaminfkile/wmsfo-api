using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Wmsfo.Api.Data;

// sql.md 14.1: advisory lock on the migrate connection, MigrateAsync, then the
// first-boot hook (populated by later tasks). Retries with 5 s, 10 s, 30 s, then
// 60 s while health stays 503.
public sealed class DatabaseMigrator
{
    public const string LockName = "wmsfo_migrations";
    public static readonly TimeSpan[] RetryDelays =
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    };

    private readonly string _migrateConnectionString;
    private readonly DbContextOptions<WmsfoDbContext> _migrateOptions;
    private readonly ILogger<DatabaseMigrator> _logger;
    private readonly IFirstBootHook _firstBoot;

    public DatabaseMigrator(
        string migrateConnectionString,
        IFirstBootHook firstBoot,
        ILogger<DatabaseMigrator> logger)
    {
        _migrateConnectionString = migrateConnectionString;
        // The migration context is built here from the migrate connection string and
        // never through the shared DbContext registrations: those share one
        // DbContextOptions<WmsfoDbContext> and resolve to the app connection, whose
        // role has no DDL rights (sql.md 12, 13).
        _migrateOptions = new DbContextOptionsBuilder<WmsfoDbContext>()
            .UseNpgsql(migrateConnectionString)
            .UseSnakeCaseNamingConvention()
            .Options;
        _firstBoot = firstBoot;
        _logger = logger;
    }

    // The context every migration runs through; opens on the migrate role.
    public WmsfoDbContext CreateMigrationContext() => new(_migrateOptions);

    public async Task MigrateAsync(CancellationToken ct)
    {
        await using var lockConnection = new NpgsqlConnection(_migrateConnectionString);
        await lockConnection.OpenAsync(ct);
        await using (var acquire = new NpgsqlCommand($"select pg_advisory_lock(hashtext('{LockName}'))", lockConnection))
        {
            await acquire.ExecuteNonQueryAsync(ct);
        }
        try
        {
            await using var db = CreateMigrationContext();
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            if (pending.Count > 0)
            {
                _logger.LogInformation("Applying {Count} pending migration(s).", pending.Count);
                await db.Database.MigrateAsync(ct);
            }
            await _firstBoot.EnsureAsync(ct);
        }
        finally
        {
            await using var release = new NpgsqlCommand($"select pg_advisory_unlock(hashtext('{LockName}'))", lockConnection);
            await release.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    public async Task RunWithRetryAsync(Action markReady, CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await MigrateAsync(ct);
                markReady();
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var delay = RetryDelays[Math.Min(attempt, RetryDelays.Length - 1)];
                _logger.LogWarning(ex, "Migration attempt failed; retrying in {Delay}.", delay);
                attempt++;
                try { await Task.Delay(delay, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }
}

// The first-boot steps of sql.md 8.16 land in later tasks; keeping this hook here
// lets the migrator stay stable.
public interface IFirstBootHook
{
    Task EnsureAsync(CancellationToken ct);
}

public sealed class NoOpFirstBootHook : IFirstBootHook
{
    public Task EnsureAsync(CancellationToken ct) => Task.CompletedTask;
}

// api.md 3 steps 5 and 8: runs on a background task after the pipeline starts
// so /api/health can answer 503 while the migration is in flight and 200 after
// the readiness gate flips.
public sealed class MigrationHostedService : BackgroundService
{
    private readonly IServiceProvider _services;

    public MigrationHostedService(IServiceProvider services)
    {
        _services = services;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Break the sync path so Kestrel starts listening before the first
        // migrate attempt runs; a slow migration must not delay step 4.
        return Task.Run(async () =>
        {
            using var scope = _services.CreateScope();
            var migrator = scope.ServiceProvider.GetRequiredService<DatabaseMigrator>();
            var gate = _services.GetRequiredService<Wmsfo.Api.Config.WmsfoReadinessGate>();
            await migrator.RunWithRetryAsync(gate.MarkReady, stoppingToken);
        }, stoppingToken);
    }
}

