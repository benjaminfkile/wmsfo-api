using System.Globalization;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Endpoints;

// Read-side helpers for the QR endpoints. Every QrCodeDto/QrCodeDetailDto goes
// through here so the wire shape stays consistent (contracts 4.5a).
public static class QrRead
{
    public static async Task<QrCodeDto?> ByIdAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select id, tag, batch_no, printed_at, active, note, opens_page_id, forward_url,
       created_by, created_at, updated_at
from qr_code where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return null;
        var dto = ReadRow(reader);
        await reader.DisposeAsync().ConfigureAwait(false);
        await LoadAttachmentAsync(conn, tx, dto, ct).ConfigureAwait(false);
        await LoadScansSummaryAsync(conn, tx, dto, ct).ConfigureAwait(false);
        await LoadResolvedOpensAsync(conn, tx, dto, ct).ConfigureAwait(false);
        await LoadAuditStampAsync(conn, tx, dto, ct).ConfigureAwait(false);
        return dto;
    }

    public static async Task<QrCodeDetailDto?> DetailAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long id, CancellationToken ct)
    {
        var basic = await ByIdAsync(conn, tx, id, ct).ConfigureAwait(false);
        if (basic is null) return null;
        var detail = new QrCodeDetailDto
        {
            Id = basic.Id,
            Tag = basic.Tag,
            BatchNo = basic.BatchNo,
            PrintedAt = basic.PrintedAt,
            Active = basic.Active,
            Note = basic.Note,
            OpensPageId = basic.OpensPageId,
            ForwardUrl = basic.ForwardUrl,
            Opens = basic.Opens,
            OpensSource = basic.OpensSource,
            Attachment = basic.Attachment,
            Scans = basic.Scans,
            CreatedBy = basic.CreatedBy,
            CreatedAt = basic.CreatedAt,
            UpdatedAt = basic.UpdatedAt,
            Audit = basic.Audit,
        };
        // history: every attachment for this code, newest first, with the
        // people count of each stay and the earlyScans it inherited (folded).
        var historyRows = new List<(long AttId, long? PlaceId, DateTimeOffset FromAt, DateTimeOffset? ToAt, int People, int Early)>();
        await using (var hist = new NpgsqlCommand(@"
select a.id, a.place_id, a.from_at, a.to_at,
  (select count(*)::int from qr_scan s
    where s.attachment_id = a.id and s.is_bot = false and s.is_repeat = false) as people,
  (select count(*)::int from qr_scan s
    where s.attachment_id = a.id and s.at < a.from_at
      and s.is_bot = false and s.is_repeat = false) as early_scans
from qr_attachment a where a.qr_code_id = $1 order by a.from_at desc, a.id desc;", conn, tx))
        {
            hist.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await hist.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                historyRows.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.GetFieldValue<DateTimeOffset>(2),
                    reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
                    reader.GetInt32(4),
                    reader.GetInt32(5)));
            }
        }
        foreach (var row in historyRows)
        {
            var path = row.PlaceId is long stayPlace
                ? await PlacePathAsync(conn, tx, stayPlace, ct).ConfigureAwait(false)
                : new List<string>();
            detail.History.Add(new QrHistoryEntryDto
            {
                AttachmentId = row.AttId,
                PlaceId = row.PlaceId,
                PlacePath = path,
                FromAt = row.FromAt,
                ToAt = row.ToAt,
                People = row.People,
                EarlyScans = row.Early,
            });
        }
        // daily: last 14 days, one row per day (people = unflagged scans).
        await using (var daily = new NpgsqlCommand(@"
select to_char(day::date, 'YYYY-MM-DD') as d,
       coalesce((
         select count(*)::int from qr_scan s
         where s.qr_code_id = $1 and s.is_bot = false and s.is_repeat = false
           and s.at::date = day::date
       ), 0) as n
from generate_series((now() - interval '13 days')::date, now()::date, interval '1 day') as day
order by d asc;", conn, tx))
        {
            daily.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = id });
            await using var reader = await daily.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                detail.Daily.Add(new QrDailyDto
                {
                    Day = reader.GetString(0),
                    People = reader.GetInt32(1),
                });
            }
        }
        return detail;
    }

    public static async Task<List<QrCodeDto>> ListAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var ids = new List<long>();
        await using (var cmd = new NpgsqlCommand("select id from qr_code order by tag asc;", conn, tx))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetInt64(0));
        }
        var items = new List<QrCodeDto>(ids.Count);
        foreach (var id in ids)
        {
            var dto = await ByIdAsync(conn, tx, id, ct).ConfigureAwait(false);
            if (dto is not null) items.Add(dto);
        }
        return items;
    }

    private static QrCodeDto ReadRow(NpgsqlDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Tag = reader.GetString(1),
        BatchNo = reader.GetInt32(2),
        PrintedAt = reader.GetFieldValue<DateTimeOffset>(3),
        Active = reader.GetBoolean(4),
        Note = reader.GetString(5),
        OpensPageId = reader.IsDBNull(6) ? null : reader.GetInt64(6),
        ForwardUrl = reader.IsDBNull(7) ? null : reader.GetString(7),
        CreatedBy = reader.GetString(8),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(9),
        UpdatedAt = reader.GetFieldValue<DateTimeOffset>(10),
    };

    private static async Task LoadAttachmentAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, QrCodeDto dto, CancellationToken ct)
    {
        long attId;
        long placeId;
        DateTimeOffset since;
        await using (var cmd = new NpgsqlCommand(@"
select id, place_id, from_at from qr_attachment where qr_code_id = $1 and to_at is null;", conn, tx))
        {
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = dto.Id });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return;
            // A place delete closes its open attachments, so an open row
            // always has a place; a null here is a row mid-delete.
            if (reader.IsDBNull(1)) return;
            attId = reader.GetInt64(0);
            placeId = reader.GetInt64(1);
            since = reader.GetFieldValue<DateTimeOffset>(2);
        }
        var path = await PlacePathAsync(conn, tx, placeId, ct).ConfigureAwait(false);
        dto.Attachment = new QrAttachmentSummaryDto
        {
            Id = attId,
            PlaceId = placeId,
            PlacePath = path,
            Since = since,
        };
    }

    private static async Task LoadScansSummaryAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, QrCodeDto dto, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select
  count(*) filter (where is_bot = false and is_repeat = false)::int as people,
  count(*) filter (where is_bot = true or is_repeat = true)::int as flagged,
  max(at) filter (where is_bot = false and is_repeat = false) as last_at
