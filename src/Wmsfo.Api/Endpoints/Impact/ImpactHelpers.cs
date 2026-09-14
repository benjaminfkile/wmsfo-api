using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: names in an impact group are the first ten rows by id, using
// the field the resource has (`name`, `title`, `tag`, `email`, or `filename`).
// counts are exact. `CountAndNamesAsync` runs one count and one names query on
// the same predicate so the two agree.
public static class ImpactHelpers
{
    public const int NamesCap = 10;

    // The fixed warning sentences (api.md 5b).
    public const string PageRoleWarning = "This page holds the {0} role; pick the page that takes it.";
    public const string BeaconActiveWarning = "This beacon is active; the live feed stops.";
    public const string CookieTallyWarning = "An event is live; its cookie tally drops by {0}.";
    public const string LiveEventBlocked = "This event is live. End it first.";
    public const string CurrentEventBlocked = "This is the current event. Make another event current first.";

    // Runs a `count(*)` plus a `select <nameCol> order by id limit 10` on the
    // same table with the same where clause and parameters, and returns them as
    // an ImpactGroupDto. If count is zero, returns null so the caller can drop
    // the group.
    public static async Task<ImpactGroupDto?> CountAndNamesAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx,
        string entity, string table, string nameCol, string whereSql,
        NpgsqlParameter[] parameters, CancellationToken ct)
    {
        int count;
        await using (var cmd = new NpgsqlCommand(
            $"select count(*)::int from {table} where {whereSql};", conn, tx))
        {
            foreach (var p in parameters) cmd.Parameters.Add(Clone(p));
            var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            count = r is int i ? i : Convert.ToInt32(r);
        }
        if (count == 0) return null;

        var names = new List<string>();
        await using (var cmd = new NpgsqlCommand(
            $"select {nameCol} from {table} where {whereSql} order by id limit {NamesCap};", conn, tx))
        {
            foreach (var p in parameters) cmd.Parameters.Add(Clone(p));
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                names.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
            }
        }
        return new ImpactGroupDto { Entity = entity, Count = count, Names = names };
    }

    // Shared count-only builder (some groups have no useful name column).
    public static async Task<ImpactGroupDto?> CountOnlyAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx,
        string entity, string table, string whereSql,
        NpgsqlParameter[] parameters, CancellationToken ct)
    {
        int count;
        await using var cmd = new NpgsqlCommand(
            $"select count(*)::int from {table} where {whereSql};", conn, tx);
        foreach (var p in parameters) cmd.Parameters.Add(Clone(p));
        var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        count = r is int i ? i : Convert.ToInt32(r);
        if (count == 0) return null;
        return new ImpactGroupDto { Entity = entity, Count = count, Names = new List<string>() };
    }

    private static NpgsqlParameter Clone(NpgsqlParameter p) => new()
    {
        NpgsqlDbType = p.NpgsqlDbType,
        Value = p.Value,
    };

    // A parameter list that borrows just the resource id as `$1`.
    public static NpgsqlParameter[] LongId(long id) => new[]
    {
        new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id },
    };

    public static NpgsqlParameter[] UuidId(Guid id) => new[]
    {
        new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id },
    };

    public static NpgsqlParameter[] TextId(string id) => new[]
    {
        new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = id },
    };
}
