using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data.Sql;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Chores;

// api.md 13 / contracts 7.6 / sql.md 9.6: media orphan collection. The four
// transitions the integration test walks through:
//   (1) ready + not referenced         -> unreferenced_since = now
//   (2) unreferenced_since past 30 d   -> tag every object and mark orphaned
//   (3) orphaned but referenced again  -> untag every object and revive to ready
//   (4) orphaned past 8 d              -> row delete (lifecycle deleted the objects)
// Every S3 call has a 3 s budget; a failure skips that row until the next run.
public sealed class MediaOrphanCollector
{
    public static readonly TimeSpan ObjectCallTimeout = TimeSpan.FromSeconds(3);

    public sealed record OrphanCounts(int Stamped, int Cleared, int Marked, int Revived, int Deleted);

    private readonly WmsfoConnectionStrings _connections;
    private readonly IObjectStore _store;
    private readonly ILogger<MediaOrphanCollector> _logger;

    public MediaOrphanCollector(
        WmsfoConnectionStrings connections,
        IObjectStore store,
        ILogger<MediaOrphanCollector> logger)
    {
        _connections = connections;
        _store = store;
        _logger = logger;
    }

    public async Task<OrphanCounts> RunOnceAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var refs = new List<Guid>();
        await using (var read = new NpgsqlCommand(ChoreRecipes.MediaOrphanRefs, conn))
        await using (var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (!reader.IsDBNull(0)) refs.Add(reader.GetGuid(0));
            }
        }

        int stamped = await ExecuteWithRefsAsync(ChoreRecipes.MediaOrphanStamp, refs, conn, ct).ConfigureAwait(false);
        int cleared = await ExecuteWithRefsAsync(ChoreRecipes.MediaOrphanClear, refs, conn, ct).ConfigureAwait(false);

        // Rows ready + unreferenced_since > 30 days -> orphan them.
        int marked = 0;
        var readyOld = await ReadRowsAsync(ChoreRecipes.MediaOrphanReady, conn, ct, useRefs: false).ConfigureAwait(false);
        foreach (var row in readyOld)
        {
            var tagged = await TagObjectsAsync(row, ObjectTags.Orphaned, ct).ConfigureAwait(false);
            if (!tagged) continue;
            await using var mark = new NpgsqlCommand(ChoreRecipes.MediaOrphanMark, conn);
            mark.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = row.Id });
            var affected = await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected > 0) marked++;
        }

        // Rows orphaned but referenced again -> revive.
        int revived = 0;
        var revive = await ReadRowsAsync(ChoreRecipes.MediaOrphanReviveList, conn, ct, useRefs: true, refs).ConfigureAwait(false);
        foreach (var row in revive)
        {
            var untagged = await UntagObjectsAsync(row, ct).ConfigureAwait(false);
            if (!untagged) continue;
            await using var upd = new NpgsqlCommand(ChoreRecipes.MediaOrphanRevive, conn);
            upd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid) { Value = row.Id });
            var affected = await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected > 0) revived++;
        }

        // Rows orphaned more than 8 days ago -> row delete (objects gone by lifecycle).
        int deleted;
        await using (var del = new NpgsqlCommand(ChoreRecipes.MediaOrphanDelete, conn))
        {
            deleted = await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var counts = new OrphanCounts(stamped, cleared, marked, revived, deleted);
        _logger.LogInformation(
            "media orphan chore: stamped={S} cleared={C} marked={M} revived={R} deleted={D}",
            counts.Stamped, counts.Cleared, counts.Marked, counts.Revived, counts.Deleted);
        return counts;
    }

    private static async Task<int> ExecuteWithRefsAsync(string sql, IReadOnlyList<Guid> refs, NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter("refs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = refs.ToArray() });
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task<List<OrphanRow>> ReadRowsAsync(string sql, NpgsqlConnection conn, CancellationToken ct, bool useRefs, IReadOnlyList<Guid>? refs = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        if (useRefs)
        {
            cmd.Parameters.Add(new NpgsqlParameter("refs", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = (refs ?? Array.Empty<Guid>()).ToArray() });
        }
        var rows = new List<OrphanRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var id = reader.GetGuid(0);
            var s3Key = reader.GetString(1);
            var variantsJson = reader.GetString(2);
            var variantKeys = ParseVariantKeys(id, s3Key, variantsJson);
            rows.Add(new OrphanRow(id, s3Key, variantKeys));
        }
        return rows;
    }

    private async Task<bool> TagObjectsAsync(OrphanRow row, string tag, CancellationToken ct)
    {
        foreach (var key in Enumerable.Repeat(row.S3Key, 1).Concat(row.VariantKeys))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ObjectCallTimeout);
                await _store.PutObjectTaggingAsync(key, tag, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "orphan tag {Tag} failed for media {Id} key {Key}; skipping row", tag, row.Id, key);
                return false;
            }
        }
        return true;
    }

    private async Task<bool> UntagObjectsAsync(OrphanRow row, CancellationToken ct)
    {
        foreach (var key in Enumerable.Repeat(row.S3Key, 1).Concat(row.VariantKeys))
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(ObjectCallTimeout);
                await _store.DeleteObjectTaggingAsync(key, cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "orphan untag failed for media {Id} key {Key}; skipping row", row.Id, key);
                return false;
            }
        }
        return true;
    }

    // media_asset.variants is jsonb like {"480": "https://cdn.../media/<id>/w480.webp"}.
    // We tag the object at its bucket key, not the CDN url, so pull the width
    // out of the map and rebuild the key from the media prefix.
    private static IReadOnlyList<string> ParseVariantKeys(Guid mediaId, string originalKey, string variantsJson)
    {
        var keys = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(variantsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return keys;
            var prefix = "media/" + mediaId.ToString("D") + "/";
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (int.TryParse(prop.Name, out var width))
                {
                    keys.Add(prefix + "w" + width + ".webp");
                }
            }
        }
        catch (JsonException)
        {
            // ignore malformed variants json
        }
        return keys;
    }

    private sealed record OrphanRow(Guid Id, string S3Key, IReadOnlyList<string> VariantKeys);
}