from qr_scan where qr_code_id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = dto.Id });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return;
        dto.Scans = new QrScanSummaryDto
        {
            People = reader.GetInt32(0),
            Flagged = reader.GetInt32(1),
            LastScanAt = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
        };
    }

    // Resolves the code's opens: its own setting first, else the ancestor chain
    // of the code's open attachment's place, else home. Also fills the opens
    // source and, on a page resolution, the page slug.
    private static async Task LoadResolvedOpensAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, QrCodeDto dto, CancellationToken ct)
    {
        long? resolvedPage = dto.OpensPageId;
        string? resolvedUrl = dto.ForwardUrl;
        string source = "home";
        if (resolvedPage is not null || resolvedUrl is not null)
        {
            source = "code";
        }
        else if (dto.Attachment is not null)
        {
            var (p, u) = await PlaceRead.ResolveOpensAsync(conn, tx, dto.Attachment.PlaceId, ct).ConfigureAwait(false);
            if (p is not null || u is not null) source = "place";
            resolvedPage = p; resolvedUrl = u;
        }
        var opens = new OpensDto();
        if (resolvedPage is long pageId)
        {
            var (slug, role, hidden) = await ReadPageAsync(conn, tx, pageId, ct).ConfigureAwait(false);
            if (slug is not null && !hidden)
            {
                opens.Kind = "page";
                opens.PageId = pageId;
                opens.Slug = role == "none" ? slug : "/";
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

    private static async Task<(string? Slug, string Role, bool Hidden)> ReadPageAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long pageId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("select slug, role, is_hidden from page where id = $1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = pageId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return (null, "none", true);
        return (reader.GetString(0), reader.GetString(1), reader.GetBoolean(2));
    }

    private static async Task LoadAuditStampAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, QrCodeDto dto, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
select action, actor, at from audit_log
where entity = 'qr_code' and entity_id = $1
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

    // The place's path from root to itself (place names in order), used by the
    // wire QrAttachmentSummaryDto and by history entries.
    public static async Task<List<string>> PlacePathAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, long placeId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
with recursive chain(id, parent_id, name, depth) as (
  select id, parent_id, name, 0 from place where id = $1
  union all
  select p.id, p.parent_id, p.name, c.depth + 1
  from place p join chain c on p.id = c.parent_id where c.depth < 32
)
select name from chain order by depth desc;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = placeId });
        var path = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) path.Add(reader.GetString(0));
        return path;
    }
}
