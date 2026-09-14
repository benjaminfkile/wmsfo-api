using Npgsql;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: api_key is never deleted (contracts 4.5 revoke), but the preview
// endpoint reports an empty impact so the panel can show the resource's page.
public static class ApiKeyImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        await using var read = new NpgsqlCommand(
            "select 1 from api_key where id = $1;", conn, tx);
        read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = id });
        _ = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return impact;
    }
}
