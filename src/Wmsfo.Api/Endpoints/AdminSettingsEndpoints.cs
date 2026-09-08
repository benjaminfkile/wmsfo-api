using System.Collections.Generic;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Settings + contracts 6 (`app_setting`). Five keys with the
// documented defaults; GET returns every key (missing rows fill in the design
// default with updatedBy = updatedAt = null); PUT is a [snapshot] write that
// upserts the row after per-key type and range validation.
public static class AdminSettingsEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapPut(app);
    }

    // The five keys with their JSON defaults and integer ranges (contracts 6).
    // Type is int only, so a single int-validating validator suffices; range
    // limits differ per key.
    public sealed record SettingKind(string Key, int DefaultValue, int Min, int Max);

    public static readonly IReadOnlyList<SettingKind> Kinds = new SettingKind[]
    {
        new("poll_interval_ms",             5000, 1000, 60000),
        new("cookie_limit_per_person",       10,    0,   1000),
        new("sponsor_linger_ms_per_dollar",  40,    0, 100000),
        new("sponsor_linger_min_ms",       2000,    0, 600000),
        new("beacon_stale_after_s",          45,   15,   3600),
    };

    private static readonly Dictionary<string, SettingKind> ByKey =
        Kinds.ToDictionary(k => k.Key, k => k, StringComparer.Ordinal);

    // GET /admin/settings → 200 { items } with defaults filled in.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/settings",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var rows = new Dictionary<string, (JsonElement value, string? by, DateTimeOffset? at)>(StringComparer.Ordinal);
                await using (var cmd = new NpgsqlCommand(
                    "select key, value, updated_by, updated_at from app_setting;", conn))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var key = reader.GetString(0);
                        var raw = reader.GetString(1);
                        using var doc = JsonDocument.Parse(raw);
                        var el = doc.RootElement.Clone();
                        rows[key] = (el, reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
                    }
                }
                var items = new List<SettingDto>(Kinds.Count);
                foreach (var kind in Kinds)
                {
                    if (rows.TryGetValue(kind.Key, out var row))
                    {
                        items.Add(new SettingDto
                        {
                            Key = kind.Key,
                            Value = row.value,
                            UpdatedBy = row.by,
                            UpdatedAt = row.at,
                        });
                    }
                    else
                    {
                        using var doc = JsonDocument.Parse(kind.DefaultValue.ToString(System.Globalization.CultureInfo.InvariantCulture));
                        items.Add(new SettingDto
                        {
                            Key = kind.Key,
                            Value = doc.RootElement.Clone(),
                            UpdatedBy = null,
                            UpdatedAt = null,
                        });
                    }
                }
                return Results.Ok(new ItemsResponse<SettingDto> { Items = items });
            })
            .WithTags("AdminSettings")
            .Produces<ItemsResponse<SettingDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PUT /admin/settings/{key} [snapshot]. 404 for an unknown key; 400 when the
    // value is not an integer or falls outside the range.
    private static void MapPut(IEndpointRouteBuilder app)
    {
        app.MapPut("/admin/settings/{key}",
            async (string key, SettingUpdateRequest body, HttpContext ctx,
                   AdminSnapshotTransaction snap, CancellationToken ct) =>
            {
                if (!ByKey.TryGetValue(key, out var kind))
                    throw new ApiException(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, "unknown setting");
                if (body.Value.ValueKind != JsonValueKind.Number || !body.Value.TryGetInt32(out var parsed))
                {
                    throw new ApiException(StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed,
                        "value must be an integer");
                }
                if (parsed < kind.Min || parsed > kind.Max)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed,
                        $"value must be between {kind.Min} and {kind.Max}");
                }
                var email = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<SettingDto>(async (conn, tx, token) =>
                {
                    var valueJson = parsed.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    string? updatedBy = null;
                    DateTimeOffset updatedAt = default;
                    await using (var up = new NpgsqlCommand(@"
insert into app_setting (key, value, updated_by, updated_at)
values ($1, $2::jsonb, $3, now())
on conflict (key) do update set value = excluded.value, updated_by = excluded.updated_by, updated_at = now()
returning updated_by, updated_at;", conn, tx))
                    {
                        up.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = kind.Key });
                        up.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = valueJson });
                        up.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                        await using var reader = await up.ExecuteReaderAsync(token);
                        await reader.ReadAsync(token);
                        updatedBy = reader.GetString(0);
                        updatedAt = reader.GetFieldValue<DateTimeOffset>(1);
                    }
                    using var doc = JsonDocument.Parse(valueJson);
                    return new SettingDto
                    {
                        Key = kind.Key,
                        Value = doc.RootElement.Clone(),
                        UpdatedBy = updatedBy,
                        UpdatedAt = updatedAt,
                    };
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminSettings")
            .Accepts<SettingUpdateRequest>("application/json")
            .Produces<SettingDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Admin)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }
}
