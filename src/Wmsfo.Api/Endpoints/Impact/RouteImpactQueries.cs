using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: routes' delete unlinks the events and the posters that reference
// it (both FKs set null); the preview lists those groups so the caller sees
// them before the delete.
public static class RouteImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var parameters = ImpactHelpers.LongId(id);
        var events = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "event", "event", "name", "route_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (events is not null) impact.Unlinks.Add(events);
        var posters = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "poster", "poster", "name", "route_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (posters is not null) impact.Unlinks.Add(posters);
        return impact;
    }

    // The FKs set null on delete; ApplyAsync is a no-op.
    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
