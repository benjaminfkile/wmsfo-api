using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Auth;
using Wmsfo.Api.Config;
using Wmsfo.Api.Content;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Security;

namespace Wmsfo.Api.Endpoints;

// contracts 4.5 Pages / Sections and items / Site settings / Content, all Editor
// policy. Every /admin/pages*, /admin/sections*, /admin/items*, /admin/site-settings*
// endpoint plus GET /admin/content/kinds, /admin/content/status, /admin/content/draft.
// Working-set writes never rebuild the snapshot (contracts 4.5 Pages preamble);
// reads still work when nothing is published yet.
public static class AdminContentEndpoints
{
    public static void MapAll(IEndpointRouteBuilder app)
    {
        MapKinds(app);
        MapPages(app);
        MapSections(app);
        MapItems(app);
        MapSiteSettings(app);
        MapContentDraftAndStatus(app);
        MapPublishVersionsRestore(app);
        MapPreviewToken(app);
        MapPreviewDocument(app);
    }

    // --- GET /admin/content/kinds -------------------------------------------------

    private static void MapKinds(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/content/kinds",
            (KindRegistry registry) =>
            {
                var items = new List<JsonNode>(registry.Kinds.Count);
                foreach (var info in registry.Kinds)
                {
                    items.Add(registry.BuildKindInfoJson(info.Kind));
                }
                var response = new JsonObject { ["items"] = new JsonArray(items.ToArray()) };
                return Results.Json(response, statusCode: StatusCodes.Status200OK);
            })
            .WithTags("AdminSections")
            .Produces<ItemsResponse<KindInfoDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- Pages --------------------------------------------------------------------

