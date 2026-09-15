using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints.Impact;

// A37: preview and audit-before for `DELETE /admin/events/{id}/locations`
// (optionally per beacon). The preview returns one `locations` group per beacon
// with `count` and the beacon's `name`. The audit before is
// `{ count, byBeacon: [{ beaconId, name, count }, ...] }` per contracts 4.5.
public static class LocationClearQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long eventId, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var groups = new List<(long BeaconId, string Name, int Count)>();
        await using (var cmd = new NpgsqlCommand(@"
select l.beacon_id, coalesce(b.name, ''), count(*)::int
from location l
left join beacon b on b.id = l.beacon_id
where l.event_id = $1
group by l.beacon_id, b.name
order by l.beacon_id;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                groups.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }
        foreach (var g in groups)
        {
            impact.Deletes.Add(new ImpactGroupDto
            {
                Entity = "locations",
                Count = g.Count,
                Names = new List<string> { g.Name },
            });
        }
        return impact;
    }

    // The audit before for the delete: total count and per-beacon breakdown,
    // scoped to the optional beaconId filter.
    public static async Task<JsonElement> BeforeAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long eventId, long? beaconId, CancellationToken ct)
    {
        var byBeacon = new List<(long BeaconId, string Name, int Count)>();
        var sql = beaconId is null
            ? @"select l.beacon_id, coalesce(b.name, ''), count(*)::int
from location l
left join beacon b on b.id = l.beacon_id
where l.event_id = $1
group by l.beacon_id, b.name
order by l.beacon_id;"
            : @"select l.beacon_id, coalesce(b.name, ''), count(*)::int
from location l
left join beacon b on b.id = l.beacon_id
where l.event_id = $1 and l.beacon_id = $2
group by l.beacon_id, b.name
order by l.beacon_id;";
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
            if (beaconId is not null)
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = beaconId.Value });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                byBeacon.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2)));
            }
        }
        var total = 0;
        foreach (var b in byBeacon) total += b.Count;
        var payload = new
        {
            count = total,
            byBeacon = byBeacon.Select(b => new
            {
                beaconId = b.BeaconId,
                name = b.Name,
                count = b.Count,
            }).ToArray(),
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, CanonicalJson.Options);
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }
}
