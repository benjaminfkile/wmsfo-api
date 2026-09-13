using System.Globalization;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints;

// Read-side helpers for the place endpoints. The tree walk uses recursive
// CTEs at most 32 levels deep (contracts 4.5a, api.md 11b).
public static class PlaceRead
{
    public static async Task<PlaceDto?> ByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select id, parent_id, name, description, opens_page_id, forward_url,
       lat, lng, accuracy_m, pin_source, pinned_by, pinned_at,
       created_by, created_at, updated_at
from place where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var dto = ReadRow(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        await LoadPathAsync(conn, tx, dto, ct).ConfigureAwait(false);
        await LoadPinAsync(conn, tx, dto, ct).ConfigureAwait(false);
        await LoadOpensAsync(conn, tx, dto, ct).ConfigureAwait(false);
        await LoadCodesAsync(conn, tx, dto, ct).ConfigureAwait(false);
        await LoadSubtreeScansAsync(conn, tx, dto, ct).ConfigureAwait(false);
        await LoadAuditStampAsync(conn, tx, dto, ct).ConfigureAwait(false);
        return dto;
    }

    public static async Task<List<PlaceDto>> TreeAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        // Read all places, then sort in tree order: parents before children,
        // siblings by name (case-insensitive).
        await using var cmd = new NpgsqlCommand(@"
select id, parent_id, name, description, opens_page_id, forward_url,
       lat, lng, accuracy_m, pin_source, pinned_by, pinned_at,
       created_by, created_at, updated_at
from place;", conn, tx);
        var rows = new List<PlaceDto>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) rows.Add(ReadRow(reader));
        }
        // Build children map. Roots are keyed by 0 since the dictionary needs a
        // non-null key type; place ids start at 1 so there is no collision.
        const long RootKey = 0;
        var byParent = new Dictionary<long, List<PlaceDto>>();
        foreach (var p in rows)
        {
            var key = p.ParentId ?? RootKey;
            if (!byParent.TryGetValue(key, out var list))
            {
                list = new List<PlaceDto>();
                byParent[key] = list;
            }
            list.Add(p);
        }
        foreach (var kv in byParent)
        {
            kv.Value.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        }
        var ordered = new List<PlaceDto>();
        void Walk(long parentId)
        {
            if (!byParent.TryGetValue(parentId, out var list)) return;
            foreach (var p in list)
            {
                ordered.Add(p);
                Walk(p.Id);
            }
        }
        Walk(RootKey);
        // Enrich each row.
        foreach (var dto in ordered)
        {
            await LoadPathAsync(conn, tx, dto, ct).ConfigureAwait(false);
            await LoadPinAsync(conn, tx, dto, ct).ConfigureAwait(false);
            await LoadOpensAsync(conn, tx, dto, ct).ConfigureAwait(false);
            await LoadCodesAsync(conn, tx, dto, ct).ConfigureAwait(false);
            await LoadSubtreeScansAsync(conn, tx, dto, ct).ConfigureAwait(false);
            await LoadAuditStampAsync(conn, tx, dto, ct).ConfigureAwait(false);
        }
        return ordered;
    }

    private static PlaceDto ReadRow(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        ParentId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
        Name = reader.GetString(2),
        Description = reader.GetString(3),
        OpensPageId = reader.IsDBNull(4) ? null : reader.GetInt64(4),
        ForwardUrl = reader.IsDBNull(5) ? null : reader.GetString(5),
        Location = reader.IsDBNull(6) ? null : new PlaceLocationDto
        {
            Lat = reader.GetDouble(6),
            Lng = reader.GetDouble(7),
            AccuracyM = reader.IsDBNull(8) ? null : reader.GetDouble(8),
            Source = reader.IsDBNull(9) ? "" : reader.GetString(9),
            PinnedBy = reader.IsDBNull(10) ? "" : reader.GetString(10),
            PinnedAt = reader.IsDBNull(11) ? default : reader.GetFieldValue<DateTimeOffset>(11),
        },
        CreatedBy = reader.GetString(12),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(13),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(14),
    };

    private static async Task LoadPathAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, PlaceDto dto, CancellationToken ct)
    {
        dto.Path = await QrRead.PlacePathAsync(conn, tx, dto.Id, ct).ConfigureAwait(false);
    }

    private static async Task LoadPinAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, PlaceDto dto, CancellationToken ct)
    {
        if (dto.Location is not null)
        {
            dto.Pin = new PlacePinResolvedDto
            {
                Lat = dto.Location.Lat,
                Lng = dto.Location.Lng,
                FromPlaceId = dto.Id,
            };
            return;
        }
        // Walk up ancestors up to 32 levels for the first one with a pin.
        await using var cmd = new NpgsqlCommand(@"
with recursive chain(id, parent_id, lat, lng, depth) as (
  select id, parent_id, lat, lng, 0 from place where id = $1
  union all
  select p.id, p.parent_id, p.lat, p.lng, c.depth + 1
  from place p join chain c on p.id = c.parent_id where c.depth < 32
)
select id, lat, lng from chain where lat is not null order by depth limit 1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = dto.Id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return;
        dto.Pin = new PlacePinResolvedDto
        {
            FromPlaceId = reader.GetInt64(0),
            Lat = reader.GetDouble(1),
            Lng = reader.GetDouble(2),
        };
    }

    private static async Task LoadOpensAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, PlaceDto dto, CancellationToken ct)
    {
        long? resolvedPage = dto.OpensPageId;
        string? resolvedUrl = dto.ForwardUrl;
        string source = "home";
        if (resolvedPage is not null || resolvedUrl is not null)
        {
            source = "place";
        }
        else
        {
            var (p, u) = await ResolveOpensViaAncestorsAsync(conn, tx, dto.ParentId, ct).ConfigureAwait(false);
            if (p is not null || u is not null) source = "ancestor";
            resolvedPage = p; resolvedUrl = u;
        }
        var opens = new OpensDto();
        if (resolvedPage is long pageId)
        {
            await using var pageCmd = new NpgsqlCommand(
                "select slug, role, is_hidden from page where id = $1;", conn, tx);
            pageCmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
            await using var reader = await pageCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var slug = reader.GetString(0);
                var role = reader.GetString(1);
                var hidden = reader.GetBoolean(2);
                if (!hidden)
                {
                    opens.Kind = "page";
                    opens.PageId = pageId;
                    opens.Slug = role == "none" ? slug : "/";
                }
            }
        }
        else if (resolvedUrl is not null)
        {
            opens.Kind = "url";
            opens.Url = resolvedUrl;
        }
        dto.Opens = opens;
        dto.OpensSource = source;
    }

    // contracts 4.5a: resolve opens starting from a specific place, walking up.
    public static async Task<(long? PageId, string? Url)> ResolveOpensAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long startPlaceId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