    private static void MapPages(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/pages",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var pages = await ReadAllPagesAsync(conn, null, ct);
                return Results.Ok(new ItemsResponse<PageAdminDto> { Items = pages });
            })
            .WithTags("AdminPages")
            .Produces<ItemsResponse<PageAdminDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        app.MapPost("/admin/pages",
            async (CreatePageRequest body, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                ValidatePageBody(body.Slug, body.Title, body.NavLabel, isCreate: true);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);

                // navPosition defaults to one past the greatest existing `none` page.
                var navPosition = body.NavPosition;
                if (navPosition <= 0)
                {
                    await using var maxCmd = new NpgsqlCommand(
                        "select coalesce(max(nav_position), 0) + 10 from page where role = 'none';", conn, tx);
                    navPosition = Convert.ToInt32(await maxCmd.ExecuteScalarAsync(ct) ?? 10);
                }

                long id;
                try
                {
                    await using var insert = new NpgsqlCommand(@"
insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by)
values ($1, $2, $3, $4, $5, 'none', $6, $6)
returning id;", conn, tx);
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Slug });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Title.Trim() });
                    insert.Parameters.Add(new NpgsqlParameter
                    {
                        NpgsqlDbType = NpgsqlDbType.Text,
                        Value = string.IsNullOrEmpty(body.NavLabel) ? DBNull.Value : (object)body.NavLabel.Trim(),
                    });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = navPosition });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = body.IsHidden });
                    insert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                    id = Convert.ToInt64(await insert.ExecuteScalarAsync(ct) ?? 0L);
                }
                catch (PostgresException ex) when (ex.SqlState == "23505")
                {
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.SlugTaken,
                        $"slug `{body.Slug}` is already used");
                }
                await tx.CommitAsync(ct);
                var page = await ReadPageByIdAsync(conn, null, id, ct);
                return Results.Json(page, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminPages")
            .Accepts<CreatePageRequest>("application/json")
            .Produces<PageAdminDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        app.MapGet("/admin/pages/{id:long}",
            async (long id, WmsfoConnectionStrings connections, KindRegistry registry,
                   SchemaValidator validator, IconLibrary? iconLibrary, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var page = await ReadPageByIdAsync(conn, null, id, ct);
                if (page is null) throw NotFound("page not found");
                var detail = new PageDetailDto
                {
                    Id = page.Id, Slug = page.Slug, Title = page.Title,
                    NavLabel = page.NavLabel, NavPosition = page.NavPosition,
                    IsHidden = page.IsHidden, Role = page.Role,
                    SectionCount = page.SectionCount, ProblemCount = page.ProblemCount,
                    CreatedBy = page.CreatedBy, CreatedAt = page.CreatedAt,
                    UpdatedBy = page.UpdatedBy, UpdatedAt = page.UpdatedAt,
                };
                detail.Sections = await ReadSectionsForPageAsync(conn, null, id, validator, ct);
                return Results.Ok(detail);
            })
            .WithTags("AdminPages")
            .Produces<PageDetailDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        app.MapPatch("/admin/pages/{id:long}",
            async (long id, PatchPageRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var v = new RequestValidation();
                if (body.Slug is not null && !IsValidSlug(body.Slug))
                {
                    v.Field("slug", "must match ^[a-z0-9]+(-[a-z0-9]+)*$, 1 to 60 characters");
                }
                if (body.Title is not null &&
                    (string.IsNullOrWhiteSpace(body.Title) || body.Title.Trim().Length > 200))
                {
                    v.Field("title", "must be 1 to 200 characters");
                }
                if (body.NavLabel is { Length: > 40 })
                {
                    v.Field("navLabel", "must be null or 1 to 40 characters");
                }
                v.ThrowIfInvalid();
                if (body.Slug is not null && DocumentBuilder.ReservedSlugs.Contains(body.Slug))
                {
                    throw new ApiException(StatusCodes.Status400BadRequest,
                        ApiErrorCodes.SlugReserved, $"slug `{body.Slug}` is reserved");
                }

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                string role;
                await using (var lockRow = new NpgsqlCommand(
                    "select role from page where id = $1 for update;", conn, tx))
                {
                    lockRow.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await lockRow.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("page not found");
                    role = (string)r;
                }
                // Role pages: navLabel must stay null, isHidden false.
                if (!string.Equals(role, "none", StringComparison.Ordinal))
                {
                    if (body.NavLabel is not null && !string.IsNullOrEmpty(body.NavLabel))
                    {
                        throw new ApiException(StatusCodes.Status400BadRequest,
                            ApiErrorCodes.ValidationFailed, "role pages must keep navLabel null");
                    }
                    if (body.IsHidden is true)
                    {
                        throw new ApiException(StatusCodes.Status400BadRequest,
                            ApiErrorCodes.ValidationFailed, "role pages cannot be hidden");
                    }
                }

                var sets = new List<string>();
                var parameters = new List<NpgsqlParameter>();
                var next = 1;
                if (body.Slug is not null) { sets.Add($"slug = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Slug }); }
                if (body.Title is not null) { sets.Add($"title = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Title.Trim() }); }
                if (body.NavLabel is not null)
                {
                    sets.Add($"nav_label = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text,
                        Value = (object?)(string.IsNullOrEmpty(body.NavLabel) ? null : body.NavLabel.Trim()) ?? DBNull.Value });
                }
                if (body.NavPosition is int np) { sets.Add($"nav_position = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = np }); }
                if (body.IsHidden is bool hidden) { sets.Add($"is_hidden = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = hidden }); }
                sets.Add($"updated_by = ${next++}"); parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                sets.Add("updated_at = now()");
                var whereIdx = next;
                try
                {
                    await using var upd = new NpgsqlCommand(
                        $"update page set {string.Join(", ", sets)} where id = ${whereIdx};", conn, tx);
                    foreach (var p in parameters) upd.Parameters.Add(p);
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await upd.ExecuteNonQueryAsync(ct);
                }
                catch (PostgresException ex) when (ex.SqlState == "23505")
                {
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.SlugTaken,
                        $"slug `{body.Slug}` is already used");
                }
                await tx.CommitAsync(ct);
                var page = await ReadPageByIdAsync(conn, null, id, ct);
                return Results.Ok(page);
            })
            .WithTags("AdminPages")
            .Accepts<PatchPageRequest>("application/json")
            .Produces<PageAdminDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        app.MapDelete("/admin/pages/{id:long}",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                string? role = null;
                await using (var check = new NpgsqlCommand(
                    "select role from page where id = $1;", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("page not found");
                    role = (string)r;
                }
                if (!string.Equals(role, "none", StringComparison.Ordinal))
                {
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.PageHasRole,
                        "role pages cannot be deleted");
                }
                await using var del = new NpgsqlCommand("delete from page where id = $1;", conn);
                del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await del.ExecuteNonQueryAsync(ct);
                return Results.NoContent();
            })
            .WithTags("AdminPages")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        app.MapPut("/admin/pages/order",
            async (PageOrderRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                if (body.Ids is null || body.Ids.Count == 0)
                {
                    RequestValidation.Throw("ids", "must include every `none` page exactly once");
                }
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                // Verify the id set equals the set of `none` pages.
                var existing = new HashSet<long>();
                await using (var cmd = new NpgsqlCommand(
                    "select id from page where role = 'none' order by id;", conn, tx))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct)) existing.Add(reader.GetInt64(0));
                }
                var provided = new HashSet<long>(body.Ids!);
                if (existing.Count != provided.Count || !existing.SetEquals(provided))
                {
                    RequestValidation.Throw("ids", "must be exactly the set of `none` page ids");
                }
                for (var i = 0; i < body.Ids!.Count; i++)
                {
                    await using var upd = new NpgsqlCommand(
                        "update page set nav_position = $1 where id = $2;", conn, tx);
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = i * 10 });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.Ids[i] });
                    await upd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                var pages = await ReadAllPagesAsync(conn, null, ct);
                return Results.Ok(new ItemsResponse<PageAdminDto> { Items = pages });
            })
            .WithTags("AdminPages")
            .Accepts<PageOrderRequest>("application/json")
            .Produces<ItemsResponse<PageAdminDto>>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- Sections -----------------------------------------------------------------

    private static void MapSections(IEndpointRouteBuilder app)
    {
        // POST /admin/pages/{id}/sections
        app.MapPost("/admin/pages/{id:long}/sections",
            async (long id, CreateSectionRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, KindRegistry registry,
                   SchemaValidator validator, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                if (string.IsNullOrEmpty(body.Kind))
                {
                    RequestValidation.Throw("kind", "is required");
                }
                if (!registry.ByName.TryGetValue(body.Kind, out var kindInfo))
                {
                    throw new ApiException(StatusCodes.Status400BadRequest,
                        ApiErrorCodes.UnknownKind, $"unknown kind `{body.Kind}`");
                }
                // Section data defaults to the kind's defaults; validated at draft level.
                var dataNode = body.Data is JsonElement de && de.ValueKind != JsonValueKind.Undefined
                    ? JsonNode.Parse(de.GetRawText())
                    : kindInfo.Defaults.DeepClone();
                // Presentation defaults per contracts 4.5.
                var presentationNode = body.Presentation is PresentationDto p
                    ? PresentationToNode(p)
                    : DefaultPresentationNode();
                // Draft validation.
                var problems = validator.ValidateSectionData(body.Kind, dataNode, ValidationLevel.Draft);
                if (problems.Count > 0) throw ValidationDraftFailed(problems);
                var presentationProblems = validator.ValidatePresentation(presentationNode, ValidationLevel.Draft);
                if (presentationProblems.Count > 0) throw ValidationDraftFailed(presentationProblems);

                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                string role;
                await using (var pageCheck = new NpgsqlCommand(
                    "select role from page where id = $1 for update;", conn, tx))
                {
                    pageCheck.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await pageCheck.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("page not found");
                    role = (string)r;
                }
                // allowedRoles check. `null` means the kind fits every role.
                if (kindInfo.AllowedRoles is { Count: > 0 } allowed && !allowed.Contains(role))
                {
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.KindNotAllowed,
                        $"kind `{body.Kind}` is not allowed on a `{role}` page");
                }

                // Position defaults to the end; existing positions >= p shift down.
                int position;
                if (body.Position is int p1)
                {
                    if (p1 < 0) RequestValidation.Throw("position", "must be >= 0");
                    await using var count = new NpgsqlCommand(
                        "select count(*) from section where page_id = $1;", conn, tx);
                    count.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct) ?? 0);
                    position = Math.Min(p1, total);
                    await using var shift = new NpgsqlCommand(
                        "update section set position = position + 1 where page_id = $1 and position >= $2;", conn, tx);
                    shift.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    shift.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = position });
                    await shift.ExecuteNonQueryAsync(ct);
                }
                else
                {
                    await using var count = new NpgsqlCommand(
                        "select count(*) from section where page_id = $1;", conn, tx);
                    count.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    position = Convert.ToInt32(await count.ExecuteScalarAsync(ct) ?? 0);
                }
                long newId;
                await using (var ins = new NpgsqlCommand(@"
insert into section (page_id, kind, position, data, presentation, updated_by)
values ($1, $2, $3, $4::jsonb, $5::jsonb, $6) returning id;", conn, tx))
                {
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = body.Kind });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = position });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = dataNode?.ToJsonString() ?? "{}" });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = presentationNode?.ToJsonString() ?? "{}" });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                    newId = Convert.ToInt64(await ins.ExecuteScalarAsync(ct) ?? 0L);
                }
                await tx.CommitAsync(ct);
                var section = await ReadSectionByIdAsync(conn, null, newId, validator, ct);
                return Results.Json(section, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminSections")
            .Accepts<CreateSectionRequest>("application/json")
            .Produces<SectionAdminDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.SectionOrItem)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // PATCH /admin/sections/{id}
        app.MapPatch("/admin/sections/{id:long}",
            async (long id, PatchSectionRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, KindRegistry registry,
                   SchemaValidator validator, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                string kind;
                await using (var check = new NpgsqlCommand(
                    "select kind from section where id = $1 for update;", conn, tx))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("section not found");
                    kind = (string)r;
                }
                JsonNode? dataNode = null;
                if (body.Data is JsonElement de && de.ValueKind != JsonValueKind.Undefined)
                {
                    dataNode = JsonNode.Parse(de.GetRawText());
                    var problems = validator.ValidateSectionData(kind, dataNode, ValidationLevel.Draft);
                    if (problems.Count > 0) throw ValidationDraftFailed(problems);
                }
                JsonNode? presentationNode = null;
                if (body.Presentation is PresentationDto pd)
                {
                    presentationNode = PresentationToNode(pd);
                    var problems = validator.ValidatePresentation(presentationNode, ValidationLevel.Draft);
                    if (problems.Count > 0) throw ValidationDraftFailed(problems);
                }
                var sets = new List<string>();
                var parameters = new List<NpgsqlParameter>();
                var next = 1;
                if (dataNode is not null)
                {
                    sets.Add($"data = ${next++}::jsonb");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = dataNode.ToJsonString() });
                }
                if (presentationNode is not null)
                {
                    sets.Add($"presentation = ${next++}::jsonb");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = presentationNode.ToJsonString() });
                }
                if (body.IsHidden is bool hidden)
                {
                    sets.Add($"is_hidden = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = hidden });
                }
                sets.Add($"updated_by = ${next++}");
                parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                sets.Add("updated_at = now()");
                var whereIdx = next;
                await using (var upd = new NpgsqlCommand(
                    $"update section set {string.Join(", ", sets)} where id = ${whereIdx};", conn, tx))
                {
                    foreach (var p in parameters) upd.Parameters.Add(p);
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await upd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                var section = await ReadSectionByIdAsync(conn, null, id, validator, ct);
                return Results.Ok(section);
            })
            .WithTags("AdminSections")
            .Accepts<PatchSectionRequest>("application/json")
            .Produces<SectionAdminDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.SectionOrItem)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // DELETE /admin/sections/{id}
        app.MapDelete("/admin/sections/{id:long}",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                long? pageId;
                await using (var del = new NpgsqlCommand(
                    "delete from section where id = $1 returning page_id;", conn, tx))
                {
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await del.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("section not found");
                    pageId = Convert.ToInt64(r);
                }
                await CompactSectionsAsync(conn, tx, pageId.Value, ct);
                await tx.CommitAsync(ct);
                return Results.NoContent();
            })
            .WithTags("AdminSections")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // POST /admin/sections/{id}/duplicate
        app.MapPost("/admin/sections/{id:long}/duplicate",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections,
                   SchemaValidator validator, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                long pageId;
                int position;
                await using (var check = new NpgsqlCommand(
                    "select page_id, position from section where id = $1 for update;", conn, tx))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using var reader = await check.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) throw NotFound("section not found");
                    pageId = reader.GetInt64(0);
                    position = reader.GetInt32(1);
                }
                // Shift existing sections after `position` down by one.
                await using (var shift = new NpgsqlCommand(
                    "update section set position = position + 1 where page_id = $1 and position > $2;", conn, tx))
                {
                    shift.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
                    shift.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = position });
                    await shift.ExecuteNonQueryAsync(ct);
                }
                long newId;
                await using (var dup = new NpgsqlCommand(@"
insert into section (page_id, kind, position, is_hidden, data, presentation, updated_by)
select page_id, kind, position + 1, is_hidden, data, presentation, $2 from section where id = $1
returning id;", conn, tx))
                {
                    dup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    dup.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                    newId = Convert.ToInt64(await dup.ExecuteScalarAsync(ct) ?? 0L);
                }
                // Duplicate every item under the source section.
                await using (var dupItems = new NpgsqlCommand(@"
insert into section_item (section_id, position, is_hidden, data, updated_by)
select $2, position, is_hidden, data, $3 from section_item where section_id = $1;", conn, tx))
                {
                    dupItems.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    dupItems.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = newId });
                    dupItems.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                    await dupItems.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                var section = await ReadSectionByIdAsync(conn, null, newId, validator, ct);
                return Results.Json(section, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminSections")
            .Produces<SectionAdminDto>(StatusCodes.Status201Created)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // POST /admin/sections/{id}/move
        app.MapPost("/admin/sections/{id:long}/move",
            async (long id, MoveSectionRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, KindRegistry registry,
                   SchemaValidator validator, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                if (body.Position < 0) RequestValidation.Throw("position", "must be >= 0");
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                long fromPage;
                string kind;
                await using (var check = new NpgsqlCommand(
                    "select page_id, kind from section where id = $1 for update;", conn, tx))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using var reader = await check.ExecuteReaderAsync(ct);
                    if (!await reader.ReadAsync(ct)) throw NotFound("section not found");
                    fromPage = reader.GetInt64(0);
                    kind = reader.GetString(1);
                }
                string targetRole;
                await using (var targetCheck = new NpgsqlCommand(
                    "select role from page where id = $1 for update;", conn, tx))
                {
                    targetCheck.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.PageId });
                    var r = await targetCheck.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("target page not found");
                    targetRole = (string)r;
                }
                if (registry.ByName.TryGetValue(kind, out var kindInfo) &&
                    kindInfo.AllowedRoles is { Count: > 0 } allowed && !allowed.Contains(targetRole))
                {
                    throw new ApiException(StatusCodes.Status409Conflict, ApiErrorCodes.KindNotAllowed,
                        $"kind `{kind}` is not allowed on a `{targetRole}` page");
                }
                // Make room at target `position` on the target page.
                await using (var shift = new NpgsqlCommand(
                    "update section set position = position + 1 where page_id = $1 and position >= $2 and id <> $3;", conn, tx))
                {
                    shift.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.PageId });
                    shift.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = body.Position });
                    shift.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await shift.ExecuteNonQueryAsync(ct);
                }
                await using (var move = new NpgsqlCommand(@"
update section set page_id = $1, position = $2, updated_by = $3, updated_at = now()
where id = $4;", conn, tx))
                {
                    move.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.PageId });
                    move.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = body.Position });
                    move.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                    move.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await move.ExecuteNonQueryAsync(ct);
                }
                await CompactSectionsAsync(conn, tx, fromPage, ct);
                if (fromPage != body.PageId) await CompactSectionsAsync(conn, tx, body.PageId, ct);
                await tx.CommitAsync(ct);
                var section = await ReadSectionByIdAsync(conn, null, id, validator, ct);
                return Results.Ok(section);
            })
            .WithTags("AdminSections")
            .Accepts<MoveSectionRequest>("application/json")
            .Produces<SectionAdminDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // PUT /admin/pages/{id}/sections/order
        app.MapPut("/admin/pages/{id:long}/sections/order",
            async (long id, SectionOrderRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections,
                   SchemaValidator validator, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                if (body.Ids is null) RequestValidation.Throw("ids", "must include every section id");
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                string role;
                await using (var pageCheck = new NpgsqlCommand(
                    "select role from page where id = $1 for update;", conn, tx))
                {
                    pageCheck.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await pageCheck.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("page not found");
                    role = (string)r;
                }
                var existing = new HashSet<long>();
                await using (var read = new NpgsqlCommand(
                    "select id from section where page_id = $1;", conn, tx))
                {
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) existing.Add(reader.GetInt64(0));
                }
                var provided = new HashSet<long>(body.Ids!);
                if (existing.Count != provided.Count || !existing.SetEquals(provided))
                {
                    RequestValidation.Throw("ids", "must be exactly the section ids on this page");
                }
                // Two-phase update to avoid the section_page_position index collision
                // when swapping positions.
                await using (var offset = new NpgsqlCommand(
                    "update section set position = position + 100000 where page_id = $1;", conn, tx))
                {
                    offset.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await offset.ExecuteNonQueryAsync(ct);
                }
                for (var i = 0; i < body.Ids!.Count; i++)
                {
                    await using var upd = new NpgsqlCommand(
                        "update section set position = $1 where id = $2 and page_id = $3;", conn, tx);
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = i });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.Ids[i] });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await upd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                var page = await ReadPageByIdAsync(conn, null, id, ct);
                if (page is null) throw NotFound("page not found");
                var detail = new PageDetailDto
                {
                    Id = page.Id, Slug = page.Slug, Title = page.Title,
                    NavLabel = page.NavLabel, NavPosition = page.NavPosition,
                    IsHidden = page.IsHidden, Role = page.Role,
                    SectionCount = page.SectionCount, ProblemCount = page.ProblemCount,
                    CreatedBy = page.CreatedBy, CreatedAt = page.CreatedAt,
                    UpdatedBy = page.UpdatedBy, UpdatedAt = page.UpdatedAt,
                };
                detail.Sections = await ReadSectionsForPageAsync(conn, null, id, validator, ct);
                return Results.Ok(detail);
            })
            .WithTags("AdminSections")
            .Accepts<SectionOrderRequest>("application/json")
            .Produces<PageDetailDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- Items --------------------------------------------------------------------

    private static void MapItems(IEndpointRouteBuilder app)
    {
        // POST /admin/sections/{id}/items
        app.MapPost("/admin/sections/{id:long}/items",
            async (long id, CreateSectionItemRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, KindRegistry registry,
                   SchemaValidator validator, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                string kind;
                await using (var check = new NpgsqlCommand(
                    "select kind from section where id = $1 for update;", conn, tx))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("section not found");
                    kind = (string)r;
                }
                if (!registry.ByName.TryGetValue(kind, out var kindInfo) || !kindInfo.HasItems)
                {
                    throw new ApiException(StatusCodes.Status400BadRequest,
                        ApiErrorCodes.ValidationFailed, $"kind `{kind}` has no items");
                }
                var dataNode = body.Data is JsonElement de && de.ValueKind != JsonValueKind.Undefined
                    ? JsonNode.Parse(de.GetRawText())
                    : kindInfo.ItemDefaults?.DeepClone();
                var problems = validator.ValidateItemData(kind, dataNode, ValidationLevel.Draft);
                if (problems.Count > 0) throw ValidationDraftFailed(problems);

                int position;
                if (body.Position is int p1)
                {
                    if (p1 < 0) RequestValidation.Throw("position", "must be >= 0");
                    await using var count = new NpgsqlCommand(
                        "select count(*) from section_item where section_id = $1;", conn, tx);
                    count.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var total = Convert.ToInt32(await count.ExecuteScalarAsync(ct) ?? 0);
                    position = Math.Min(p1, total);
                    await using var shift = new NpgsqlCommand(
                        "update section_item set position = position + 1 where section_id = $1 and position >= $2;", conn, tx);
                    shift.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    shift.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = position });
                    await shift.ExecuteNonQueryAsync(ct);
                }
                else
                {
                    await using var count = new NpgsqlCommand(
                        "select count(*) from section_item where section_id = $1;", conn, tx);
                    count.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    position = Convert.ToInt32(await count.ExecuteScalarAsync(ct) ?? 0);
                }
                long newId;
                await using (var ins = new NpgsqlCommand(@"
insert into section_item (section_id, position, data, updated_by)
values ($1, $2, $3::jsonb, $4) returning id;", conn, tx))
                {
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = position });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = dataNode?.ToJsonString() ?? "{}" });
                    ins.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                    newId = Convert.ToInt64(await ins.ExecuteScalarAsync(ct) ?? 0L);
                }
                await tx.CommitAsync(ct);
                var item = await ReadItemByIdAsync(conn, null, newId, kind, validator, ct);
                return Results.Json(item, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminSections")
            .Accepts<CreateSectionItemRequest>("application/json")
            .Produces<SectionItemAdminDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.SectionOrItem)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // PATCH /admin/items/{id}
        app.MapPatch("/admin/items/{id:long}",
            async (long id, PatchSectionItemRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, KindRegistry registry,
                   SchemaValidator validator, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                string kind;
                await using (var check = new NpgsqlCommand(@"
select s.kind from section_item i join section s on s.id = i.section_id
where i.id = $1 for update;", conn, tx))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("item not found");
                    kind = (string)r;
                }
                JsonNode? dataNode = null;
                if (body.Data is JsonElement de && de.ValueKind != JsonValueKind.Undefined)
                {
                    dataNode = JsonNode.Parse(de.GetRawText());
                    var problems = validator.ValidateItemData(kind, dataNode, ValidationLevel.Draft);
                    if (problems.Count > 0) throw ValidationDraftFailed(problems);
                }
                var sets = new List<string>();
                var parameters = new List<NpgsqlParameter>();
                var next = 1;
                if (dataNode is not null)
                {
                    sets.Add($"data = ${next++}::jsonb");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = dataNode.ToJsonString() });
                }
                if (body.IsHidden is bool hidden)
                {
                    sets.Add($"is_hidden = ${next++}");
                    parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Boolean, Value = hidden });
                }
                sets.Add($"updated_by = ${next++}");
                parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                sets.Add("updated_at = now()");
                var whereIdx = next;
                await using (var upd = new NpgsqlCommand(
                    $"update section_item set {string.Join(", ", sets)} where id = ${whereIdx};", conn, tx))
                {
                    foreach (var p in parameters) upd.Parameters.Add(p);
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await upd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                var item = await ReadItemByIdAsync(conn, null, id, kind, validator, ct);
                return Results.Ok(item);
            })
            .WithTags("AdminSections")
            .Accepts<PatchSectionItemRequest>("application/json")
            .Produces<SectionItemAdminDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.SectionOrItem)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // DELETE /admin/items/{id}
        app.MapDelete("/admin/items/{id:long}",
            async (long id, HttpContext ctx, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                long? sectionId;
                await using (var del = new NpgsqlCommand(
                    "delete from section_item where id = $1 returning section_id;", conn, tx))
                {
                    del.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await del.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("item not found");
                    sectionId = Convert.ToInt64(r);
                }
                await CompactItemsAsync(conn, tx, sectionId.Value, ct);
                await tx.CommitAsync(ct);
                return Results.NoContent();
            })
            .WithTags("AdminSections")
            .Produces(StatusCodes.Status204NoContent)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // PUT /admin/sections/{id}/items/order
        app.MapPut("/admin/sections/{id:long}/items/order",
            async (long id, ItemOrderRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, KindRegistry registry,
                   SchemaValidator validator, CancellationToken ct) =>
            {
                _ = AdminHelpers.RequireAdminEmail(ctx);
                if (body.Ids is null) RequestValidation.Throw("ids", "required");
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                string kind;
                await using (var check = new NpgsqlCommand(
                    "select kind from section where id = $1 for update;", conn, tx))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull) throw NotFound("section not found");
                    kind = (string)r;
                }
                var existing = new HashSet<long>();
                await using (var read = new NpgsqlCommand(
                    "select id from section_item where section_id = $1;", conn, tx))
                {
                    read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await using var reader = await read.ExecuteReaderAsync(ct);
                    while (await reader.ReadAsync(ct)) existing.Add(reader.GetInt64(0));
                }
                var provided = new HashSet<long>(body.Ids!);
                if (existing.Count != provided.Count || !existing.SetEquals(provided))
                {
                    RequestValidation.Throw("ids", "must be exactly this section's item ids");
                }
                await using (var offset = new NpgsqlCommand(
                    "update section_item set position = position + 100000 where section_id = $1;", conn, tx))
                {
                    offset.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await offset.ExecuteNonQueryAsync(ct);
                }
                for (var i = 0; i < body.Ids!.Count; i++)
                {
                    await using var upd = new NpgsqlCommand(
                        "update section_item set position = $1 where id = $2 and section_id = $3;", conn, tx);
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = i });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = body.Ids[i] });
                    upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                    await upd.ExecuteNonQueryAsync(ct);
                }
                await tx.CommitAsync(ct);
                var section = await ReadSectionByIdAsync(conn, null, id, validator, ct);
                return Results.Ok(section);
            })
            .WithTags("AdminSections")
            .Accepts<ItemOrderRequest>("application/json")
            .Produces<SectionAdminDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- Site settings ------------------------------------------------------------

    private static void MapSiteSettings(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/site-settings",
            async (WmsfoConnectionStrings connections, SchemaValidator validator, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var dto = await ReadSiteSettingsAsync(conn, null, validator, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminSiteSettings")
            .Produces<SiteSettingsDraftDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        app.MapPut("/admin/site-settings",
            async (SiteSettingsUpdateRequest body, HttpContext ctx,
                   WmsfoConnectionStrings connections, SchemaValidator validator, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var node = body.Data.ValueKind == JsonValueKind.Undefined
                    ? new JsonObject()
                    : JsonNode.Parse(body.Data.GetRawText());
                var problems = validator.ValidateSiteSettings(node, ValidationLevel.Draft);
                if (problems.Count > 0) throw ValidationDraftFailed(problems);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(@"
update site_setting_draft set data = $1::jsonb, updated_by = $2, updated_at = now() where id = 1;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = node?.ToJsonString() ?? "{}" });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                await cmd.ExecuteNonQueryAsync(ct);
                var dto = await ReadSiteSettingsAsync(conn, null, validator, ct);
                return Results.Ok(dto);
            })
            .WithTags("AdminSiteSettings")
            .Accepts<SiteSettingsUpdateRequest>("application/json")
            .Produces<SiteSettingsDraftDto>(StatusCodes.Status200OK)
            .WithBodyLimit(BodyLimits.SectionOrItem)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- GET /admin/content/draft, /admin/content/status --------------------------

    private static void MapContentDraftAndStatus(IEndpointRouteBuilder app)
    {
        app.MapGet("/admin/content/draft",
            async (WmsfoConnectionStrings connections, WmsfoOptions options,
                   IconLibrary? iconLibrary, DocumentBuilder builder, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var load = await builder.LoadAsync(conn, null, includeHidden: false, ct);
                // Media map for referenced ids, whatever state.
                var mediaIds = DocumentBuilder.CollectReferencedMediaIds(load.WorkingSet);
                var mediaMap = await BuildMediaMapAsync(conn, null, mediaIds, options, ct);
                var iconsMap = new SortedDictionary<string, string>(StringComparer.Ordinal);
                if (iconLibrary is not null)
                {
                    foreach (var kv in iconLibrary.Map) iconsMap[kv.Key] = kv.Value;
                }
                var bundle = new ContentBundleDto
                {
                    Content = JsonSerializer.SerializeToElement(load.Document, CanonicalJson.Options),
                    Media = mediaMap,
                    Icons = iconsMap,
                };
                return Results.Ok(bundle);
            })
            .WithTags("AdminContent")
            .Produces<ContentBundleDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        app.MapGet("/admin/content/status",
            async (WmsfoConnectionStrings connections, WmsfoOptions options,
                   DocumentBuilder builder, IconLibrary? iconLibrary,
                   SchemaValidator validator, KindRegistry registry, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var load = await builder.LoadAsync(conn, null, includeHidden: false, ct);
                // Publish-level validation of every section, item, and settings.
                var problems = await BuildPublishProblemsAsync(load, validator, registry,
                    new ReferenceChecker(iconLibrary), conn, null, ct);

                // Hash the working set's canonical bytes.
                var bytes = CanonicalJson.SerializeToUtf8Bytes(load.Document);
                var sha = CanonicalJson.Sha256Hex(bytes);

                ContentVersionInfoDto? published = null;
                await using (var cmd = new NpgsqlCommand(@"
select cv.id, cv.sha256, cv.label, cv.published_by, cv.published_at, cv.document
from content_version cv
order by cv.id desc limit 1;", conn))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        var doc = reader.GetString(5);
                        var (pc, sc) = CountPagesAndSections(doc);
                        published = new ContentVersionInfoDto
                        {
                            Id = reader.GetInt64(0),
                            Sha256 = reader.GetString(1).Trim(),
                            Label = reader.IsDBNull(2) ? null : reader.GetString(2),
                            PublishedBy = reader.GetString(3),
                            PublishedAt = reader.GetFieldValue<DateTimeOffset>(4),
                            PageCount = pc,
                            SectionCount = sc,
                        };
                    }
                }
                var status = new ContentStatusDto
                {
                    Published = published,
                    DraftSha256 = sha,
                    HasUnpublishedChanges = published is null || !string.Equals(published.Sha256, sha, StringComparison.Ordinal),
                    Problems = problems.ToList(),
                    DraftUpdatedAt = load.DraftUpdatedAt,
                };
                return Results.Ok(status);
            })
            .WithTags("AdminContent")
            .Produces<ContentStatusDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- POST /admin/content/publish, /admin/content/versions, restore ---

    private static void MapPublishVersionsRestore(IEndpointRouteBuilder app)
    {
        // POST /admin/content/publish (contracts 4.5 Content, api.md 11a.3).
        app.MapPost("/admin/content/publish",
            async (PublishContentRequest? body, HttpContext ctx, Publisher publisher, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var label = body?.Label;
                if (label is not null)
                {
                    label = label.Trim();
                    if (label.Length == 0) label = null;
                    else if (label.Length > 200)
                    {
                        RequestValidation.Throw("label", "must be null or 1 to 200 characters");
                    }
                }
                var result = await publisher.PublishAsync(email, label, ct);
                return Results.Json(result.Version, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminContent")
            .Accepts<PublishContentRequest>("application/json")
            .Produces<ContentVersionInfoDto>(StatusCodes.Status201Created)
            .WithBodyLimit(BodyLimits.JsonDefault)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // GET /admin/content/versions - the newest 50 rows (list, newest first).
        app.MapGet("/admin/content/versions",
            async (WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var items = new List<ContentVersionInfoDto>();
                await using var cmd = new NpgsqlCommand(@"
select id, sha256, label, published_by, published_at, document
from content_version order by id desc limit 50;", conn);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var docJson = reader.GetString(5);
                    var (pc, sc) = CountPagesAndSections(docJson);
                    items.Add(new ContentVersionInfoDto
                    {
                        Id = reader.GetInt64(0),
                        Sha256 = reader.GetString(1).Trim(),
                        Label = reader.IsDBNull(2) ? null : reader.GetString(2),
                        PublishedBy = reader.GetString(3),
                        PublishedAt = reader.GetFieldValue<DateTimeOffset>(4),
                        PageCount = pc,
                        SectionCount = sc,
                    });
                }
                return Results.Ok(new ItemsResponse<ContentVersionInfoDto> { Items = items });
            })
            .WithTags("AdminContent")
            .Produces<ItemsResponse<ContentVersionInfoDto>>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // GET /admin/content/versions/{id} - the full detail with the document.
        app.MapGet("/admin/content/versions/{id:long}",
            async (long id, WmsfoConnectionStrings connections, CancellationToken ct) =>
            {
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(@"
select id, sha256, label, published_by, published_at, document
from content_version where id = $1;", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (!await reader.ReadAsync(ct))
                {
                    throw NotFound($"content version `{id}` not found");
                }
                var docJson = reader.GetString(5);
                var (pc, sc) = CountPagesAndSections(docJson);
                var dto = new ContentVersionDetailDto
                {
                    Id = reader.GetInt64(0),
                    Sha256 = reader.GetString(1).Trim(),
                    Label = reader.IsDBNull(2) ? null : reader.GetString(2),
                    PublishedBy = reader.GetString(3),
                    PublishedAt = reader.GetFieldValue<DateTimeOffset>(4),
                    PageCount = pc,
                    SectionCount = sc,
                    Document = JsonElementFromString(docJson),
                };
                return Results.Ok(dto);
            })
            .WithTags("AdminContent")
            .Produces<ContentVersionDetailDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);

        // POST /admin/content/versions/{id}/restore (sql.md 8.20).
        app.MapPost("/admin/content/versions/{id:long}/restore",
            async (long id, HttpContext ctx, Restorer restorer,
                   WmsfoConnectionStrings connections, WmsfoOptions options,
                   DocumentBuilder builder, IconLibrary? iconLibrary,
                   SchemaValidator validator, KindRegistry registry, CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                await restorer.RestoreAsync(id, email, ct);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                var load = await builder.LoadAsync(conn, null, includeHidden: false, ct);
                var problems = await BuildPublishProblemsAsync(load, validator, registry,
                    new ReferenceChecker(iconLibrary), conn, null, ct);
                var bytes = CanonicalJson.SerializeToUtf8Bytes(load.Document);
                var sha = CanonicalJson.Sha256Hex(bytes);
                ContentVersionInfoDto? published = null;
                await using (var cmd = new NpgsqlCommand(@"
select id, sha256, label, published_by, published_at, document
from content_version order by id desc limit 1;", conn))
                await using (var reader = await cmd.ExecuteReaderAsync(ct))
                {
                    if (await reader.ReadAsync(ct))
                    {
                        var doc = reader.GetString(5);
                        var (pc, sc) = CountPagesAndSections(doc);
                        published = new ContentVersionInfoDto
                        {
                            Id = reader.GetInt64(0),
                            Sha256 = reader.GetString(1).Trim(),
                            Label = reader.IsDBNull(2) ? null : reader.GetString(2),
                            PublishedBy = reader.GetString(3),
                            PublishedAt = reader.GetFieldValue<DateTimeOffset>(4),
                            PageCount = pc,
                            SectionCount = sc,
                        };
                    }
                }
                var status = new ContentStatusDto
                {
                    Published = published,
                    DraftSha256 = sha,
                    HasUnpublishedChanges = published is null || !string.Equals(published.Sha256, sha, StringComparison.Ordinal),
                    Problems = problems.ToList(),
                    DraftUpdatedAt = load.DraftUpdatedAt,
                };
                return Results.Ok(status);
            })
            .WithTags("AdminContent")
            .Produces<ContentStatusDto>(StatusCodes.Status200OK)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- POST /admin/content/preview-token ---

    private static void MapPreviewToken(IEndpointRouteBuilder app)
    {
        app.MapPost("/admin/content/preview-token",
            async (HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options,
                   CancellationToken ct) =>
            {
                var email = AdminHelpers.RequireAdminEmail(ctx);
                var minted = Keys.MintPreviewToken();
                var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using var cmd = new NpgsqlCommand(@"
insert into preview_token (token_hash, created_by, expires_at)
values ($1, $2, $3);", conn);
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = minted.Hash });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = email });
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = expiresAt });
                await cmd.ExecuteNonQueryAsync(ct);
                var dto = new PreviewTokenDto
                {
                    Token = minted.Token,
                    Url = options.PublicApiBaseUrl.TrimEnd('/') + "/preview/document?token=" + minted.Token,
                    ExpiresAt = expiresAt,
                };
                return Results.Json(dto, statusCode: StatusCodes.Status201Created);
            })
            .WithTags("AdminContent")
            .Produces<PreviewTokenDto>(StatusCodes.Status201Created)
            .RequireAuthorization(AuthPolicies.Editor)
            .RequireRateLimiting(RateLimitPolicies.AdminPerPerson);
    }

    // --- GET /preview/document ---

    private static void MapPreviewDocument(IEndpointRouteBuilder app)
    {
        app.MapGet("/preview/document",
            async (HttpContext ctx, WmsfoConnectionStrings connections, WmsfoOptions options,
                   IconLibrary? iconLibrary, DocumentBuilder builder, CancellationToken ct) =>
            {
                var token = ctx.Request.Query["token"].ToString();
                if (string.IsNullOrEmpty(token))
                {
                    throw new ApiException(StatusCodes.Status404NotFound,
                        ApiErrorCodes.PreviewTokenInvalid, "preview token missing or expired");
                }
                var hash = Keys.Hash(token);
                await using var conn = new NpgsqlConnection(connections.App);
                await conn.OpenAsync(ct);
                await using (var check = new NpgsqlCommand(
                    "select 1 from preview_token where token_hash = $1 and expires_at > now();", conn))
                {
                    check.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = hash });
                    var r = await check.ExecuteScalarAsync(ct);
                    if (r is null || r is DBNull)
                    {
                        throw new ApiException(StatusCodes.Status404NotFound,
                            ApiErrorCodes.PreviewTokenInvalid, "preview token missing or expired");
                    }
                }
                var load = await builder.LoadAsync(conn, null, includeHidden: false, ct);
                var mediaIds = DocumentBuilder.CollectReferencedMediaIds(load.WorkingSet);
                var mediaMap = await BuildMediaMapAsync(conn, null, mediaIds, options, ct);
                var iconsMap = new SortedDictionary<string, string>(StringComparer.Ordinal);
                if (iconLibrary is not null)
                {
                    foreach (var kv in iconLibrary.Map) iconsMap[kv.Key] = kv.Value;
                }
                var bundle = new ContentBundleDto
                {
                    Content = JsonSerializer.SerializeToElement(load.Document, CanonicalJson.Options),
                    Media = mediaMap,
                    Icons = iconsMap,
                };
                return Results.Ok(bundle);
            })
            .WithTags("Public")
            .Produces<ContentBundleDto>(StatusCodes.Status200OK)
            .RequireRateLimiting(RateLimitPolicies.PreviewPerIp);
    }

    // --- helpers ------------------------------------------------------------------

    private static ApiException NotFound(string message) =>
        new(StatusCodes.Status404NotFound, ApiErrorCodes.NotFound, message);

    private static ApiException ValidationDraftFailed(IReadOnlyList<ProblemDto> problems)
    {
        var first = problems[0];
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in problems)
        {
            var key = string.IsNullOrEmpty(p.Path) ? "data" : p.Path;
            fields.TryAdd(key, p.Message);
        }
        return new ApiException(StatusCodes.Status400BadRequest, ApiErrorCodes.ValidationFailed,
            $"{first.Path} {first.Message}", new ValidationDetails(fields));
    }

    private static void ValidatePageBody(string slug, string title, string? navLabel, bool isCreate)
    {
        var v = new RequestValidation();
        if (isCreate)
        {
            if (string.IsNullOrEmpty(slug)) v.Field("slug", "is required");
            else if (!IsValidSlug(slug))
                v.Field("slug", "must match ^[a-z0-9]+(-[a-z0-9]+)*$, 1 to 60 characters");
            if (string.IsNullOrWhiteSpace(title))
                v.Field("title", "is required, 1 to 200 characters");
            else if (title.Trim().Length > 200)
                v.Field("title", "must be 1 to 200 characters");
        }
        if (navLabel is { Length: > 40 })
            v.Field("navLabel", "must be null or 1 to 40 characters");
        v.ThrowIfInvalid();

        if (DocumentBuilder.ReservedSlugs.Contains(slug))
        {
            throw new ApiException(StatusCodes.Status400BadRequest,
                ApiErrorCodes.SlugReserved, $"slug `{slug}` is reserved");
        }
    }

    private static bool IsValidSlug(string slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > 60) return false;
        var sawChar = false;
        var previousHyphen = false;
        foreach (var c in slug)
        {
            if (c == '-')
            {
                if (!sawChar || previousHyphen) return false;
                previousHyphen = true;
                sawChar = false;
                continue;
            }
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
            {
                sawChar = true;
                previousHyphen = false;
                continue;
            }
            return false;
        }
        return sawChar && !previousHyphen;
    }

    private static JsonNode DefaultPresentationNode() => new JsonObject
    {
        ["width"] = "wide",
        ["align"] = "start",
        ["background"] = new JsonObject { ["kind"] = "none" },
        ["spacing"] = "normal",
        ["iconBefore"] = null,
        ["iconAfter"] = null,
        ["anchor"] = null,
    };

    private static JsonNode PresentationToNode(PresentationDto p)
    {
        var background = p.Background.ValueKind == JsonValueKind.Undefined
            ? new JsonObject { ["kind"] = "none" }
            : JsonNode.Parse(p.Background.GetRawText());
        var obj = new JsonObject
        {
            ["width"] = p.Width,
            ["align"] = p.Align,
            ["background"] = background,
            ["spacing"] = p.Spacing,
            ["iconBefore"] = p.IconBefore is null ? null : new JsonObject { ["source"] = p.IconBefore.Source, ["id"] = p.IconBefore.Id },
            ["iconAfter"] = p.IconAfter is null ? null : new JsonObject { ["source"] = p.IconAfter.Source, ["id"] = p.IconAfter.Id },
            ["anchor"] = p.Anchor,
        };
        return obj;
    }

    private static async Task<List<PageAdminDto>> ReadAllPagesAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var pages = new List<PageAdminDto>();
        // Order: role pages first in status order, then `none` by nav_position, id.
        await using var cmd = new NpgsqlCommand(@"
select p.id, p.slug, p.title, p.nav_label, p.nav_position, p.is_hidden, p.role,
       p.created_by, p.created_at, p.updated_by, p.updated_at,
       coalesce(sc.n, 0) as section_count
from page p
left join (
  select page_id, count(*) as n from section group by page_id
) sc on sc.page_id = p.id
order by case p.role
  when 'no_event' then 0 when 'planned' then 1 when 'scheduled' then 2
  when 'live' then 3 when 'ended' then 4 when 'cancelled' then 5 else 6 end,
  p.nav_position, p.id;", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pages.Add(new PageAdminDto
            {
                Id = reader.GetInt64(0),
                Slug = reader.GetString(1),
                Title = reader.GetString(2),
                NavLabel = reader.IsDBNull(3) ? null : reader.GetString(3),
                NavPosition = reader.GetInt32(4),
                IsHidden = reader.GetBoolean(5),
                Role = reader.GetString(6),
                CreatedBy = reader.GetString(7),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
                UpdatedBy = reader.GetString(9),
                UpdatedAt = reader.GetFieldValue<DateTimeOffset>(10),
                SectionCount = reader.GetInt32(11),
                ProblemCount = 0,
            });
        }
        return pages;
    }

    private static async Task<PageAdminDto?> ReadPageByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select p.id, p.slug, p.title, p.nav_label, p.nav_position, p.is_hidden, p.role,
       p.created_by, p.created_at, p.updated_by, p.updated_at,
       (select count(*) from section where page_id = p.id) as section_count
