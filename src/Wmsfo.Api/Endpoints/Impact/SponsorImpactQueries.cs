using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: sponsor cascades its years already (FK Cascade on
// sponsor_year.sponsor_id); the preview lists that group so the caller sees it.
public static class SponsorImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var parameters = ImpactHelpers.LongId(id);
        var years = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "sponsor_year", "sponsor_year", "sponsor_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (years is not null) impact.Deletes.Add(years);
        return impact;
    }

    // The cascade covers sponsor_year; ApplyAsync is a no-op.
    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
