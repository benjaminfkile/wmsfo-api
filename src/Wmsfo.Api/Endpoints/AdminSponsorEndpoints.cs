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

// contracts 4.5 Sponsors (Editor) - CRUD and per-year upsert. Every write is
// marked [snapshot] in contracts, so PATCH / POST / PUT / DELETE flow through
// AdminSnapshotTransaction (api.md 10.2). logoMediaId on PATCH must name a
// ready media asset - otherwise 404 (asset missing) or 409 media_not_ready.
public static class AdminSponsorEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapList(app);
        MapGet(app);
        MapCreate(app);
        MapPatch(app);
        MapDelete(app);
        MapUpsertYear(app);
        MapDeleteYear(app);
    }

    // GET /admin/sponsors → 200 { items: Sponsor[] } ordered by name asc.
    private static void MapList(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/sponsors",
            async (WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);

                var sponsors = new List<SponsorDto>();
                var byId = new Dictionary<long, SponsorDto>();
                await using (var cmd = new NpgsqlCommand(SponsorSelect + " order by s.name asc, s.id asc;", conn))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var dto = ReadSponsorRow(reader, options);
                        sponsors.Add(dto);
                        byId[dto.Id] = dto;
                    }
                }
                if (sponsors.Count > 0)
                {
                    await LoadYearsAsync(conn, null, byId, ct);
                }
                return Results.Ok(new ItemsResponse<SponsorDto> { Items = sponsors });
            })
            .WithTags("AdminSponsors")
            .Produces<ItemsResponse<SponsorDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // GET /admin/sponsors/{id} → 200 Sponsor.
    private static void MapGet(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/sponsors/{id:long}",
            async (long id, WmsfoConnectionStrings connections, WmsfoOptions options, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var dto = await ReadSponsorByIdAsync(conn, null, id, options, ct);
                if (dto is null) throw NotFound("sponsor not found");
                return Results.Ok(dto);
            })
            .WithTags("AdminSponsors")
            .Produces<SponsorDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // POST /admin/sponsors [snapshot]. name 1..200; url fields absolute http/https up to 2048.
    private static void MapCreate(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/sponsors",
            async (CreateSponsorRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   WmsfoOptions options, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                ValidateName(body.Name, v);
                ValidateUrls(v, body.WebsiteUrl, body.FbUrl, body.IgUrl);
                v.ThrowIfInvalid();
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<SponsorDto>(async (conn, tx, token) =>
                {
                    long newId;
                    await using (var insert = new NpgsqlCommand(@"
insert into sponsor (name, contact_person, email, phone, address, website_url, fb_url, ig_url, updated_at)
values ($1, $2, $3, $4, $5, $6, $7, $8, now()) returning id;", conn, tx))
                    {
                        insert.Parameters.Add(TextOrNull(body.Name.Trim()));
                        insert.Parameters.Add(TextOrNull(body.ContactPerson));
                        insert.Parameters.Add(TextOrNull(body.Email));
                        insert.Parameters.Add(TextOrNull(body.Phone));
                        insert.Parameters.Add(TextOrNull(body.Address));
                        insert.Parameters.Add(TextOrNull(body.WebsiteUrl));
                        insert.Parameters.Add(TextOrNull(body.FbUrl));
                        insert.Parameters.Add(TextOrNull(body.IgUrl));
                        newId = (long)(await insert.ExecuteScalarAsync(token) ?? 0L);
                    }
                    var dto = await ReadSponsorByIdAsync(conn, tx, newId, options, token);
                    if (dto is null) throw NotFound("sponsor not found");
                    return dto;
                }, ct);
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminSponsors")
            .Accepts<CreateSponsorRequest>("application/json")
            .Produces<SponsorDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PATCH /admin/sponsors/{id} [snapshot]. logoMediaId: null clears; a value
    // must reference a ready media_asset (404 asset missing, 409 media_not_ready).
    private static void MapPatch(IEndpointRouteBuilder app)
    {
        app.MapPatch("/admin/sponsors/{id:long}",
            async (long id, PatchSponsorRequest body, HttpContext ctx, AdminSnapshotTransaction snap,
                   WmsfoOptions options, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (body.Name is not null) ValidateName(body.Name, v);
                ValidateUrls(v, body.WebsiteUrl, body.FbUrl, body.IgUrl);
                Guid? parsedLogo = null;
                bool clearLogo = false;
                bool setLogo = false;
                if (body.LogoMediaId is not null)
                {
                    if (body.LogoMediaId.Length == 0)
                    {
                        clearLogo = true;
                    }
                    else if (Guid.TryParse(body.LogoMediaId, out var g))
                    {
                        parsedLogo = g;
                        setLogo = true;
                    }
                    else
                    {
                        v.Field("logoMediaId", "must be a uuid, empty string, or null");
                    }
                }
                v.ThrowIfInvalid();
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<SponsorDto>(async (conn, tx, token) =>
                {
                    // Confirm the sponsor exists (and lock it).
                    await using (var check = new NpgsqlCommand(
                        "select 1 from sponsor where id = $1 for update;", conn, tx))
                    {
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull) throw NotFound("sponsor not found");
                    }

                    // Check logo media asset readiness when the caller is setting a new id.
                    if (setLogo && parsedLogo is not null)
                    {
                        string? state = null;
                        await using (var read = new NpgsqlCommand(
                            "select state from media_asset where id = $1;", conn, tx))
                        {
                            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = parsedLogo.Value });
                            var r = await read.ExecuteScalarAsync(token);
                            if (r is null || r is DBNull) throw NotFound("media not found");
                            state = (string)r;
                        }
                        if (!string.Equals(state, "ready", StringComparison.Ordinal))
                        {
                            throw new ApiException(StatusCodes.Status409Conflict,
                                "media_not_ready", "media asset is not ready");
                        }
                    }

                    var sets = new List<string>();
                    var parameters = new List<NpgsqlParameter>();
                    var next = 1;
                    if (body.Name is not null) { sets.Add($"name = ${next++}"); parameters.Add(TextOrNull(body.Name.Trim())); }
                    if (body.ContactPerson is not null) { sets.Add($"contact_person = ${next++}"); parameters.Add(TextOrNull(body.ContactPerson)); }
                    if (body.Email is not null) { sets.Add($"email = ${next++}"); parameters.Add(TextOrNull(body.Email)); }
                    if (body.Phone is not null) { sets.Add($"phone = ${next++}"); parameters.Add(TextOrNull(body.Phone)); }
                    if (body.Address is not null) { sets.Add($"address = ${next++}"); parameters.Add(TextOrNull(body.Address)); }
                    if (body.WebsiteUrl is not null) { sets.Add($"website_url = ${next++}"); parameters.Add(TextOrNull(body.WebsiteUrl)); }
                    if (body.FbUrl is not null) { sets.Add($"fb_url = ${next++}"); parameters.Add(TextOrNull(body.FbUrl)); }
                    if (body.IgUrl is not null) { sets.Add($"ig_url = ${next++}"); parameters.Add(TextOrNull(body.IgUrl)); }
                    if (setLogo)
                    {
                        sets.Add($"logo_media_id = ${next++}");
                        parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = parsedLogo!.Value });
                    }
                    else if (clearLogo)
                    {
                        sets.Add("logo_media_id = null");
                    }
                    sets.Add("updated_at = now()");
                    var whereIdx = next;
                    var sql = $"update sponsor set {string.Join(", ", sets)} where id = ${whereIdx};";
                    await using var upd = new NpgsqlCommand(sql, conn, tx);
                    foreach (var p in parameters) upd.Parameters.Add(p);
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await upd.ExecuteNonQueryAsync(token);

                    var updated = await ReadSponsorByIdAsync(conn, tx, id, options, token);
                    if (updated is null) throw NotFound("sponsor not found");
                    return updated;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminSponsors")
            .Accepts<PatchSponsorRequest>("application/json")
            .Produces<SponsorDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/sponsors/{id} [snapshot]. Cascades years; logo asset stays in the library.
    private static void MapDelete(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/sponsors/{id:long}",
            async (long id, HttpContext ctx, AdminSnapshotTransaction snap, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await snap.RunAsync<object?>(async (conn, tx, token) =>
                {
                    await using var del = new NpgsqlCommand("delete from sponsor where id = $1;", conn, tx);
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var rows = await del.ExecuteNonQueryAsync(token);
                    if (rows == 0) throw NotFound("sponsor not found");
                    return null;
                }, ct);
                return Results.NoContent();
            })
            .WithTags("AdminSponsors")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // PUT /admin/sponsors/{id}/years/{eventYear} [snapshot]. Upsert on (sponsor_id, event_year).
    // amountDonated 0..1_000_000_000 with 2 decimals or null; eventYear 2000..2100.
    private static void MapUpsertYear(IEndpointRouteBuilder app)
    {
        app.MapPut("/admin/sponsors/{id:long}/years/{eventYear:int}",
            async (long id, int eventYear, UpsertSponsorYearRequest body, HttpContext ctx,
                   AdminSnapshotTransaction snap, WmsfoOptions options, CancellationToken ct) =>
            {
                var v = new RequestValidation();
                if (eventYear < 2000 || eventYear > 2100)
                    v.Field("eventYear", "must be between 2000 and 2100");
                if (body.AmountDonated is decimal amt)
                {
                    if (amt < 0m || amt > 1_000_000_000m)
                        v.Field("amountDonated", "must be between 0 and 1,000,000,000");
                    else if (decimal.Round(amt, 2) != amt)
                        v.Field("amountDonated", "must have at most 2 decimal places");
                }
                v.ThrowIfInvalid();
                _ = AdminHelpers.RequireAdminEmail(ctx);

                var (dto, _) = await snap.RunAsync<SponsorDto>(async (conn, tx, token) =>
                {
                    await using (var check = new NpgsqlCommand(
                        "select 1 from sponsor where id = $1 for update;", conn, tx))
                    {
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull) throw NotFound("sponsor not found");
                    }
                    await using (var ins = new NpgsqlCommand(@"
insert into sponsor_year (sponsor_id, event_year, amount_donated, active, can_advertise, anonymous)
values ($1, $2, $3, $4, $5, $6)
on conflict (sponsor_id, event_year) do update set
  amount_donated = excluded.amount_donated,
  active         = excluded.active,
  can_advertise  = excluded.can_advertise,
  anonymous      = excluded.anonymous;", conn, tx))
                    {
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = eventYear });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Numeric, Value = (object?)body.AmountDonated ?? DBNull.Value });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = body.Active });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = body.CanAdvertise });
                        ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = body.Anonymous });
                        await ins.ExecuteNonQueryAsync(token);
                    }
                    var dto = await ReadSponsorByIdAsync(conn, tx, id, options, token);
                    if (dto is null) throw NotFound("sponsor not found");
                    return dto;
                }, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminSponsors")
            .Accepts<UpsertSponsorYearRequest>("application/json")
            .Produces<SponsorDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // DELETE /admin/sponsors/{id}/years/{eventYear} [snapshot]. Idempotent.
    private static void MapDeleteYear(IEndpointRouteBuilder app)
    {
        app.MapDelete("/admin/sponsors/{id:long}/years/{eventYear:int}",
            async (long id, int eventYear, HttpContext ctx, AdminSnapshotTransaction snap, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await snap.RunAsync<object?>(async (conn, tx, token) =>
                {
                    await using (var check = new NpgsqlCommand(
                        "select 1 from sponsor where id = $1;", conn, tx))
                    {
                        check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                        var r = await check.ExecuteScalarAsync(token);
                        if (r is null || r is DBNull) throw NotFound("sponsor not found");
                    }
                    await using var del = new NpgsqlCommand(
                        "delete from sponsor_year where sponsor_id = $1 and event_year = $2;", conn, tx);
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = eventYear });
                    await del.ExecuteNonQueryAsync(token);
                    return null;
                }, ct);
                return Results.NoContent();
            })
            .WithTags("AdminSponsors")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- helpers ---

    private const string SponsorSelect = @"
