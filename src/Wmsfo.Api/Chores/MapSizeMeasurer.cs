using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data.Sql;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Chores;

// api.md 13 / contracts 7.6 / sql.md 9.7: the map size measure, once at
// leader start and nightly after the pending map sweep. For every
// tracker_map row in state ready it HEADs prefix/tiles.pmtiles and, when
// terrain_max_zoom is set, prefix/terrain.pmtiles, and writes tiles_bytes
// and terrain_bytes where the measured length differs from the stored one.
// A missing object or a failed call keeps the stored value and logs one
// Warning naming the map and the file; nothing else on the row changes and
// no object is written. Every object call has a 3 s budget. One log line
// carries the counts.
public sealed class MapSizeMeasurer
{
    public static readonly TimeSpan ObjectCallTimeout = TimeSpan.FromSeconds(3);

    private readonly WmsfoConnectionStrings _connections;
    private readonly IObjectStore _store;
    private readonly ILogger<MapSizeMeasurer> _logger;

    public MapSizeMeasurer(
        WmsfoConnectionStrings connections,
        IObjectStore store,
        ILogger<MapSizeMeasurer> logger)
    {
        _connections = connections;
        _store = store;
        _logger = logger;
    }

    // Answers the number of rows updated.
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var rows = new List<(long Id, string Prefix, bool HasTerrain, long? Tiles, long? Terrain)>();
        await using (var read = new NpgsqlCommand(ChoreRecipes.MapSizeMeasureRows, conn))
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetString(1),
                    !reader.IsDBNull(2),
                    reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    reader.IsDBNull(4) ? null : reader.GetInt64(4)));
            }
        }

        var updated = 0;
        var missing = 0;
        foreach (var row in rows)
        {
            var tiles = await MeasureAsync(row.Id, row.Prefix, "tiles.pmtiles", ct).ConfigureAwait(false);
            if (tiles is null) missing++;
            long? terrain = null;
            if (row.HasTerrain)
            {
                terrain = await MeasureAsync(row.Id, row.Prefix, "terrain.pmtiles", ct).ConfigureAwait(false);
                if (terrain is null) missing++;
            }

            var changed = (tiles is not null && tiles != row.Tiles) || (terrain is not null && terrain != row.Terrain);
            if (!changed) continue;

            await using var update = new NpgsqlCommand(ChoreRecipes.MapSizeMeasureUpdate, conn);
            update.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = row.Id });
            update.Parameters.Add(new NpgsqlParameter("tiles", NpgsqlDbType.Bigint) { Value = (object?)tiles ?? DBNull.Value });
            update.Parameters.Add(new NpgsqlParameter("terrain", NpgsqlDbType.Bigint) { Value = (object?)terrain ?? DBNull.Value });
            updated += await update.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("map size measure: maps={Maps} updated={Updated} missing={Missing}",
            rows.Count, updated, missing);
        return updated;
    }

    // The archive's length from a HEAD; null when the object is missing or
    // the call failed, after one Warning naming the map and the file.
    private async Task<long?> MeasureAsync(long id, string prefix, string file, CancellationToken ct)
    {
        try
        {
            var head = await _store.HeadObjectAsync(prefix + "/" + file, ct)
                .WaitAsync(ObjectCallTimeout, ct).ConfigureAwait(false);
            if (head is not null) return head.ContentLength;
            _logger.LogWarning("map size measure: object missing mapId={MapId} file={File}", id, file);
            return null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "map size measure: head failed mapId={MapId} file={File}", id, file);
            return null;
        }
    }
}
