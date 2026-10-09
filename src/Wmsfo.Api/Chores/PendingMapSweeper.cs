using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data.Sql;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.Chores;

// api.md 13 / contracts 7.6 / sql.md 9.7: the pending map sweep at 09:00 UTC,
// after the nightly cleanup. Every tracker_map row still pending a day after
// its create loses its open uploads and its objects, then the row itself
// where it is still pending. Every object call has a 3 s budget; a failure
// skips that row until the next run. One log line carries the row count.
public sealed class PendingMapSweeper
{
    public static readonly TimeSpan ObjectCallTimeout = TimeSpan.FromSeconds(3);

    private readonly WmsfoConnectionStrings _connections;
    private readonly IObjectStore _store;
    private readonly ILogger<PendingMapSweeper> _logger;

    public PendingMapSweeper(
        WmsfoConnectionStrings connections,
        IObjectStore store,
        ILogger<PendingMapSweeper> logger)
    {
        _connections = connections;
        _store = store;
        _logger = logger;
    }

    // Answers the number of rows deleted.
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var rows = new List<(long Id, string Prefix)>();
        await using (var read = new NpgsqlCommand(ChoreRecipes.PendingMapSweepRows, conn))
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add((reader.GetInt64(0), reader.GetString(1)));
        }

        var deleted = 0;
        foreach (var (id, prefix) in rows)
        {
            if (prefix != TrackerThemeSeed.ValleyPrefix && !await RemoveObjectsAsync(id, prefix, ct).ConfigureAwait(false))
                continue;
            await using var del = new NpgsqlCommand(ChoreRecipes.PendingMapSweepDelete, conn);
            del.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
            deleted += await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("pending map sweep: rows={Rows}", deleted);
        return deleted;
    }

    // Aborts every open upload and deletes every object under the prefix;
    // false when a call failed, which leaves the row for the next run.
    private async Task<bool> RemoveObjectsAsync(long id, string prefix, CancellationToken ct)
    {
        var under = prefix + "/";
        try
        {
            var uploads = await _store.ListMultipartUploadsAsync(under, ct)
                .WaitAsync(ObjectCallTimeout, ct).ConfigureAwait(false);
            foreach (var upload in uploads)
            {
                await _store.AbortMultipartAsync(upload.Key, upload.UploadId, ct)
                    .WaitAsync(ObjectCallTimeout, ct).ConfigureAwait(false);
            }
            var keys = await ListKeysAsync(under, ct).WaitAsync(ObjectCallTimeout, ct).ConfigureAwait(false);
            foreach (var key in keys)
            {
                await _store.DeleteObjectAsync(key, ct).WaitAsync(ObjectCallTimeout, ct).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "pending map sweep skipped mapId={MapId}", id);
            return false;
        }
    }

    private async Task<List<string>> ListKeysAsync(string prefix, CancellationToken ct)
    {
        var keys = new List<string>();
        await foreach (var entry in _store.ListPrefixAsync(prefix, ct).ConfigureAwait(false)) keys.Add(entry.Key);
        return keys;
    }
}
