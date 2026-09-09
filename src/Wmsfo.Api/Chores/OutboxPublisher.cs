using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Data.Sql;
using Wmsfo.Api.Email;

namespace Wmsfo.Api.Chores;

// api.md 13 / contracts 7.6 + 7.7 / sql.md 9.1 + 9.2: the outbox publisher.
// Claim up to 50 rows with skip-locked, process each per topic, mark
// published on success and last_error on failure. Overlapping leaders claim
// disjoint rows (skip-locked) and the alert_delivery unique constraint
// bounds the damage from any second attempt.
public sealed class OutboxPublisher
{
    private readonly WmsfoConnectionStrings _connections;
    private readonly WmsfoOptions _options;
    private readonly ISesSender _sender;
    private readonly ILogger<OutboxPublisher> _logger;

    public OutboxPublisher(
        WmsfoConnectionStrings connections,
        WmsfoOptions options,
        ISesSender sender,
        ILogger<OutboxPublisher> logger)
    {
        _connections = connections;
        _options = options;
        _sender = sender;
        _logger = logger;
    }

    // One pass: returns how many rows were processed (success + skipped).
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var claims = new List<ClaimedRow>();
        await using (var claim = new NpgsqlCommand(ChoreRecipes.OutboxClaim, conn))
        {
            await using var reader = await claim.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                claims.Add(new ClaimedRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt32(3)));
            }
        }

        foreach (var row in claims)
        {
            try
            {
                await ProcessAsync(row, conn, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "outbox row {Id} topic={Topic} attempt {Attempt} failed",
                    row.Id, row.Topic, row.Attempts);
                await FailAsync(row.Id, ex.Message, conn, ct).ConfigureAwait(false);
                if (row.Attempts >= 5)
                {
                    _logger.LogWarning(
                        "outbox row {Id} topic={Topic} exhausted after {Attempts} attempts; marker=wmsfo_outbox_exhausted",
                        row.Id, row.Topic, row.Attempts);
                }
            }
        }
        return claims.Count;
    }

    private async Task ProcessAsync(ClaimedRow row, NpgsqlConnection conn, CancellationToken ct)
    {
        using var payload = JsonDocument.Parse(row.Payload);
        switch (row.Topic)
        {
            case "event.status_changed":
                await HandleStatusChangedAsync(row, payload.RootElement, conn, ct).ConfigureAwait(false);
                break;
            case "event.message_posted":
                await HandleMessagePostedAsync(row, payload.RootElement, conn, ct).ConfigureAwait(false);
                break;
            case "subscription.verify":
                await HandleSubscriptionVerifyAsync(row, payload.RootElement, conn, ct).ConfigureAwait(false);
                break;
            case "contact.received":
                await HandleContactReceivedAsync(row, payload.RootElement, conn, ct).ConfigureAwait(false);
                break;
            default:
                _logger.LogWarning("outbox row {Id} topic {Topic} is not handled; skipping", row.Id, row.Topic);
                await MarkPublishedAsync(row.Id, conn, ct).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleStatusChangedAsync(
        ClaimedRow row, JsonElement payload, NpgsqlConnection conn, CancellationToken ct)
    {
        var notify = payload.TryGetProperty("notify", out var n) && n.ValueKind == JsonValueKind.True;
        int? toStatus = null;
        if (payload.TryGetProperty("toStatusId", out var toEl) && toEl.ValueKind == JsonValueKind.Number)
            toStatus = toEl.GetInt32();

        if (!notify || (toStatus != 2 && toStatus != 3))
        {
            await MarkPublishedAsync(row.Id, conn, ct).ConfigureAwait(false);
            return;
        }

        await FanOutAsync(row.Id, conn, ct).ConfigureAwait(false);
        await MarkPublishedAsync(row.Id, conn, ct).ConfigureAwait(false);
    }

    private async Task HandleMessagePostedAsync(
        ClaimedRow row, JsonElement payload, NpgsqlConnection conn, CancellationToken ct)
    {
        long eventId = payload.GetProperty("eventId").GetInt64();
        long messageId = payload.GetProperty("messageId").GetInt64();

        await using (var exists = new NpgsqlCommand(ChoreRecipes.EventMessageExists, conn))
        {
            exists.Parameters.Add(new NpgsqlParameter("message_id", NpgsqlDbType.Bigint) { Value = messageId });
            exists.Parameters.Add(new NpgsqlParameter("event_id", NpgsqlDbType.Bigint) { Value = eventId });
            var found = await exists.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (found is null)
            {
                await using var mark = new NpgsqlCommand(ChoreRecipes.OutboxMarkSourceDeleted, conn);
                mark.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = row.Id });
                await mark.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
        }

        await FanOutAsync(row.Id, conn, ct).ConfigureAwait(false);
        await MarkPublishedAsync(row.Id, conn, ct).ConfigureAwait(false);
    }

    private async Task HandleSubscriptionVerifyAsync(
        ClaimedRow row, JsonElement payload, NpgsqlConnection conn, CancellationToken ct)
    {
        long subscriberId = payload.GetProperty("subscriberId").GetInt64();
        string verifyToken = payload.GetProperty("verifyToken").GetString() ?? "";

        string? address = null;
        await using (var lookup = new NpgsqlCommand(ChoreRecipes.SubscriberFetchForVerify, conn))
        {
            lookup.Parameters.Add(new NpgsqlParameter("subscriber_id", NpgsqlDbType.Bigint) { Value = subscriberId });
            var scalar = await lookup.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is string s) address = s;
        }

        if (address is null)
        {
            // Missing or verified/unsubscribed subscriber: mark published, send nothing.
            await MarkPublishedAsync(row.Id, conn, ct).ConfigureAwait(false);
            return;
        }

        var verifyUrl = _options.SiteBaseUrl.TrimEnd('/') + "/alerts/verify?token=" + Uri.EscapeDataString(verifyToken);
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["verifyUrl"] = verifyUrl,
            ["siteUrl"] = _options.SiteBaseUrl,
        };
        await _sender.SendAsync(new SesMessage(
            TemplateName: EmailTemplates.SubscriptionVerify,
            ToAddress: address,
            Values: values), ct).ConfigureAwait(false);

        await MarkPublishedAsync(row.Id, conn, ct).ConfigureAwait(false);
    }

    private async Task HandleContactReceivedAsync(
        ClaimedRow row, JsonElement payload, NpgsqlConnection conn, CancellationToken ct)
    {
        long contactId = payload.GetProperty("contactMessageId").GetInt64();

        string? name = null;
        string? email = null;
        string? body = null;
        await using (var lookup = new NpgsqlCommand(ChoreRecipes.ContactMessageFetchForOutbox, conn))
        {
            lookup.Parameters.Add(new NpgsqlParameter("contact_message_id", NpgsqlDbType.Bigint) { Value = contactId });
            await using var reader = await lookup.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                name = reader.GetString(0);
                email = reader.GetString(1);
                body = reader.GetString(2);
            }
        }

        if (name is null || email is null || body is null)
        {
            await MarkPublishedAsync(row.Id, conn, ct).ConfigureAwait(false);
            return;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contactName"] = name,
            ["contactEmail"] = email,
            ["contactMessage"] = body,
        };
        await _sender.SendAsync(new SesMessage(
            TemplateName: EmailTemplates.ContactReceived,
            ToAddress: _options.ContactNotifyEmail,
            Values: values,
            ReplyTo: email), ct).ConfigureAwait(false);

        await MarkPublishedAsync(row.Id, conn, ct).ConfigureAwait(false);
    }

    private static async Task FanOutAsync(long outboxId, NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(ChoreRecipes.AlertDeliveryFanOut, conn);
        cmd.Parameters.Add(new NpgsqlParameter("outbox_id", NpgsqlDbType.Bigint) { Value = outboxId });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task MarkPublishedAsync(long id, NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(ChoreRecipes.OutboxPublished, conn);
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task FailAsync(long id, string error, NpgsqlConnection conn, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(ChoreRecipes.OutboxFailed, conn);
        cmd.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Bigint) { Value = id });
        cmd.Parameters.Add(new NpgsqlParameter("error", NpgsqlDbType.Text) { Value = error.Length > 500 ? error[..500] : error });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private sealed record ClaimedRow(long Id, string Topic, string Payload, int Attempts);
}