from page p where p.id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new PageAdminDto
        {
            Id = reader.GetInt64(0),
            Slug = reader.GetString(1),
            Title = reader.GetString(2),
            NavLabel = reader.IsDBNull(3) ? null : reader.GetString(3),
            NavPosition = reader.GetInt32(4),
            IsHidden = reader.GetBoolean(5),
            Role = reader.GetString(6),
            CreatedBy = reader.GetString(7),
            CreatedAt = reader.GetFieldValue<DateTimeOffset>(8),
            UpdatedBy = reader.GetString(9),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(10),
            SectionCount = Convert.ToInt32(reader.GetInt64(11)),
            ProblemCount = 0,
        };
    }

    private static async Task<List<SectionAdminDto>> ReadSectionsForPageAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long pageId,
        SchemaValidator validator, CancellationToken ct)
    {
        var sections = new List<SectionAdminDto>();
        await using (var cmd = new NpgsqlCommand(@"
select id, page_id, kind, position, is_hidden, data, presentation, updated_by, updated_at
from section where page_id = $1
order by position, id;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                sections.Add(BuildSectionDto(reader, validator));
            }
        }
        // Load every item in one query keyed on section_id.
        var itemsBySection = new Dictionary<long, List<SectionItemAdminDto>>();
        if (sections.Count > 0)
        {
            var sectionIds = sections.Select(s => s.Id).ToArray();
            await using var cmd = new NpgsqlCommand(@"
select i.id, i.section_id, i.position, i.is_hidden, i.data, i.updated_by, i.updated_at, s.kind
from section_item i join section s on s.id = i.section_id
where i.section_id = any($1)
order by i.section_id, i.position, i.id;", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
                Value = sectionIds,
            });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var sectionId = reader.GetInt64(1);
                var kind = reader.GetString(7);
                var itemDto = new SectionItemAdminDto
                {
                    Id = reader.GetInt64(0),
                    SectionId = sectionId,
                    Position = reader.GetInt32(2),
                    IsHidden = reader.GetBoolean(3),
                    Data = JsonElementFromString(reader.GetString(4)),
                    UpdatedBy = reader.GetString(5),
                    UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
                };
                itemDto.Problems = ValidateItemPublish(validator, kind, itemDto.Data);
                if (!itemsBySection.TryGetValue(sectionId, out var list))
                {
                    list = new List<SectionItemAdminDto>();
                    itemsBySection[sectionId] = list;
                }
                list.Add(itemDto);
            }
        }
        foreach (var s in sections)
        {
            if (itemsBySection.TryGetValue(s.Id, out var list))
                s.Items = list;
        }
        return sections;
    }

    private static async Task<SectionAdminDto?> ReadSectionByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id,
        SchemaValidator validator, CancellationToken ct)
    {
        SectionAdminDto? section = null;
        await using (var cmd = new NpgsqlCommand(@"
select id, page_id, kind, position, is_hidden, data, presentation, updated_by, updated_at
from section where id = $1;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
            {
                section = BuildSectionDto(reader, validator);
            }
        }
        if (section is null) return null;
        // Items.
        var items = new List<SectionItemAdminDto>();
        await using (var cmd = new NpgsqlCommand(@"
select id, section_id, position, is_hidden, data, updated_by, updated_at
from section_item where section_id = $1 order by position, id;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                items.Add(new SectionItemAdminDto
                {
                    Id = reader.GetInt64(0),
                    SectionId = reader.GetInt64(1),
                    Position = reader.GetInt32(2),
                    IsHidden = reader.GetBoolean(3),
                    Data = JsonElementFromString(reader.GetString(4)),
                    UpdatedBy = reader.GetString(5),
                    UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
                });
            }
        }
        foreach (var i in items)
        {
            i.Problems = ValidateItemPublish(validator, section.Kind, i.Data);
        }
        section.Items = items;
        return section;
    }

    private static async Task<SectionItemAdminDto?> ReadItemByIdAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long id, string kind,
        SchemaValidator validator, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select id, section_id, position, is_hidden, data, updated_by, updated_at
from section_item where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var item = new SectionItemAdminDto
        {
            Id = reader.GetInt64(0),
            SectionId = reader.GetInt64(1),
            Position = reader.GetInt32(2),
            IsHidden = reader.GetBoolean(3),
            Data = JsonElementFromString(reader.GetString(4)),
            UpdatedBy = reader.GetString(5),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(6),
        };
        item.Problems = ValidateItemPublish(validator, kind, item.Data);
        return item;
    }

    private static SectionAdminDto BuildSectionDto(NpgsqlDataReader reader, SchemaValidator validator)
    {
        var kind = reader.GetString(2);
        var dataJson = reader.GetString(5);
        var presentationJson = reader.GetString(6);
        var dto = new SectionAdminDto
        {
            Id = reader.GetInt64(0),
            PageId = reader.GetInt64(1),
            Kind = kind,
            Position = reader.GetInt32(3),
            IsHidden = reader.GetBoolean(4),
            Data = JsonElementFromString(dataJson),
            Presentation = ParsePresentationDto(presentationJson),
            UpdatedBy = reader.GetString(7),
            UpdatedAt = reader.GetFieldValue<DateTimeOffset>(8),
        };
        // Publish-level structural problems for this section (references are added
        // by ContentStatus which runs the ReferenceChecker over the whole tree).
        var problems = new List<ProblemDto>();
        problems.AddRange(validator.ValidateSectionData(kind, JsonNode.Parse(dataJson), ValidationLevel.Publish));
        problems.AddRange(validator.ValidatePresentation(JsonNode.Parse(presentationJson), ValidationLevel.Publish));
        dto.Problems = problems;
        return dto;
    }

    private static List<ProblemDto> ValidateItemPublish(SchemaValidator validator, string kind, JsonElement data)
    {
        var node = JsonNode.Parse(data.GetRawText());
        return validator.ValidateItemData(kind, node, ValidationLevel.Publish).ToList();
    }

    private static async Task<SiteSettingsDraftDto> ReadSiteSettingsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx,
        SchemaValidator validator, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(
            "select data, updated_by, updated_at from site_setting_draft where id = 1;", conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return new SiteSettingsDraftDto { Data = JsonElementFromString("{}") };
        }
        var raw = reader.GetString(0);
        var dto = new SiteSettingsDraftDto
        {
            Data = JsonElementFromString(raw),
            UpdatedBy = reader.IsDBNull(1) ? null : reader.GetString(1),
            UpdatedAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
        };
        dto.Problems = validator.ValidateSiteSettings(JsonNode.Parse(raw), ValidationLevel.Publish).ToList();
        return dto;
    }

    private static async Task<IReadOnlyList<ProblemRefDto>> BuildPublishProblemsAsync(
        DocumentBuilder.LoadResult load, SchemaValidator validator, KindRegistry registry,
        ReferenceChecker refChecker, NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var problems = new List<ProblemRefDto>();
        // Settings.
        foreach (var p in validator.ValidateSiteSettings(load.SettingsData, ValidationLevel.Publish))
        {
            problems.Add(new ProblemRefDto { Path = p.Path, Message = p.Message });
        }
        // Every visible page/section/item.
        foreach (var page in load.WorkingSet.Pages)
        {
            foreach (var section in page.Sections)
            {
                foreach (var p in validator.ValidateSectionData(section.Kind, section.Data, ValidationLevel.Publish))
                {
                    problems.Add(new ProblemRefDto
                    {
                        Path = "/data" + p.Path,
                        Message = p.Message,
                        PageId = page.Id,
                        SectionId = section.Id,
                    });
                }
                foreach (var p in validator.ValidatePresentation(section.Presentation, ValidationLevel.Publish))
                {
                    problems.Add(new ProblemRefDto
                    {
                        Path = "/presentation" + p.Path,
                        Message = p.Message,
                        PageId = page.Id,
                        SectionId = section.Id,
                    });
                }
                foreach (var item in section.Items)
                {
                    foreach (var p in validator.ValidateItemData(section.Kind, item.Data, ValidationLevel.Publish))
                    {
                        problems.Add(new ProblemRefDto
                        {
                            Path = "/data" + p.Path,
                            Message = p.Message,
                            PageId = page.Id,
                            SectionId = section.Id,
                            ItemId = item.Id,
                        });
                    }
                }
            }
        }
        // Semantic checks.
        var refProblems = await refChecker.CheckAsync(load.WorkingSet, conn, tx, ct);
        problems.AddRange(refProblems);
        return problems;
    }

    private static async Task<SortedDictionary<string, MediaEntry>> BuildMediaMapAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid[] ids, WmsfoOptions options, CancellationToken ct)
    {
        var map = new SortedDictionary<string, MediaEntry>(StringComparer.Ordinal);
        if (ids.Length == 0) return map;
        var cdn = options.CdnBaseUrl.TrimEnd('/');
        await using var cmd = new NpgsqlCommand(@"
select id, s3_key, kind, width, height, alt, variants
from media_asset where id = any($1) order by id;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            Value = ids,
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetGuid(0);
            var key = reader.GetString(1);
            var kind = reader.GetString(2);
            int? w = reader.IsDBNull(3) ? null : reader.GetInt32(3);
            int? h = reader.IsDBNull(4) ? null : reader.GetInt32(4);
            var alt = reader.GetString(5);
            var variants = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using var doc = JsonDocument.Parse(reader.GetString(6));
            foreach (var e in doc.RootElement.EnumerateObject())
            {
                variants[e.Name] = cdn + "/" + e.Value.GetString();
            }
            map[id.ToString()] = new MediaEntry
            {
                Url = cdn + "/" + key,
                Kind = kind,
                Width = w,
                Height = h,
                Alt = alt,
                Variants = variants,
            };
        }
        return map;
    }

    private static async Task CompactSectionsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long pageId, CancellationToken ct)
    {
        // Two-phase compaction to avoid transient duplicate (page_id, position) rows if
        // there is a unique constraint. section_page_position is not unique but two-phase
        // is defensive.
        await using (var offset = new NpgsqlCommand(
            "update section set position = position + 100000 where page_id = $1;", conn, tx))
        {
            offset.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
            await offset.ExecuteNonQueryAsync(ct);
        }
        await using (var compact = new NpgsqlCommand(@"
update section s set position = r.rn - 1
from (
  select id, row_number() over (order by position, id) rn
  from section where page_id = $1
) r
where s.id = r.id and s.page_id = $1;", conn, tx))
        {
            compact.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
            await compact.ExecuteNonQueryAsync(ct);
        }
    }

    private static async Task CompactItemsAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, long sectionId, CancellationToken ct)
    {
        await using (var offset = new NpgsqlCommand(
            "update section_item set position = position + 100000 where section_id = $1;", conn, tx))
        {
            offset.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sectionId });
            await offset.ExecuteNonQueryAsync(ct);
        }
        await using (var compact = new NpgsqlCommand(@"
update section_item s set position = r.rn - 1
from (
  select id, row_number() over (order by position, id) rn
  from section_item where section_id = $1
) r
where s.id = r.id and s.section_id = $1;", conn, tx))
        {
            compact.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sectionId });
            await compact.ExecuteNonQueryAsync(ct);
        }
    }

    private static (int Pages, int Sections) CountPagesAndSections(string documentJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(documentJson);
            if (!doc.RootElement.TryGetProperty("pages", out var pagesEl) || pagesEl.ValueKind != JsonValueKind.Array)
                return (0, 0);
            var pages = pagesEl.GetArrayLength();
            var sections = 0;
            foreach (var p in pagesEl.EnumerateArray())
            {
                if (p.TryGetProperty("sections", out var s) && s.ValueKind == JsonValueKind.Array)
                {
                    sections += s.GetArrayLength();
                }
            }
            return (pages, sections);
        }
        catch (JsonException)
        {
            return (0, 0);
        }
    }

    private static JsonElement JsonElementFromString(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private static PresentationDto ParsePresentationDto(string raw)
    {
        try
        {
            var dto = JsonSerializer.Deserialize<PresentationDto>(raw, CanonicalJson.Options);
            if (dto is not null) return dto;
        }
        catch (JsonException)
        {
            // Fall through.
        }
        return new PresentationDto
        {
            Width = "wide",
            Align = "start",
            Background = JsonElementFromString("{\"kind\":\"none\"}"),
            Spacing = "normal",
        };
    }
}
