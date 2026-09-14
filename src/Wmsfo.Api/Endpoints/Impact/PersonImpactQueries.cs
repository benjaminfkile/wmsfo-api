using Npgsql;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: person cascades its cookies and its subscribers (FK Cascade on
// both); the preview lists both groups.
public static class PersonImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var parameters = ImpactHelpers.LongId(id);

        var subscribers = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "subscriber", "subscriber", "address", "person_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (subscribers is not null) impact.Deletes.Add(subscribers);

        var cookies = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "cookie", "cookie", "person_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (cookies is not null) impact.Deletes.Add(cookies);

        return impact;
    }

    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
