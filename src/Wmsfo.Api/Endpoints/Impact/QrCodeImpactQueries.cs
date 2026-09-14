using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: qr_code cascades qr_attachment and qr_scan already (FK Cascade
// on qr_attachment.qr_code_id and qr_scan.qr_code_id). The preview lists both
// groups so the caller sees the reach.
public static class QrCodeImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var parameters = ImpactHelpers.LongId(id);

        var attachments = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "qr_attachment", "qr_attachment", "qr_code_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (attachments is not null) impact.Deletes.Add(attachments);

        var scans = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "qr_scan", "qr_scan", "qr_code_id = $1",
            parameters, ct).ConfigureAwait(false);
        if (scans is not null) impact.Deletes.Add(scans);

        return impact;
    }

    public static Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long id, CancellationToken ct)
        => Task.CompletedTask;
}
