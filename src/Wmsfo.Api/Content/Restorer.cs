using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Content;

// api.md 11a.3 / sql.md 8.20: POST /admin/content/versions/{id}/restore.
// Copies a stored content_version document into the working set: deletes every
// page (sections and items cascade), then re-inserts each page, section, and
// item. The site settings row is updated in place. Neither content_version nor
// snapshot are touched — restore replaces the draft, it does not publish.
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
    // not exist. Returns the version info that was restored.
    public async Task RestoreAsync(long versionId, string restoredBy, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_connections.App);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        string? documentJson = null;
        await using (var read = new NpgsqlCommand(
            "select document from content_version where id = $1;", conn, tx))
        {
            read.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = versionId });
            var r = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (r is null || r is DBNull)
            {
                throw new ApiException(StatusCodes.Status404NotFound,
                    ApiErrorCodes.NotFound, $"content version `{versionId}` not found");
            }
            documentJson = (string)r;
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

        // Delete the working set. Sections and items cascade off page.
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

        await tx.CommitAsync(ct).ConfigureAwait(false);
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
