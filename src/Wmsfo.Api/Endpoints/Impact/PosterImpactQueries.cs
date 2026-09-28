using Npgsql;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: nothing depends on a poster. The preview names the poster
// itself under `deletes` (one group `poster`, count 1, its name) so the panel
// shows what goes.
public static class PosterImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var poster = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "poster", "poster", "name", "id = $1",
            ImpactHelpers.LongId(id), ct).ConfigureAwait(false);
        if (poster is not null) impact.Deletes.Add(poster);
        return impact;
    }

    // Nothing references a poster; ApplyAsync is a no-op.
    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
