using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Help;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Help (Editor read, Admin write, the `help` capability). One
// row per help popover of the admin panel; the boot keeps the rows in line
// with help/topics.json (sql.md 8.16) and these endpoints let an admin replace
// a topic's text or put the seed's text back. Nothing here is
// snapshot-affecting, so every write runs in a plain transaction.
public static class AdminHelpEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapPut(app);
        MapReset(app);
    }

    // GET /admin/help → 200 { items: HelpTopic[] } ordered by page then key.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/help",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<HelpTopicDto>();
                await using var cmd = new NpgsqlCommand(
                    SelectSql + " order by h.page collate \"C\", h.key collate \"C\";", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) items.Add(ReadRow(reader));
                return Results.Ok(new ItemsResponse<HelpTopicDto> { Items = items });
            })
            .WithTags("AdminHelp")
            .Produces<ItemsResponse<HelpTopicDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireCapability(ApiKeyCapabilities.Help)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PUT /admin/help/{key} → 200 HelpTopic. title 1 to 120, body 1 to 2000,
    // links 0 to 6 of { label 1 to 60, to a / path or an https URL }, each
    // trimmed and free of em and en dashes. Stamps edited_by (the email claim
    // or key:<name>) and edited_at.
    private static void MapPut(IEndpointRouteBuilder app)
    {
        app.MapPut("/admin/help/{key}",
            async (string key, PutHelpTopicRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   AuditRecorder audit, CancellationToken ct) =>
            {
                var (title, text, links) = Validate(body);
                if (!HelpTopicSeed.IsValidKey(key)) throw NotFound();
                var actor = AdminHelpers.RequireAdminEmail(ctx);

                var dto = await snap.RunWithoutSnapshotAsync<HelpTopicDto>(async (conn, tx, token) =>
                {
                    var before = await LockAndReadAsync(conn, tx, key, token);
                    await using (var update = new NpgsqlCommand(@"
update help_topic
set title = $2, body = $3, links = $4, edited_by = $5, edited_at = now(), updated_at = now()
where key = $1;", conn, tx))
                    {
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = title });
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = text });
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = HelpTopics.LinksJson(links) });
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = actor });
                        await update.ExecuteNonQueryAsync(token);
                    }
                    var after = await ReadByKeyAsync(conn, tx, key, token) ?? throw NotFound();
                    after.Audit = await audit.RecordAsync(conn, tx, "update", "help_topic", key, before, after, token);
                    return after;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminHelp")
            .Accepts<PutHelpTopicRequest>("application/json")
            .Produces<HelpTopicDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Help)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/help/{key}/reset → 200 HelpTopic. Copies the defaults into
    // title, body, and links and nulls edited_by and edited_at.
    private static void MapReset(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/help/{key}/reset",
            async (string key, HttpContext ctx, AdminSnapshotTransaction snap,
                   AuditRecorder audit, CancellationToken ct) =>
            {
                if (!HelpTopicSeed.IsValidKey(key)) throw NotFound();
                _ = AdminHelpers.RequireAdminEmail(ctx);
                var dto = await snap.RunWithoutSnapshotAsync<HelpTopicDto>(async (conn, tx, token) =>
                {
                    var before = await LockAndReadAsync(conn, tx, key, token);
                    await using (var update = new NpgsqlCommand(@"
update help_topic
set title = default_title, body = default_body, links = default_links,
    edited_by = null, edited_at = null, updated_at = now()
where key = $1;", conn, tx))
                    {
                        update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
                        await update.ExecuteNonQueryAsync(token);
                    }
                    var after = await ReadByKeyAsync(conn, tx, key, token) ?? throw NotFound();
                    after.Audit = await audit.RecordAsync(conn, tx, "reset", "help_topic", key, before, after, token);
                    return after;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminHelp")
            .Produces<HelpTopicDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireCapability(ApiKeyCapabilities.Help)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- shared plumbing ---

    private const string DashMessage = "must not contain an em dash or an en dash";

    private static (string Title, string Body, List<HelpLinkDto> Links) Validate(PutHelpTopicRequest body)
    {
        var v = new RequestValidation();
        var title = body.Title?.Trim() ?? "";
        var text = body.Body?.Trim() ?? "";
        if (title.Length < 1 || title.Length > HelpTopicSeed.TitleMax)
            v.Field("title", $"must be 1 to {HelpTopicSeed.TitleMax} characters");
        else if (HelpTopicSeed.HasDash(title))
            v.Field("title", DashMessage);
        if (text.Length < 1 || text.Length > HelpTopicSeed.BodyMax)
            v.Field("body", $"must be 1 to {HelpTopicSeed.BodyMax} characters");
        else if (HelpTopicSeed.HasDash(text))
            v.Field("body", DashMessage);

        var links = new List<HelpLinkDto>();
        if (body.Links is null)
        {
            v.Field("links", $"must be an array of 0 to {HelpTopicSeed.LinksMax} links");
        }
        else if (body.Links.Count > HelpTopicSeed.LinksMax)
        {
            v.Field("links", $"must have at most {HelpTopicSeed.LinksMax} links");
        }
        else
        {
            for (var i = 0; i < body.Links.Count; i++)
            {
                var link = body.Links[i];
                if (link is null)
                {
                    v.Field($"links[{i}]", "required");
                    continue;
                }
                var label = link.Label?.Trim() ?? "";
                var to = link.To?.Trim() ?? "";
                if (label.Length < 1 || label.Length > HelpTopicSeed.LinkLabelMax)
                    v.Field($"links[{i}].label", $"must be 1 to {HelpTopicSeed.LinkLabelMax} characters");
                else if (HelpTopicSeed.HasDash(label))
                    v.Field($"links[{i}].label", DashMessage);
                if (!HelpTopicSeed.IsValidLinkTarget(to))
                    v.Field($"links[{i}].to", "must be a / path or an https:// URL");
                else if (HelpTopicSeed.HasDash(to))
                    v.Field($"links[{i}].to", DashMessage);
                links.Add(new HelpLinkDto { Label = label, To = to });
            }
        }
        v.ThrowIfInvalid();
        return (title, text, links);
    }

    private static async Task<HelpTopicDto> LockAndReadAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string key, CancellationToken ct)
    {
        await using (var lockRow = new NpgsqlCommand(
            "select 1 from help_topic where key = $1 for update;", conn, tx))
        {
            lockRow.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
            var found = await lockRow.ExecuteScalarAsync(ct);
            if (found is null || found is DBNull) throw NotFound();
        }
        return await ReadByKeyAsync(conn, tx, key, ct) ?? throw NotFound();
    }

    private const string SelectSql = @"
select h.key, h.page, h.label, h.title, h.body, h.links::text,
       h.edited_by, h.edited_at, h.default_updated_at, h.updated_at,
       a.action, a.actor, a.at
from help_topic h
left join lateral (
  select action, actor, at from audit_log
  where entity = 'help_topic' and entity_id = h.key
  order by id desc limit 1
) a on true";

    private static async Task<HelpTopicDto?> ReadByKeyAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string key, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(SelectSql + " where h.key = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = key });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadRow(reader);
    }

    private static HelpTopicDto ReadRow(NpgsqlDataReader reader)
    {
        var editedBy = reader.IsDBNull(6) ? null : reader.GetString(6);
        DateTimeOffset? editedAt = reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7);
        var defaultUpdatedAt = reader.GetFieldValue<DateTimeOffset>(8);
        var edited = editedBy is not null;
        var dto = new HelpTopicDto
        {
            Key = reader.GetString(0),
            Page = reader.GetString(1),
            Label = reader.GetString(2),
            Title = reader.GetString(3),
            Body = reader.GetString(4),
            Links = JsonSerializer.Deserialize<List<HelpLinkDto>>(reader.GetString(5), CanonicalJson.Options) ?? new(),
            Edited = edited,
            EditedBy = editedBy,
            EditedAt = editedAt,
            DefaultChanged = edited && editedAt is DateTimeOffset at && defaultUpdatedAt > at,
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        };
        if (!reader.IsDBNull(10))
        {
            dto.Audit = new AuditStampDto
            {
                Action = reader.GetString(10),
                By = reader.GetString(11),
                At = reader.GetFieldValue<DateTimeOffset>(12),
            };
        }
        return dto;
    }

    private static ApiException NotFound() =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "help topic not found");
}
