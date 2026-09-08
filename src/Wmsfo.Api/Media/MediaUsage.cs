using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Media;

// api.md 11.5 / sql.md 8.22: the five usage statements run in order to build
// the MediaUsage DTO for GET /admin/media/{id}/usage and the DELETE pre-check.
// The text-match usage queries are exact because the id is a UUID that cannot
// occur by accident; the working set is small enough for a scan.
public static class MediaUsage
{
    public static async Task<MediaUsageDto> ForAsync(NpgsqlConnection conn, Guid id, CancellationToken ct)
    {
        var idText = id.ToString();
        var usage = new MediaUsageDto();

        // 1. Draft pages that mention the id anywhere in a section's data /
        //    presentation, or in an item's data.
        await using (var cmd = new NpgsqlCommand(@"
select p.id, p.slug, p.title
from page p
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
order by p.nav_position, p.id;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = idText });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                usage.DraftPages.Add(new MediaUsagePageRef
                {
                    Id = reader.GetInt64(0),
                    Slug = reader.GetString(1),
                    Title = reader.GetString(2),
                });
            }
        }

        // 2. Version count: how many retained content_versions include the id.
        await using (var cmd = new NpgsqlCommand(
            "select count(*)::int from content_version where $1 = any(media_ids);", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
            var r = await cmd.ExecuteScalarAsync(ct);
            usage.VersionCount = r is int i ? i : Convert.ToInt32(r);
        }

        // 3. Sponsors: logo reference.
        await using (var cmd = new NpgsqlCommand(
            "select id, name from sponsor where logo_media_id = $1 order by id;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = id });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                usage.Sponsors.Add(new MediaUsageSponsorRef
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                });
            }
        }

        // 4. Cookie types: media-sourced icons.
        await using (var cmd = new NpgsqlCommand(@"
select id, name from cookie_type
where icon->>'source' = 'media' and icon->>'id' = $1
order by id;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = idText });
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                usage.CookieTypes.Add(new MediaUsageCookieTypeRef
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                });
            }
        }

        // 5. Site settings draft mentions the id.
        await using (var cmd = new NpgsqlCommand(
            "select data::text like '%' || $1 || '%' from site_setting_draft where id = 1;", conn))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = idText });
            var r = await cmd.ExecuteScalarAsync(ct);
            usage.SiteSettings = r is bool b && b;
        }

        return usage;
    }

    public static bool IsInUse(MediaUsageDto usage) =>
        usage.DraftPages.Count > 0
        || usage.VersionCount > 0
        || usage.Sponsors.Count > 0
        || usage.CookieTypes.Count > 0
        || usage.SiteSettings;
}
