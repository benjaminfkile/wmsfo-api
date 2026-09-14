using Npgsql;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: subscriber cascades alert_delivery (FK Cascade). The preview
// lists that group so the caller sees it.
public static class SubscriberImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var parameters = ImpactHelpers.LongId(id);

        var deliveries = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "alert_delivery", "alert_delivery", "subscriber_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (deliveries is not null) impact.Deletes.Add(deliveries);
        return impact;
    }

    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