with recursive chain(id, parent_id, opens_page_id, forward_url, depth) as (
  select id, parent_id, opens_page_id, forward_url, 0
  from place where id = $1
  union all
  select p.id, p.parent_id, p.opens_page_id, p.forward_url, c.depth + 1
  from place p join chain c on p.id = c.parent_id where c.depth < 32
)
select opens_page_id, forward_url
from chain
where opens_page_id is not null or forward_url is not null
order by depth limit 1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = startPlaceId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return (null, null);
        return (reader.IsDBNull(0) ? null : reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    private static async Task<(long? PageId, string? Url)> ResolveOpensViaAncestorsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long? startParentId, CancellationToken ct)
    {
        if (startParentId is null) return (null, null);
        return await ResolveOpensAsync(conn, tx, startParentId.Value, ct).ConfigureAwait(false);
    }

    private static async Task LoadCodesAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, PlaceDto dto, CancellationToken ct)
    {
        // Every code currently attached to this place (open attachment).
        await using var cmd = new NpgsqlCommand(@"
select c.id, c.tag from qr_code c
join qr_attachment a on a.qr_code_id = c.id and a.to_at is null
where a.place_id = $1
order by c.tag asc;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = dto.Id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            dto.Codes.Add(new PlaceCodeRefDto { Id = reader.GetInt64(0), Tag = reader.GetString(1) });
        }
    }

    private static async Task LoadSubtreeScansAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, PlaceDto dto, CancellationToken ct)
    {
        // Unflagged scans of every code with an open attachment in the subtree.
        await using var cmd = new NpgsqlCommand(@"
with recursive subtree(id) as (
  select id from place where id = $1
  union all
  select p.id from place p join subtree s on p.parent_id = s.id
)
select coalesce(count(*), 0)::int from qr_scan s
join qr_attachment a on a.id = s.attachment_id
where a.place_id in (select id from subtree)
  and s.is_bot = false and s.is_repeat = false;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = dto.Id });
        var r = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        dto.Scans = new PlaceScansDto { People = Convert.ToInt32(r ?? 0) };
    }

    private static async Task LoadAuditStampAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, PlaceDto dto, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select action, actor, at from audit_log
where entity = 'place' and entity_id = $1
order by id desc limit 1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = dto.Id.ToString(CultureInfo.InvariantCulture) });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return;
        dto.Audit = new AuditStampDto
        {
            Action = reader.GetString(0),
            By = reader.GetString(1),
            At = reader.GetFieldValue<DateTimeOffset>(2),
        };
    }
}
