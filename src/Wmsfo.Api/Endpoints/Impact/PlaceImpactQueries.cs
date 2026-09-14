using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: place delete already cascades its subtree (FK Cascade on
// place.parent_id); attachments in the subtree keep their history rows but
// their place_id is set null (FK SetNull). The preview lists both groups so
// the caller sees the reach of the delete.
public static class PlaceImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var parameters = ImpactHelpers.LongId(id);

        // The subtree of places (self + descendants) that will go with the row.
        int subtreeCount = 0;
        var childNames = new List<string>();
        await using (var cmd = new NpgsqlCommand(@"
with recursive subtree(id, name) as (
  select id, name from place where id = $1
  union all
  select p.id, p.name from place p join subtree s on p.parent_id = s.id
)
select id, name from subtree;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                subtreeCount++;
                if (childNames.Count < ImpactHelpers.NamesCap && reader.GetInt64(0) != id)
                    childNames.Add(reader.IsDBNull(1) ? "" : reader.GetString(1));
            }
        }
        var descendantCount = subtreeCount - 1;
        if (descendantCount > 0)
        {
            impact.Deletes.Add(new ImpactGroupDto
            {
                Entity = "place", Count = descendantCount, Names = childNames,
            });
        }

        // Open attachments in the subtree; every stay keeps its history row
        // (place_id set null). Count by qr_code tag.
        var openAttachments = new List<string>();
        int openCount = 0;
        await using (var cmd = new NpgsqlCommand(@"
with recursive subtree(id) as (
  select id from place where id = $1
  union all
  select p.id from place p join subtree s on p.parent_id = s.id
)
select coalesce(c.tag, '') from qr_attachment a
join qr_code c on c.id = a.qr_code_id
where a.to_at is null and a.place_id in (select id from subtree)
order by a.id
limit $2;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = ImpactHelpers.NamesCap });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                openAttachments.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        }
        await using (var cmd = new NpgsqlCommand(@"
with recursive subtree(id) as (
  select id from place where id = $1
  union all
  select p.id from place p join subtree s on p.parent_id = s.id
)
select count(*)::int from qr_attachment a
where a.to_at is null and a.place_id in (select id from subtree);", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            openCount = r is int i ? i : System.Convert.ToInt32(r);
        }
        if (openCount > 0)
        {
            impact.Unlinks.Add(new ImpactGroupDto
            {
                Entity = "qr_attachment", Count = openCount, Names = openAttachments,
            });
        }
        return impact;
    }

    // ApplyAsync closes every open attachment in the subtree (the endpoint
    // already does this); the FK cascade removes the descendant place rows.
    public static async Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
with recursive subtree(id) as (
  select id from place where id = $1
  union all
  select p.id from place p join subtree s on p.parent_id = s.id
)
update qr_attachment set to_at = now()
where to_at is null and place_id in (select id from subtree);", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
