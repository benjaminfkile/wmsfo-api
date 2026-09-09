using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data.Sql;
using Wmsfo.Api.Email;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Chores;

// api.md 13 / contracts 7.6 / sql.md 9.3: the alert-send chore. Reads up to
// 5 * WMSFO_ALERT_SEND_PER_SEC unsent deliveries oldest first, sends each
// through SesSender under a RateLimiter of WMSFO_ALERT_SEND_PER_SEC per
// second, updates each row with sent_at or attempts + 1 and last_error.
public sealed class AlertSender
{
    private readonly WmsfoConnectionStrings _connections;
    private readonly WmsfoOptions _options;
    private readonly ISesSender _sender;
    private readonly ILogger<AlertSender> _logger;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public AlertSender(
        WmsfoConnectionStrings connections,
        WmsfoOptions options,
        ISesSender sender,
        ILogger<AlertSender> logger)
    {
        _connections = connections;
        _options = options;
        _sender = sender;
        _logger = logger;
        // Refill one token per (1000/N) ms so the average holds even when the
        // batch delivers all rows in one burst.
        var perSec = Math.Max(1, options.AlertSendPerSec);
        var interval = TimeSpan.FromMilliseconds(Math.Max(1, 1000 / perSec));
        _rateLimiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = perSec,
            QueueLimit = perSec * 10,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            ReplenishmentPeriod = interval,
            TokensPerPeriod = 1,
            AutoReplenishment = true,
        });
    }

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var batch = 5 * Math.Max(1, _options.AlertSendPerSec);
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var rows = new List<DeliveryRow>();
        await using (var claim = new NpgsqlCommand(ChoreRecipes.AlertDeliveryClaim, conn))
        {
            claim.Parameters.Add(new NpgsqlParameter("batch", NpgsqlDbType.Integer) { Value = batch });
            await using var reader = await claim.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(new DeliveryRow(
                    Id: reader.GetInt64(0),
                    OutboxId: reader.GetInt64(1),
                    Attempts: reader.GetInt32(2),
                    Address: reader.GetString(3),
                    UnsubscribeToken: reader.GetString(4),
                    Topic: reader.GetString(5),
                    Payload: reader.GetString(6)));
            }
        }

        int sent = 0;
        foreach (var row in rows)
        {
            using var lease = await _rateLimiter.AcquireAsync(1, ct).ConfigureAwait(false);
            if (!lease.IsAcquired) continue;
            try
            {
                var message = BuildMessage(row);
                var messageId = await _sender.SendAsync(message, ct).ConfigureAwait(false);
                await MarkSuccessAsync(row.Id, messageId, conn, ct).ConfigureAwait(false);
                sent++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "alert delivery {Id} attempt {Attempt} failed: {Error}", row.Id, row.Attempts + 1, ex.Message);
                await MarkFailureAsync(row.Id, ex.Message, conn, ct).ConfigureAwait(false);
                if (row.Attempts + 1 >= 5)
                {
                    _logger.LogWarning(
                        "alert delivery {Id} exhausted after {Attempts} attempts; marker={Marker}",
                        row.Id, row.Attempts + 1, LogMarkers.AlertExhausted);
                }
            }
        }
        return sent;
    }

    private SesMessage BuildMessage(DeliveryRow row)
    {
        using var payload = JsonDocument.Parse(row.Payload);
        var unsubscribeUrl = _options.SiteBaseUrl.TrimEnd('/') + "/alerts/unsubscribe?token=" + Uri.EscapeDataString(row.UnsubscribeToken);
        var apiUnsubscribe = _options.PublicApiBaseUrl.TrimEnd('/') + "/subscriptions/unsubscribe?token=" + Uri.EscapeDataString(row.UnsubscribeToken);

        return row.Topic switch
        {
            "event.status_changed" => BuildStatusChanged(row, payload.RootElement, unsubscribeUrl, apiUnsubscribe),
            "event.message_posted" => BuildMessagePosted(row, payload.RootElement, unsubscribeUrl, apiUnsubscribe),
            _ => throw new InvalidOperationException($"Unsupported alert topic: {row.Topic}"),
        };
    }

    private SesMessage BuildStatusChanged(DeliveryRow row, JsonElement payload, string unsubscribeUrl, string apiUnsubscribe)
    {
        int toStatus = payload.GetProperty("toStatusId").GetInt32();
        long eventId = payload.GetProperty("eventId").GetInt64();
        var eventName = LookupEventName(eventId);
        var scheduledAt = LookupEventScheduledAt(eventId);
        var template = toStatus == 3 ? EmailTemplates.EventLive : EmailTemplates.EventScheduled;
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eventName"] = eventName,
            ["scheduledAt"] = scheduledAt is null ? "" : EmailTemplates.FormatMountainTime(scheduledAt.Value),
            ["siteUrl"] = _options.SiteBaseUrl,
            ["unsubscribeUrl"] = unsubscribeUrl,
        };
        return new SesMessage(
            TemplateName: template,
            ToAddress: row.Address,
            Values: values,
            UnsubscribeUrl: apiUnsubscribe);
    }

    private SesMessage BuildMessagePosted(DeliveryRow row, JsonElement payload, string unsubscribeUrl, string apiUnsubscribe)
    {
        long eventId = payload.GetProperty("eventId").GetInt64();
        long messageId = payload.GetProperty("messageId").GetInt64();
        var eventName = LookupEventName(eventId);
        var body = LookupMessageBody(messageId) ?? "";
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["eventName"] = eventName,
            ["messageBody"] = body,
            ["messagePreview"] = EmailTemplates.SubjectPreview(body),
            ["siteUrl"] = _options.SiteBaseUrl,
            ["unsubscribeUrl"] = unsubscribeUrl,
        };
        return new SesMessage(
            TemplateName: EmailTemplates.EventMessage,
            ToAddress: row.Address,
            Values: values,
            UnsubscribeUrl: apiUnsubscribe);
    }

    private string LookupEventName(long eventId)
    {
        using var conn = new NpgsqlConnection(_connections.App);
        conn.Open();
        using var cmd = new NpgsqlCommand("select name from event where id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        var r = cmd.ExecuteScalar();
        return r is string s ? s : "Santa tracker";
    }

    private DateTimeOffset? LookupEventScheduledAt(long eventId)
    {
        using var conn = new NpgsqlConnection(_connections.App);
        conn.Open();
        using var cmd = new NpgsqlCommand("select scheduled_at from event where id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        var r = cmd.ExecuteScalar();
        return r is DateTime dt
            ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc))
            : r is DateTimeOffset dto ? dto : (DateTimeOffset?)null;
    }

    private string? LookupMessageBody(long messageId)
    {
        using var conn = new NpgsqlConnection(_connections.App);
        conn.Open();
        using var cmd = new NpgsqlCommand("select body from event_message where id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = messageId });
        var r = cmd.ExecuteScalar();
        return r as string;
    }

    private static async Task MarkSuccessAsync(long id, string messageId, NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(ChoreRecipes.AlertDeliverySuccess, conn);
        cmd.Parameters.Add(new NpgsqlParameter("ses_message_id", NpgsqlDbType.Text) { Value = messageId });
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task MarkFailureAsync(long id, string error, NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(ChoreRecipes.AlertDeliveryFailure, conn);
        cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = error.Length > 500 ? error[..500] : error });
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record DeliveryRow(
        long Id,
        long OutboxId,
        int Attempts,
        string Address,
        string UnsubscribeToken,
        string Topic,
        string Payload);
}
