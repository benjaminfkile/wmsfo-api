using Npgsql;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: contact_message has no dependents; the preview is empty.
public static class ContactMessageImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        await using var read = new NpgsqlCommand(
            "select 1 from contact_message where id = $1;", conn, tx);
        read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint, Value = id });
        _ = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return impact;
    }

    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
