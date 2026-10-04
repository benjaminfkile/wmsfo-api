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
// second, updates each row with sent_at or attempts + 1 and last_error. Each
// batch resolves the layout's logo and site name once through
// EmailLogoResolver (the bundled logo and DefaultSiteName without one).
public sealed class AlertSender
{
    private readonly WmsfoConnectionStrings _connections;
    private readonly WmsfoOptions _options;
    private readonly ISesSender _sender;
    private readonly ILogger<AlertSender> _logger;
    private readonly EmailLogoResolver? _logo;
    private readonly TokenBucketRateLimiter _rateLimiter;

    public AlertSender(
        WmsfoConnectionStrings connections,
        WmsfoOptions options,
        ISesSender sender,
        ILogger<AlertSender> logger,
        EmailLogoResolver? logo = null)
    {
        _connections = connections;
        _options = options;
        _sender = sender;
        _logger = logger;
        _logo = logo;
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
        if (rows.Count == 0) return sent;
        var brand = _logo is null ? null : await _logo.ResolveAsync(ct).ConfigureAwait(false);
        foreach (var row in rows)
        {
            using var lease = await _rateLimiter.AcquireAsync(1, ct).ConfigureAwait(false);
            if (!lease.IsAcquired) continue;
            try
            {
                var message = WithBrand(BuildMessage(row), brand);
                var messageId = await _sender.SendAsync(message, ct).ConfigureAwait(false);
                await MarkSuccessAsync(row.Id, messageId, conn, ct).ConfigureAwait(false);
                // sql.md 9.3: bump sent_count on the row that owns the outbox
                // row: a history row for a status alert, a message row for
                // event.message_posted.
                await BumpSentCountAsync(row.OutboxId, conn, ct).ConfigureAwait(false);
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

    // At most one of the two updates matches: an outbox row belongs to one
    // history row or one message row.
    private static async Task BumpSentCountAsync(long outboxId, NpgsqlConnection conn, CancellationToken ct)
    {
        foreach (var sql in new[]
        {
            "update event_status_history set sent_count = sent_count + 1 where outbox_id = $1;",
            "update event_message set sent_count = sent_count + 1 where outbox_id = $1;",
        })
        {
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = outboxId });
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    // The message with the layout's logoUrl and siteName set from `brand`;
    // unchanged when there is none, so the render supplies the defaults.
    private static SesMessage WithBrand(SesMessage message, EmailBrand? brand)
    {
        if (brand is null) return message;
        var values = new Dictionary<string, string>(message.Values, StringComparer.Ordinal)
        {
            ["logoUrl"] = brand.LogoUrl,
            ["siteName"] = brand.SiteName,
        };
        return message with { Values = values };
    }

    private SesMessage BuildMessage(DeliveryRow row)
    {
        using var payload = JsonDocument.Parse(row.Payload);
        var unsubscribeUrl = _options.SiteBaseUrl.TrimEnd('/') + "/alerts/unsubscribe?token=" + Uri.EscapeDataString(row.UnsubscribeToken);
        var apiUnsubscribe = _options.PublicApiBaseUrl.TrimEnd('/') + "/subscriptions/unsubscribe?token=" + Uri.EscapeDataString(row.UnsubscribeToken);

        return row.Topic switch
        {
            "event.status_changed" => BuildStatusChanged(row, payload.RootElement, unsubscribeUrl, apiUnsubscribe),
            "event.status_notified" => BuildStatusNotified(row, payload.RootElement, unsubscribeUrl, apiUnsubscribe),
            "event.message_posted" => BuildMessagePosted(row, payload.RootElement, unsubscribeUrl, apiUnsubscribe),
            _ => throw new InvalidOperationException($"Unsupported alert topic: {row.Topic}"),
        };
    }

    private SesMessage BuildStatusChanged(DeliveryRow row, JsonElement payload, string unsubscribeUrl, string apiUnsubscribe)
    {
        int toStatus = payload.GetProperty("toStatusId").GetInt32();
        long eventId = payload.GetProperty("eventId").GetInt64();
        var customMessage = LookupStatusMessage(payload);
        return BuildEventStatusMessage(row, eventId, toStatus, customMessage, unsubscribeUrl, apiUnsubscribe);
    }

    private SesMessage BuildStatusNotified(DeliveryRow row, JsonElement payload, string unsubscribeUrl, string apiUnsubscribe)
    {
        int statusId = payload.GetProperty("statusId").GetInt32();
        long eventId = payload.GetProperty("eventId").GetInt64();
        var customMessage = LookupStatusMessage(payload);
        return BuildEventStatusMessage(row, eventId, statusId, customMessage, unsubscribeUrl, apiUnsubscribe);
    }

    private SesMessage BuildEventStatusMessage(
        DeliveryRow row, long eventId, int statusId, string? customMessage,
        string unsubscribeUrl, string apiUnsubscribe)
    {
        var eventName = LookupEventName(eventId);
        var (scheduledAt, scheduleTimeZone) = LookupEventSchedule(eventId);
        var template = EmailTemplates.TemplateForStatus(statusId);
        var body = customMessage ?? EmailTemplates.StockParagraph(statusId, eventName, scheduledAt, scheduleTimeZone);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["customMessage"] = body,
            ["siteUrl"] = _options.SiteBaseUrl,
            ["unsubscribeUrl"] = unsubscribeUrl,
        };
        return new SesMessage(
            TemplateName: template,
            ToAddress: row.Address,
            Values: values,
            UnsubscribeUrl: apiUnsubscribe);
    }

    // The body of the event message a status alert's payload names in
    // `messageId`; null when the payload names none or the row is gone, so
    // the stock paragraph renders.
    private string? LookupStatusMessage(JsonElement payload)
    {
        if (!payload.TryGetProperty("messageId", out var p) || p.ValueKind != JsonValueKind.Number) return null;
        return LookupMessageBody(p.GetInt64());
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

    private (DateTimeOffset? ScheduledAt, string? ScheduleTimeZone) LookupEventSchedule(long eventId)
    {
        using var conn = new NpgsqlConnection(_connections.App);
        conn.Open();
        using var cmd = new NpgsqlCommand("select scheduled_at, schedule_time_zone from event where id = $1", conn);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = eventId });
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return (null, null);
        DateTimeOffset? scheduledAt = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
        string? zone = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (scheduledAt, zone);
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
