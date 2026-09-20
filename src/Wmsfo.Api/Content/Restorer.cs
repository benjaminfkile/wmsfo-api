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
// page (sections and items cascade), then re-inserts each page, section, and
// item. The site settings row is updated in place. Neither content_version nor
// snapshot are touched - restore replaces the draft, it does not publish.
public sealed class Restorer
{
    private readonly WmsfoConnectionStrings _connections;
    private readonly ILogger<Restorer> _logger;

    public Restorer(WmsfoConnectionStrings connections, ILogger<Restorer> logger)
    {
        _connections = connections;
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

        // Places and printed codes point at pages by id, and the pages are about
        // to get new ids. Remember each link by the page's slug so it can be put
        // back onto the restored page with the same slug (contracts 4.5 Content).
        await using (var keepLinks = new NpgsqlCommand(@"
create temp table restore_page_link on commit drop as
select 'place'::text as owner, pl.id as owner_id, pg.slug
from place pl join page pg on pg.id = pl.opens_page_id
union all
select 'qr_code'::text, q.id, pg.slug
from qr_code q join page pg on pg.id = q.opens_page_id;", conn, tx))
        {
            await keepLinks.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Delete the working set. Sections and items cascade off page; the page
        // links on places and codes go null and are put back below.
        await using (var delPages = new NpgsqlCommand("delete from page;", conn, tx))
        {
            await delPages.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Recreate pages, sections, and items with new ids. Positions come from
        // the array index (the stored document is already in canonical order).
        foreach (var page in document.Pages)
        {
            long pageId;
            await using (var insertPage = new NpgsqlCommand(@"
insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by)
values ($1, $2, $3, $4, false, $5, $6, $6) returning id;", conn, tx))
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
        await using (var relink = new NpgsqlCommand(@"
update place pl set opens_page_id = pg.id
from restore_page_link l join page pg on pg.slug = l.slug
where l.owner = 'place' and l.owner_id = pl.id;
update qr_code q set opens_page_id = pg.id
from restore_page_link l join page pg on pg.slug = l.slug
where l.owner = 'qr_code' and l.owner_id = q.id;", conn, tx))
        {
            await relink.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

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
