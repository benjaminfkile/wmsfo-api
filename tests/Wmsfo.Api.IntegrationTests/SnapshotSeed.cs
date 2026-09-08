using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.IntegrationTests;

// Shared helper: ensure a content_version row + a snapshot row exist so admin
// [snapshot] writes have something to embed and increment. Idempotent.
internal static class SnapshotSeed
{
    public static async Task EnsureAsync(NpgsqlConnection conn)
    {
        await using (var check = new NpgsqlCommand("select 1 from content_version limit 1;", conn))
        {
            var existing = await check.ExecuteScalarAsync();
            if (existing is null || existing is DBNull)
            {
                var doc = FixtureData.BuildContentDocument();
                var bytes = CanonicalJson.SerializeToUtf8Bytes(doc);
                var sha = CanonicalJson.Sha256Hex(bytes);
                await using var ins = new NpgsqlCommand(@"
insert into content_version (document, sha256, media_ids, label, published_by)
values ($1::jsonb, $2, '{}'::uuid[], 'seed', 'seed');", conn);
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = System.Text.Encoding.UTF8.GetString(bytes) });
                ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Char, Value = sha });
                await ins.ExecuteNonQueryAsync();
            }
        }
        await using (var check = new NpgsqlCommand("select 1 from snapshot where id = 1;", conn))
        {
            var existing = await check.ExecuteScalarAsync();
            if (existing is null || existing is DBNull)
            {
                await using var ins = new NpgsqlCommand(@"
insert into snapshot (id, version, url, s3_key, built_at)
values (1, 1, 'https://cdn.example/snapshots/seed.json', 'snapshots/seed.json', now())
on conflict (id) do nothing;", conn);
                await ins.ExecuteNonQueryAsync();
            }
        }
    }
}
