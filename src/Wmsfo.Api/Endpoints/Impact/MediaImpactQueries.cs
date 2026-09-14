using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints.Impact;

// api.md 5b: a media delete clears every reference. FK cascades handle
// sponsor.logo_media_id and event.route_image_media_id (set null). Content
// JSON (sections, items, site settings, content_version.media_ids) and cookie
// type icons live outside those FKs, so ApplyAsync clears them explicitly.
public static class MediaImpactQueries
{
    public static async Task<DeleteImpactDto> PreviewAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
    {
        var impact = new DeleteImpactDto();
        var uuidParams = ImpactHelpers.UuidId(id);
        var textParams = ImpactHelpers.TextId(id.ToString());

        // sponsor.logo_media_id
        var sponsors = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "sponsor", "sponsor", "name", "logo_media_id = $1",
            uuidParams, ct).ConfigureAwait(false);
        if (sponsors is not null) impact.Unlinks.Add(sponsors);

        // event.route_image_media_id
        var events = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "event", "event", "name", "route_image_media_id = $1",
            uuidParams, ct).ConfigureAwait(false);
        if (events is not null) impact.Unlinks.Add(events);

        // cookie_type icons (media-sourced).
        var cookieTypes = await ImpactHelpers.CountAndNamesAsync(
            conn, tx, "cookie_type", "cookie_type", "name",
            "icon->>'source' = 'media' and icon->>'id' = $1",
            textParams, ct).ConfigureAwait(false);
        if (cookieTypes is not null) impact.Unlinks.Add(cookieTypes);

        // Content draft pages: any section or item whose JSON mentions the id.
        var pages = await CountAndNamesPagesAsync(conn, tx, id, ct).ConfigureAwait(false);
        if (pages is not null) impact.Unlinks.Add(pages);

        // Site settings draft: single row, count-only.
        var siteSettings = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "site_setting_draft", "site_setting_draft",
            "id = 1 and data::text like '%' || $1 || '%'",
            textParams, ct).ConfigureAwait(false);
        if (siteSettings is not null) impact.Unlinks.Add(new ImpactGroupDto
        {
            Entity = "site_settings", Count = siteSettings.Count, Names = new List<string>(),
        });

        // Published content_versions referencing the id.
        var versions = await ImpactHelpers.CountOnlyAsync(
            conn, tx, "content_version", "content_version",
            "$1 = any(media_ids)", uuidParams, ct).ConfigureAwait(false);
        if (versions is not null) impact.Unlinks.Add(new ImpactGroupDto
        {
            Entity = "content_version", Count = versions.Count, Names = new List<string>(),
        });

        return impact;
    }

    // ApplyAsync clears content JSON references (sections, items, site settings,
    // cookie_type icon), and clears content_version.media_ids entries.
    // sponsor.logo_media_id and event.route_image_media_id are handled by the
    // FK cascades (set null).
    public static async Task ApplyAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, Guid id, CancellationToken ct)
    {
        var idText = id.ToString();

        // Sections whose data or presentation JSON references the id.
        await ClearJsonReferencesAsync(conn, tx, "section", new[] { "data", "presentation" }, idText, ct)
            .ConfigureAwait(false);
        // Section items.
        await ClearJsonReferencesAsync(conn, tx, "section_item", new[] { "data" }, idText, ct)
            .ConfigureAwait(false);
        // Site settings draft.
        await using (var upd = new NpgsqlCommand(@"
update site_setting_draft set data = (data::text)::jsonb, updated_at = now()
where id = 1 and data::text like '%' || $1 || '%';", conn, tx))
        {
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = idText });
            // The literal substitution is done in ClearJsonSubstringAsync.
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        await ClearSiteSettingsJsonAsync(conn, tx, idText, ct).ConfigureAwait(false);

        // Cookie type icons whose media id matches.
        await using (var upd = new NpgsqlCommand(@"
update cookie_type set icon = null, updated_at = now()
where icon->>'source' = 'media' and icon->>'id' = $1;", conn, tx))
        {
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = idText });
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // content_version.media_ids: drop the id from the array where present.
        await using (var upd = new NpgsqlCommand(@"
update content_version set media_ids = array_remove(media_ids, $1)
where $1 = any(media_ids);", conn, tx))
        {
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<ImpactGroupDto?> CountAndNamesPagesAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
    {
        var idText = id.ToString();
        int count = 0;
        await using (var cmd = new NpgsqlCommand(@"
select count(*)::int from page p
where exists (
  select 1 from section s
  where s.page_id = p.id
    and (s.data::text like '%' || $1 || '%' or s.presentation::text like '%' || $1 || '%')
)
or exists (
  select 1 from section s
  join section_item i on i.section_id = s.id
  where s.page_id = p.id and i.data::text like '%' || $1 || '%'
);", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = idText });
            var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            count = r is int i ? i : Convert.ToInt32(r);
        }
        if (count == 0) return null;
        var names = new List<string>();
        await using (var cmd = new NpgsqlCommand(@"
select title from page p
where exists (
  select 1 from section s
  where s.page_id = p.id
    and (s.data::text like '%' || $1 || '%' or s.presentation::text like '%' || $1 || '%')
)
or exists (
  select 1 from section s
  join section_item i on i.section_id = s.id
  where s.page_id = p.id and i.data::text like '%' || $1 || '%'
)
order by p.id
limit 10;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = idText });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                names.Add(reader.IsDBNull(0) ? "" : reader.GetString(0));
        }
        return new ImpactGroupDto { Entity = "page", Count = count, Names = names };
    }

    // For each column in the table, replace the media id substring with empty
    // string when the JSON text contains it. The MediaUsage query in api.md
    // 11.5 uses this same substring approach: media ids are UUIDs and cannot
    // collide with anything else in the JSON.
    private static async Task ClearJsonReferencesAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string table, string[] columns, string idText, CancellationToken ct)
    {
        foreach (var col in columns)
        {
            await using var upd = new NpgsqlCommand(
                $"update {table} set {col} = replace({col}::text, $1, '')::jsonb, updated_at = now() " +
                $"where {col}::text like '%' || $1 || '%';", conn, tx);
            upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = idText });
            await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task ClearSiteSettingsJsonAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string idText, CancellationToken ct)
    {
        await using var upd = new NpgsqlCommand(@"
update site_setting_draft set data = replace(data::text, $1, '')::jsonb, updated_at = now()
where id = 1 and data::text like '%' || $1 || '%';", conn, tx);
        upd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = idText });
        await upd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
