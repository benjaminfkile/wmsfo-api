using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Node;

// api.md 10.2 / sql.md 8.16: full first-boot order. Called by DatabaseMigrator
// under the migration advisory lock, after EF migrations apply. The four steps
// are idempotent; already-done steps skip:
//   1. Starter content — when `page` is empty, seed the working set from
//      contracts/starter-content.json.
//   2. Icon library — handled by FleetFirstBootHook (A3).
//   3. Content version 1 — when `content_version` is empty, publish the working
//      set (Publisher.EnsureVersionOneAsync) without a snapshot rebuild.
//   4. Snapshot version 1 — when the single snapshot row is absent, build the
//      snapshot object, PUT it, and insert (id=1, version=1).
public sealed class SnapshotBootstrap
{
    private readonly SnapshotBuilder _snapshotBuilder;
    private readonly WmsfoConnectionStrings _connections;
    private readonly WmsfoOptions _options;
    private readonly ILogger<SnapshotBootstrap> _logger;
    private readonly StarterContent? _starterContent;
    private readonly Publisher? _publisher;

    public SnapshotBootstrap(
        SnapshotBuilder snapshotBuilder,
        WmsfoConnectionStrings connections,
        WmsfoOptions options,
        ILogger<SnapshotBootstrap> logger,
        StarterContent? starterContent = null,
        Publisher? publisher = null)
    {
        _snapshotBuilder = snapshotBuilder;
        _connections = connections;
        _options = options;
        _logger = logger;
        _starterContent = starterContent;
        _publisher = publisher;
    }

    // Runs the four steps in order. Uses the migrate role (the migrator holds
    // the advisory lock).
    public async Task EnsureVersionOneAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.Migrate);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // Step 1: starter content when the working set is empty. Falls back to
        // an empty seed when StarterContent is not available (test hosts that
        // wire SnapshotBootstrap directly).
        if (_starterContent is not null)
        {
            await _starterContent.EnsureSeededAsync(conn, ct).ConfigureAwait(false);
        }

        // Step 3: content_version row when the table is empty. A14: publish the
        // starter content proper. When Publisher is not registered (older test
        // hosts), fall back to the fixture stand-in so the pipeline is testable
        // end to end.
        if (_publisher is not null)
        {
            await _publisher.EnsureVersionOneAsync(conn, ct).ConfigureAwait(false);
        }
        else
        {
            await EnsureFixtureContentVersionAsync(conn, ct).ConfigureAwait(false);
        }

        // Step 4: snapshot row when absent.
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

    // Fallback used when the Publisher isn't wired up (older tests only). Keeps
    // A7's SnapshotBootstrap ctor working: insert the fixture ContentDocument
    // as content_version 1 so SnapshotBuilder finds a row to embed.
    private async Task EnsureFixtureContentVersionAsync(NpgsqlConnection conn, CancellationToken ct)
    {
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
