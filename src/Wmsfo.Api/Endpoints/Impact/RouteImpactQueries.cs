using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: routes' delete unlinks the events that reference it (FK is set
// null); the preview lists that group so the caller sees it before the delete.
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
        return impact;
    }

    // The FK sets null on delete; ApplyAsync is a no-op.
    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
