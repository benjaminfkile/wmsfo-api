using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data.Sql;

namespace Wmsfo.Api.Chores;

// api.md 13 / contracts 7.6 / sql.md 9.5: the six nightly deletes at 09:00
// UTC. Each statement is idempotent (two leaders running it in the same
// minute delete nothing twice); each is logged with its row count.
public sealed class NightlyCleanup
{
    public sealed record CleanupCounts(
        int EnrollmentTokens,
        int Outbox,
        int UnverifiedSubscribers,
        int BeaconLogs,
        int PreviewTokens,
        int StalePendingMedia);

    private readonly WmsfoConnectionStrings _connections;
    private readonly ILogger<NightlyCleanup> _logger;

    public NightlyCleanup(WmsfoConnectionStrings connections, ILogger<NightlyCleanup> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    public async Task<CleanupCounts> RunOnceAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var counts = new CleanupCounts(
            EnrollmentTokens: await ExecuteAsync(ChoreRecipes.NightlyEnrollmentTokens, conn, ct).ConfigureAwait(false),
            Outbox: await ExecuteAsync(ChoreRecipes.NightlyOutbox, conn, ct).ConfigureAwait(false),
            UnverifiedSubscribers: await ExecuteAsync(ChoreRecipes.NightlyUnverifiedSubscribers, conn, ct).ConfigureAwait(false),
            BeaconLogs: await ExecuteAsync(ChoreRecipes.NightlyBeaconLogs, conn, ct).ConfigureAwait(false),
            PreviewTokens: await ExecuteAsync(ChoreRecipes.NightlyPreviewTokens, conn, ct).ConfigureAwait(false),
            StalePendingMedia: await ExecuteAsync(ChoreRecipes.NightlyStalePendingMedia, conn, ct).ConfigureAwait(false));

        _logger.LogInformation(
            "nightly cleanup: tokens={T} outbox={O} unverified={U} beaconLogs={B} previewTokens={P} pendingMedia={M}",
            counts.EnrollmentTokens, counts.Outbox, counts.UnverifiedSubscribers,
            counts.BeaconLogs, counts.PreviewTokens, counts.StalePendingMedia);
        return counts;
    }

    private static async Task<int> ExecuteAsync(string sql, NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