select s.id, s.name, s.contact_person, s.email, s.phone, s.address,
       s.website_url, s.fb_url, s.ig_url, s.logo_media_id,
       s.created_at, s.updated_at,
       m.filename, m.content_type, m.kind, m.state, m.s3_key,
       m.size_bytes, m.width, m.height, m.sha256, m.variants,
       m.alt, m.title, m.uploaded_by, m.created_at, m.confirmed_at,
       m.unreferenced_since, m.orphaned_at
from sponsor s
left join media_asset m on m.id = s.logo_media_id";

    private static ApiException NotFound(string message) =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, message);

    private static NpgsqlParameter TextOrNull(string? value) => new()
    {
        NpgsqlDbType = NpgsqlDbType.Text,
        Value = (object?)value ?? DBNull.Value,
    };

    private static void ValidateName(string name, RequestValidation v)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 200)
            v.Field("name", "must be 1 to 200 characters");
    }

    private static void ValidateUrls(RequestValidation v, params string?[] urls)
    {
        string[] fields = { "websiteUrl", "fbUrl", "igUrl" };
        for (var i = 0; i < urls.Length; i++)
        {
            var value = urls[i];
            if (string.IsNullOrEmpty(value)) continue;
            if (value.Length > 2048) { v.Field(fields[i], "must be at most 2048 characters"); continue; }
            if (!Uri.TryCreate(value, UriKind.Absolute, out var u) ||
                (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
                v.Field(fields[i], "must be an absolute http or https url");
        }
    }

    private static async Task<SponsorDto?> ReadSponsorByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, WmsfoOptions options, CancellationToken ct)
    {
        SponsorDto? dto;
        await using (var cmd = new NpgsqlCommand(SponsorSelect + " where s.id = $1;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            dto = ReadSponsorRow(reader, options);
        }
        var byId = new Dictionary<long, SponsorDto> { [dto.Id] = dto };
        await LoadYearsAsync(conn, tx, byId, ct);
        return dto;
    }

    private static async Task LoadYearsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Dictionary<long, SponsorDto> byId, CancellationToken ct)
    {
        if (byId.Count == 0) return;
        var ids = byId.Keys.ToArray();
        await using var cmd = new NpgsqlCommand(@"
select sponsor_id, event_year, amount_donated, active, can_advertise, anonymous, registered_at
from sponsor_year where sponsor_id = any($1)
order by sponsor_id, event_year desc;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            Value = ids,
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sponsorId = reader.GetInt64(0);
            if (!byId.TryGetValue(sponsorId, out var sponsor)) continue;
            sponsor.Years.Add(new SponsorYearDto
            {
                EventYear = reader.GetInt32(1),
                AmountDonated = reader.IsDBNull(2) ? null : reader.GetDecimal(2),
                Active = reader.GetBoolean(3),
                CanAdvertise = reader.GetBoolean(4),
                Anonymous = reader.GetBoolean(5),
                RegisteredAt = reader.GetFieldValue<DateTimeOffset>(6),
            });
        }
    }

    private static SponsorDto ReadSponsorRow(NpgsqlDataReader reader, WmsfoOptions options)
    {
        var cdn = options.CdnBaseUrl.TrimEnd('/');
        var dto = new SponsorDto
        {
            Id = reader.GetInt64(0),
            Name = reader.GetString(1),
            ContactPerson = reader.IsDBNull(2) ? null : reader.GetString(2),
            Email = reader.IsDBNull(3) ? null : reader.GetString(3),
            Phone = reader.IsDBNull(4) ? null : reader.GetString(4),
            Address = reader.IsDBNull(5) ? null : reader.GetString(5),
            WebsiteUrl = reader.IsDBNull(6) ? null : reader.GetString(6),
            FbUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
            IgUrl = reader.IsDBNull(8) ? null : reader.GetString(8),
            LogoMediaId = reader.IsDBNull(9) ? null : reader.GetGuid(9).ToString(),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(10),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(11),
        };
        if (!reader.IsDBNull(9))
        {
            var s3Key = reader.GetString(16);
            var variantsJson = reader.IsDBNull(21) ? "{}" : reader.GetString(21);
            var variants = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using (var doc = System.Text.Json.JsonDocument.Parse(variantsJson))
            {
                foreach (var e in doc.RootElement.EnumerateObject())
                {
                    var v = e.Value.GetString() ?? "";
                    variants[e.Name] = cdn + "/" + v;
                }
            }
            dto.Logo = new MediaAssetDto
            {
                Id = dto.LogoMediaId ?? "",
                Filename = reader.GetString(12),
                ContentType = reader.GetString(13),
                Kind = reader.GetString(14),
                State = reader.GetString(15),
                SizeBytes = reader.IsDBNull(17) ? null : reader.GetInt64(17),
                Width = reader.IsDBNull(18) ? null : reader.GetInt32(18),
                Height = reader.IsDBNull(19) ? null : reader.GetInt32(19),
                Sha256 = reader.IsDBNull(20) ? null : reader.GetString(20).Trim(),
                Variants = variants,
                Alt = reader.GetString(22),
                Title = reader.GetString(23),
                UploadedBy = reader.GetString(24),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(25),
                ConfirmedAt = reader.IsDBNull(26) ? null : reader.GetFieldValue<DateTimeOffset>(26),
                UnreferencedSince = reader.IsDBNull(27) ? null : reader.GetFieldValue<DateTimeOffset>(27),
                OrphanedAt = reader.IsDBNull(28) ? null : reader.GetFieldValue<DateTimeOffset>(28),
                Url = cdn + "/" + s3Key,
            };
        }
        return dto;
    }
}
