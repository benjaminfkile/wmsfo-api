using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Content;

// sql.md 8.16 step 1: seed the working set from the starter content when
// `select 1 from page limit 1` returns nothing. All rows are `created_by =
// updated_by = 'seed'`. Idempotent - inserts nothing on later boots.
public sealed class StarterContent
{
    private readonly ILogger<StarterContent> _logger;

    public StarterContent(ILogger<StarterContent> logger)
    {
        _logger = logger;
    }

    public async Task EnsureSeededAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        long pageCount;
        await using (var check = new NpgsqlCommand("select count(*) from page;", conn))
        {
            pageCount = Convert.ToInt64(await check.ExecuteScalarAsync(ct).ConfigureAwait(false));
        }
        if (pageCount > 0) return;

        var document = StarterContentBuilder.Build();

        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var page in document.Pages)
        {
            long pageId;
            await using (var insertPage = new NpgsqlCommand(@"
insert into page (slug, title, nav_label, nav_position, is_hidden, role, created_by, updated_by)
values ($1, $2, $3, $4, false, $5, 'seed', 'seed') returning id;", conn, tx))
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
values ($1, $2, $3, $4::jsonb, $5::jsonb, 'seed') returning id;", conn, tx))
                {
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = section.Kind });
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = position });
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = dataJson });
                    insertSection.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = presentationJson });
                    sectionId = Convert.ToInt64(await insertSection.ExecuteScalarAsync(ct).ConfigureAwait(false));
                }
                position++;

                var itemPos = 0;
                foreach (var item in section.Items)
                {
                    var itemDataJson = item.Data is null ? "{}" : item.Data.ToJsonString(CanonicalJson.Options);
                    await using (var insertItem = new NpgsqlCommand(@"
insert into section_item (section_id, position, data, updated_by)
values ($1, $2, $3::jsonb, 'seed');", conn, tx))
                    {
                        insertItem.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = sectionId });
                        insertItem.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = itemPos });
                        insertItem.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = itemDataJson });
                        await insertItem.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                    itemPos++;
                }
            }
        }

        // Site settings - insert from the document. The table has a single seeded
        // row (id = 1); write the document's settings into it.
        var settingsJson = JsonSerializer.Serialize(document.Settings, CanonicalJson.Options);
        await using (var upd = new NpgsqlCommand(@"
update site_setting_draft set data = $1::jsonb, updated_by = 'seed', updated_at = now() where id = 1;",
            conn, tx))
        {
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = settingsJson });
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("starter content seeded: pages={PageCount}", document.Pages.Count);
    }
}
