using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Node;

// api.md 10.2 / sql.md 8.16 step 4: build snapshot version 1 on first boot when
// the snapshot table is empty. Runs under the migration advisory lock (held by
// DatabaseMigrator). The starter content and content_version 1 are A14; until
// they land the bootstrap ensures a content_version row exists by inserting the
// fixture ContentDocument so the pipeline is testable end to end.
public sealed class SnapshotBootstrap
{
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly WmsfoConnectionStrings _connections;
    private readonly WmsfoOptions _options;
    private readonly ILogger<SnapshotBootstrap> _logger;

    public SnapshotBootstrap(
        SnapshotBuilder snapshotBuilder,
        WmsfoConnectionStrings connections,
        WmsfoOptions options,
        ILogger<SnapshotBootstrap> logger)
    {
        _snapshotBuilder = snapshotBuilder;
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    // sql.md 8.16: run four idempotent steps under the migration lock (the caller
    // holds it). Steps that are already done are skipped. Steps 1 (starter
    // content) and 3 (content_version 1) belong to A14; step 2 (icon library) is
    // A3. Here we ensure a content_version row exists (fixture stand-in) and then
    // insert snapshot version 1.
    public async Task EnsureVersionOneAsync(CancellationToken ct)
    {
        // Migration ran against the migrate connection; we use the migrate role
        // here too so bootstrap does not need the app role's grants on writes.
        await using var conn = new NpgsqlConnection(_connections.Migrate);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // sql.md 8.16 step 3 stand-in: insert a content_version row from the
        // fixture ContentDocument so SnapshotBuilder has something to embed.
        await EnsureContentVersionOneAsync(conn, ct).ConfigureAwait(false);

        // sql.md 8.16 step 4: check if snapshot row exists.
        var exists = await SnapshotRowExistsAsync(conn, ct).ConfigureAwait(false);
        if (exists) return;

        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        var built = await _snapshotBuilder.BuildAndPutAsync(conn, tx, ct).ConfigureAwait(false);
        await using (var insert = new NpgsqlCommand(@"
insert into snapshot (id, version, url, s3_key, built_at)
values (1, 1, $1, $2, now());", conn, tx))
        {
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = built.Url });
            insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = built.Key });
            await insert.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation(
            "snapshot version 1 written: key={Key} url={Url}", built.Key, built.Url);
    }

    private static async Task<bool> SnapshotRowExistsAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select 1 from snapshot where id = 1;", conn);
        var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return r is not null;
    }

    private async Task EnsureContentVersionOneAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        // Only insert if no content_version exists (A14 will replace this with
        // the real seed + publish).
        long? existing;
        await using (var check = new NpgsqlCommand(
            "select id from content_version order by id desc limit 1;", conn))
        {
            var r = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
            existing = r is null || r is DBNull ? null : (long?)Convert.ToInt64(r);
        }
        if (existing is not null) return;

        var document = FixtureData.BuildContentDocument();
        var bytes = CanonicalJson.SerializeToUtf8Bytes(document);
        var sha = CanonicalJson.Sha256Hex(bytes);
        var documentJson = System.Text.Encoding.UTF8.GetString(bytes);

        await using var cmd = new NpgsqlCommand(@"
insert into content_version (document, sha256, media_ids, label, published_by)
values ($1::jsonb, $2, '{}'::uuid[], $3, 'seed');", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = documentJson });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)"Starter content" ?? DBNull.Value });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("bootstrap inserted fixture content_version");
    }
}
