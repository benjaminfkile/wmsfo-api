using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Content;

// api.md 11a.3 / sql.md 8.20: POST /admin/content/versions/{id}/restore.
// Copies a stored content_version document into the working set: deletes every
// page (sections and items cascade) but a role page whose role the document
// has no page for, then re-inserts each page, section, and item. The site
// settings row is updated in place. Neither content_version nor snapshot are
// touched - restore replaces the draft, it does not publish.
public sealed class Restorer
{
    private readonly WmsfoConnectionStrings _connections;
    private readonly KindRegistry _registry;
    private readonly ILogger<Restorer> _logger;

    public Restorer(WmsfoConnectionStrings connections, KindRegistry registry, ILogger<Restorer> logger)
    {
        _connections = connections;
        _registry = registry;
        _logger = logger;
    }

    // Restores the version with the given id. Throws 404 if the version does
    // not exist. Records one audit_log row against `content_version` inside the
    // same transaction (api.md 5a): action `restore`, before is null, after is
    // the version info that was restored.
    public async Task<ContentVersionInfoDto> RestoreAsync(long versionId, string restoredBy,
        AuditRecorder audit, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        string? documentJson = null;
        string sha = "";
        string? label = null;
        string publishedBy = "";
        DateTimeOffset publishedAt = default;
        await using (var read = new NpgsqlCommand(
            "select document, sha256, label, published_by, published_at from content_version where id = $1;", conn, tx))
        {
            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = versionId });
            await using var reader = await read.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                throw new ApiException(StatusCodes.Status404NotFound,
                    ApiErrorCodes.NotFound, $"content version `{versionId}` not found");
            }
            documentJson = reader.GetString(0);
            sha = reader.GetString(1).Trim();
            label = reader.IsDBNull(2) ? null : reader.GetString(2);
            publishedBy = reader.GetString(3);
            publishedAt = reader.GetFieldValue<DateTimeOffset>(4);
        }

        ContentDocument document;
        try
        {
            document = JsonSerializer.Deserialize<ContentDocument>(documentJson!, RestoreReadOptions)
                ?? throw new JsonException("empty document");
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "restore failed to parse content_version {Id}", versionId);
            throw new ApiException(StatusCodes.Status500InternalServerError,
                ApiErrorCodes.InternalError, "stored version document is not readable");
        }

        // A section keeps only the data keys its kind's current schema lists,
        // so a restore never reinstates a key the next publish would refuse.
        var stripped = StripUnknownSectionKeys(document);
        _logger.LogInformation(
            "restore stripped {Count} section data keys the current schemas do not allow; versionId={VersionId}",
            stripped, versionId);

        // Places and printed codes point at pages by id, and the pages are about
        // to get new ids. Remember each link by the page's slug so it can be put
        // back onto the restored page with the same slug (contracts 4.5 Content).
        var placeLinks = await ReadPageLinksAsync(conn, tx,
            "select pl.id, pg.slug from place pl join page pg on pg.id = pl.opens_page_id;", ct).ConfigureAwait(false);
        var codeLinks = await ReadPageLinksAsync(conn, tx,
            "select q.id, pg.slug from qr_code q join page pg on pg.id = q.opens_page_id;", ct).ConfigureAwait(false);

        // Delete the working set, except a role page whose role the document
        // has no page for: it stays with its sections so every role keeps its
        // page. Sections and items cascade off page; the page links on places
        // and codes go null and are put back below.
        var keptRoles = DocumentBuilder.PageRoles
            .Where(role => !document.Pages.Any(p => string.Equals(p.Role, role, StringComparison.Ordinal)))
            .ToArray();
        await using (var delPages = new NpgsqlCommand("delete from page where role <> all($1);", conn, tx))
        {
            delPages.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
                Value = keptRoles,
            });
            await delPages.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Recreate pages, sections, and items with new ids. Positions come from
        // the array index (the stored document is already in canonical order).
        foreach (var page in document.Pages)
        {
            long pageId;
            await using (var insertPage = new NpgsqlCommand(@"
insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by, icon)
values ($1, $2, $3, $4, false, $5, $6, $6, $7::jsonb) returning id;", conn, tx))
            {
                insertPage.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = page.Slug });
                insertPage.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = page.Title });
                insertPage.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Text,
                    Value = string.IsNullOrEmpty(page.NavLabel) ? DBNull.Value : (object)page.NavLabel,
                });
                insertPage.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = page.NavPosition });
                insertPage.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = page.Role });
                insertPage.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = restoredBy });
                insertPage.Parameters.Add(new NpgsqlParameter
                {
                    NpgsqlDbType = NpgsqlDbType.Jsonb,
                    Value = page.Icon is null ? DBNull.Value : (object)JsonSerializer.Serialize(page.Icon, CanonicalJson.Options),
                });
                pageId = Convert.ToInt64(await insertPage.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }

            var position = 0;
            foreach (var section in page.Sections)
            {
                var presentationJson = JsonSerializer.Serialize(section.Presentation, CanonicalJson.Options);
                var dataJson = section.Data is null ? "{}" : section.Data.ToJsonString(CanonicalJson.Options);
                long sectionId;
                await using (var insertSection = new NpgsqlCommand(@"
insert into section (page_id, kind, position, data, presentation, updated_by)
values ($1, $2, $3, $4::jsonb, $5::jsonb, $6) returning id;", conn, tx))
                {
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = section.Kind });
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = position });
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = dataJson });
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = presentationJson });
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = restoredBy });
                    sectionId = Convert.ToInt64(await insertSection.ExecuteScalarAsync(ct).ConfigureAwait(false));
                }
                position++;

                var itemPos = 0;
                foreach (var item in section.Items)
                {
                    var itemDataJson = item.Data is null ? "{}" : item.Data.ToJsonString(CanonicalJson.Options);
                    await using (var insertItem = new NpgsqlCommand(@"
insert into section_item (section_id, position, data, updated_by)
values ($1, $2, $3::jsonb, $4);", conn, tx))
                    {
                        insertItem.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sectionId });
                        insertItem.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = itemPos });
                        insertItem.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = itemDataJson });
                        insertItem.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = restoredBy });
                        await insertItem.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                    itemPos++;
                }
            }
        }

        // Put the page links back by slug. A link whose slug is not in the
        // restored document stays null, as it would after deleting that page.
        await RelinkPagesAsync(conn, tx, "place", placeLinks, ct).ConfigureAwait(false);
        await RelinkPagesAsync(conn, tx, "qr_code", codeLinks, ct).ConfigureAwait(false);

        // Site settings.
        var settingsJson = JsonSerializer.Serialize(document.Settings, CanonicalJson.Options);
        await using (var upd = new NpgsqlCommand(@"
update site_setting_draft set data = $1::jsonb, updated_by = $2, updated_at = now() where id = 1;",
            conn, tx))
        {
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = settingsJson });
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = restoredBy });
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var (pageCount, sectionCount) = CountPagesAndSections(document);
        var info = new ContentVersionInfoDto
        {
            Id = versionId,
            Sha256 = sha,
            Label = label,
            PublishedBy = publishedBy,
            PublishedAt = publishedAt,
            PageCount = pageCount,
            SectionCount = sectionCount,
        };
        var stamp = await audit.RecordAsync(conn, tx, "restore", "content_version",
            versionId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            before: null, after: info, ct).ConfigureAwait(false);
        info.Audit = stamp;

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return info;
    }

    // Removes, from every section's data, each key the kind's schema does not
    // list under `properties` when that schema has `additionalProperties:
    // false`. A kind the registry does not know is left as stored. Returns the
    // number of keys removed.
    private int StripUnknownSectionKeys(ContentDocument document)
    {
        var removed = 0;
        foreach (var page in document.Pages)
        {
            foreach (var section in page.Sections)
            {
                if (section.Data is not JsonObject data) continue;
                if (!_registry.ByName.TryGetValue(section.Kind, out var info)) continue;
                if (info.SchemaNode is not JsonObject schema) continue;
                if (schema["additionalProperties"] is not JsonValue additional
                    || additional.GetValueKind() != JsonValueKind.False) continue;
                var allowed = schema["properties"] as JsonObject;
                foreach (var key in data.Select(kv => kv.Key).ToList())
                {
                    if (allowed is not null && allowed.ContainsKey(key)) continue;
                    data.Remove(key);
                    removed++;
                }
            }
        }
        return removed;
    }

    // The rows of one table that open a page, as (row id, page slug).
    private static async Task<List<(long Id, string Slug)>> ReadPageLinksAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        var links = new List<(long, string)>();
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            links.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        return links;
    }

    // Points each remembered row at the page that now carries its slug. The table
    // name is one of two literals chosen by the caller, never input.
    private static async Task RelinkPagesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string table,
        List<(long Id, string Slug)> links, CancellationToken ct)
    {
        if (links.Count == 0) return;
        await using var cmd = new NpgsqlCommand($@"
update {table} t set opens_page_id = pg.id
from unnest($1, $2) as l(owner_id, slug) join page pg on pg.slug = l.slug
where t.id = l.owner_id;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            Value = links.Select(l => l.Id).ToArray(),
        });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text,
            Value = links.Select(l => l.Slug).ToArray(),
        });
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static (int Pages, int Sections) CountPagesAndSections(ContentDocument doc)
    {
        var pages = doc.Pages?.Count ?? 0;
        var sections = 0;
        if (doc.Pages is not null)
        {
            foreach (var p in doc.Pages) sections += p.Sections?.Count ?? 0;
        }
        return (pages, sections);
    }

    private static readonly JsonSerializerOptions RestoreReadOptions = BuildReadOptions();
    private static JsonSerializerOptions BuildReadOptions()
    {
        var o = new JsonSerializerOptions(CanonicalJson.Options)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
        };
        return o;
    }
}
