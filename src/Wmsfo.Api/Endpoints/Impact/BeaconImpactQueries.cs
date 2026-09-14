using Npgsql;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: beacons are never deleted (contracts 4.5 revoke, sql.md 3.6);
// PreviewAsync is provided for completeness and warns about the active flag.
// The endpoints do not offer a delete; the preview endpoint answers 200 with
// counts of beacon_enrollment_token and beacon_log so the panel can show them.
public static class BeaconImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        bool exists = false;
        bool isActive = false;
        await using (var read = new NpgsqlCommand(
            "select is_active from beacon where id = $1;", conn, tx))
        {
            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = id });
            var r = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (r is not null && r is not DBNull)
            {
                exists = true;
                isActive = (bool)r;
            }
        }
        if (!exists) return impact;

        if (isActive) impact.Warnings.Add(ImpactHelpers.BeaconActiveWarning);

        var parameters = ImpactHelpers.LongId(id);
        var logs = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "beacon_log", "beacon_log", "beacon_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (logs is not null) impact.Deletes.Add(logs);

        var tokens = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "beacon_enrollment_token", "beacon_enrollment_token", "beacon_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (tokens is not null) impact.Deletes.Add(tokens);

        var locations = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "location", "location", "beacon_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (locations is not null) impact.Deletes.Add(locations);

        return impact;
    }
}
