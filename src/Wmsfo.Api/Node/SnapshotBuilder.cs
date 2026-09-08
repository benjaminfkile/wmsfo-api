using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Http;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Node;

// api.md 10.2 / sql.md 7 + 8.5: runs inside the admin transaction on the same
// connection so the reads that build the object see the write's changes.
// Sequence per contracts 7.3:
//   1. `select * from snapshot where id = 1 for update` (the caller does this).
//   2. Apply the write (the caller does this).
//   3. Run the snapshot reads of sql.md 7.
//   4. bytes = CanonicalJson.Serialize(snapshot); key = "snapshots/" + sha256 + ".json"
//   5. objectStore.PutAsync(key, bytes, immutable) with a 3 s timeout, one attempt.
//   6. update snapshot set version = version + 1, url = <cdn>+'/'+key, s3_key = key, built_at = now()
//   7. Commit (the caller does this).
public sealed class SnapshotBuilder
{
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public const string JsonContentType = "application/json; charset=utf-8";
    public static readonly TimeSpan PutTimeout = TimeSpan.FromSeconds(3);
    public const string SnapshotWriteFailedCode = "snapshot_write_failed";

    private readonly IObjectStore _store;
    private readonly IconLibrary _icons;
    private readonly WmsfoOptions _options;
    private readonly ILogger<SnapshotBuilder> _logger;

    public SnapshotBuilder(
        IObjectStore store,
        IconLibrary icons,
        WmsfoOptions options,
        ILogger<SnapshotBuilder> logger)
    {
        _store = store;
        _icons = icons;
        _options = options;
        _logger = logger;
    }

    // Called inside an admin transaction. On PUT failure throws ApiException(502)
    // so the caller's transaction rolls back per api.md 10.2.
    public async Task<BuiltSnapshot> BuildAndPutAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken ct)
    {
        var snapshot = await ReadAsync(connection, transaction, ct).ConfigureAwait(false);
        var bytes = CanonicalJson.SerializeToUtf8Bytes(snapshot);
        var sha = CanonicalJson.Sha256Hex(bytes);
        var key = $"snapshots/{sha}.json";
        var url = _options.CdnBaseUrl.TrimEnd('/') + "/" + key;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PutTimeout);
            await _store.PutObjectAsync(key, bytes, JsonContentType, ImmutableCacheControl, tag: null, cts.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "snapshot PUT failed; marker=wmsfo_snapshot_write_failed key={Key}", key);
            throw new ApiException(502, SnapshotWriteFailedCode, "snapshot upload failed");
        }
        return new BuiltSnapshot(snapshot, bytes, sha, key, url);
    }

    // Executes the full transactional flow of api.md 10.2 including the snapshot
    // row update. Called by POST /admin/snapshot/rebuild (which passes a no-op
    // write) and directly by other handlers that want the whole frame.
    public async Task<SnapshotRowInfo> RebuildAsync(CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(_options.DbConnection);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return await RebuildAsync(conn, ct).ConfigureAwait(false);
    }

    public async Task<SnapshotRowInfo> RebuildAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (var lockRow = new NpgsqlCommand("select id from snapshot where id = 1 for update;", conn, tx))
        {
            await lockRow.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        var built = await BuildAndPutAsync(conn, tx, ct).ConfigureAwait(false);
        var version = await ApplySnapshotRowUpdateAsync(conn, tx, built, ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new SnapshotRowInfo(version, built.Url, built.Key, DateTimeOffset.UtcNow);
    }

    // The step 6 update; the caller uses it after BuildAndPutAsync when the
    // transaction is not managed by RebuildAsync (e.g., a status change).
    public static async Task<long> ApplySnapshotRowUpdateAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        BuiltSnapshot built,
        CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
update snapshot
set version = version + 1,
    url = $1,
    s3_key = $2,
    built_at = now()
where id = 1
returning version;", connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = built.Url });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = built.Key });
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(result!);
    }

    // sql.md 7: the reads that populate the object.
    private async Task<Snapshot> ReadAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var snap = new Snapshot { SchemaVersion = 1 };
        SnapshotEvent? currentEvent = null;
        int? currentYear = null;
        int lingerMsPerDollar = 40;
        int lingerMinMs = 2000;

        // 1. current event (nullable) and its (optional) route URL.
        await using (var cmd = new NpgsqlCommand(@"
select e.id, e.year, e.name, e.status_id, e.scheduled_at, e.went_live_at, e.ended_at, e.funds_percent, r.url
from event e
left join route r on r.id = e.route_id
where e.is_current;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                currentEvent = new SnapshotEvent
                {
                    Id = reader.GetInt64(0),
                    Year = reader.GetInt32(1),
                    Name = reader.GetString(2),
                    StatusId = reader.GetInt16(3),
                    ScheduledAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
                    WentLiveAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5),
                    EndedAt = reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                    FundsPercent = reader.GetInt32(7),
                    RouteUrl = reader.IsDBNull(8) ? null : reader.GetString(8),
                    LatestMessage = null,
                };
                currentYear = currentEvent.Year;
            }
        }

        // 2. latest message of the current event.
        if (currentEvent is not null)
        {
            await using var cmd = new NpgsqlCommand(@"
select id, body, event_time, created_at
from event_message
where event_id = $1
order by created_at desc, id desc
limit 1;", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = currentEvent.Id });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                currentEvent.LatestMessage = new SnapshotLatestMessage
                {
                    Id = reader.GetInt64(0),
                    Body = reader.GetString(1),
                    EventTime = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2),
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                };
            }
        }

        snap.Event = currentEvent;

        // 3. sponsors of the current event's year.
        var sponsorLogos = new List<Guid>();
        var sponsors = new List<SnapshotSponsor>();
        if (currentYear is not null)
        {
            await using var cmd = new NpgsqlCommand(@"
select s.id, s.name, s.website_url, s.fb_url, s.ig_url, s.logo_media_id,
       y.amount_donated,
       agg.latest_year, agg.years_as_sponsor
from sponsor s
join sponsor_year y on y.sponsor_id = s.id and y.event_year = $1
join lateral (
  select max(event_year) as latest_year, count(distinct event_year) as years_as_sponsor
  from sponsor_year where sponsor_id = s.id
) agg on true
where y.active and not y.anonymous and y.can_advertise
order by y.amount_donated desc nulls last, s.name asc, s.id asc;", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = currentYear });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var s = new SnapshotSponsor
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    WebsiteUrl = reader.IsDBNull(2) ? null : reader.GetString(2),
                    FbUrl = reader.IsDBNull(3) ? null : reader.GetString(3),
                    IgUrl = reader.IsDBNull(4) ? null : reader.GetString(4),
                    LogoMediaId = reader.IsDBNull(5) ? null : reader.GetGuid(5).ToString(),
                    LatestYear = reader.GetInt32(7),
                    YearsAsSponsor = (int)reader.GetInt64(8),
                };
                var amount = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6);
                s.LingerMs = ComputeLingerMs(amount, lingerMsPerDollar, lingerMinMs);
                sponsors.Add(s);
                if (!reader.IsDBNull(5)) sponsorLogos.Add(reader.GetGuid(5));
            }
        }

        // 4. cookie types.
        var cookieTypes = new List<SnapshotCookieType>();
        var cookieTypeMediaIds = new List<Guid>();
        await using (var cmd = new NpgsqlCommand(@"
select id, name, icon, sort from cookie_type where active order by sort, id;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                IconValue? icon = null;
                if (!reader.IsDBNull(2))
                {
                    var json = reader.GetString(2);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        var source = doc.RootElement.TryGetProperty("source", out var sr) ? sr.GetString() ?? "" : "";
                        var id = doc.RootElement.TryGetProperty("id", out var ir) ? ir.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(source) && !string.IsNullOrEmpty(id))
                        {
                            icon = new IconValue { Source = source, Id = id };
                            if (source == "media" && Guid.TryParse(id, out var mediaId))
                                cookieTypeMediaIds.Add(mediaId);
                        }
                    }
                }
                cookieTypes.Add(new SnapshotCookieType
                {
                    Id = reader.GetInt64(0),
                    Name = reader.GetString(1),
                    Icon = icon,
                    Sort = reader.GetInt32(3),
                });
            }
        }

        // 5. settings — needed for lingerMs; re-read the two keys since we already
        // filled sponsors with a temporary computation. Do this before sponsors in
        // real use; the code above recomputes lingerMs after reading settings.
        await using (var cmd = new NpgsqlCommand(@"
select key, value from app_setting where key in ('sponsor_linger_ms_per_dollar', 'sponsor_linger_min_ms');", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind == JsonValueKind.Number && doc.RootElement.TryGetInt32(out var parsed))
                {
                    if (key == "sponsor_linger_ms_per_dollar") lingerMsPerDollar = parsed;
                    else if (key == "sponsor_linger_min_ms") lingerMinMs = parsed;
                }
            }
        }
        // Recompute lingerMs with the actual settings (they may have moved).
        if (sponsors.Count > 0 && currentYear is not null)
        {
            var i = 0;
            await using var cmd = new NpgsqlCommand(@"
select s.id, y.amount_donated
from sponsor s
join sponsor_year y on y.sponsor_id = s.id and y.event_year = $1
where y.active and not y.anonymous and y.can_advertise
order by y.amount_donated desc nulls last, s.name asc, s.id asc;", conn, tx);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = currentYear });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false) && i < sponsors.Count)
            {
                var amount = reader.IsDBNull(1) ? (decimal?)null : reader.GetDecimal(1);
                sponsors[i].LingerMs = ComputeLingerMs(amount, lingerMsPerDollar, lingerMinMs);
                i++;
            }
        }

        snap.Sponsors = sponsors;
        snap.CookieTypes = cookieTypes;

        // 6. the published content document (newest content_version row) verbatim.
        Guid[] contentMediaIds = Array.Empty<Guid>();
        ContentDocument content = new ContentDocument
        {
            SchemaVersion = 1,
            Settings = new SiteSettings { SiteName = "", HomeNavLabel = "" },
            Pages = new List<ContentPage>(),
        };
        bool contentFromVersion = false;
        await using (var cmd = new NpgsqlCommand(@"
select id, document, media_ids from content_version order by id desc limit 1;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var docJson = reader.GetString(1);
                var deserialized = JsonSerializer.Deserialize<ContentDocument>(docJson, ContentReadOptions);
                if (deserialized is not null) content = deserialized;
                if (!reader.IsDBNull(2))
                {
                    var arr = (Guid[])reader.GetValue(2);
                    contentMediaIds = arr;
                }
                contentFromVersion = true;
            }
        }
        snap.Content = content;

        // 7. media map: content media ids + sponsor logos + cookie type media icons.
        var mediaIds = new HashSet<Guid>();
        foreach (var id in contentMediaIds) mediaIds.Add(id);
        foreach (var id in sponsorLogos) mediaIds.Add(id);
        foreach (var id in cookieTypeMediaIds) mediaIds.Add(id);

        var media = new SortedDictionary<string, MediaEntry>(StringComparer.Ordinal);
        if (mediaIds.Count > 0)
        {
            await using var cmd = new NpgsqlCommand(@"
select id, s3_key, kind, width, height, alt, variants
from media_asset
where id = any($1) and state = 'ready'
order by id;", conn, tx);
            var idsArray = mediaIds.ToArray();
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                Value = idsArray,
            });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                var key = reader.GetString(1);
                var kind = reader.GetString(2);
                int? width = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                int? height = reader.IsDBNull(4) ? null : reader.GetInt32(4);
                var alt = reader.GetString(5);
                var variantsJson = reader.GetString(6);
                var variants = new SortedDictionary<string, string>(StringComparer.Ordinal);
                using (var doc = JsonDocument.Parse(variantsJson))
                {
                    foreach (var e in doc.RootElement.EnumerateObject())
                    {
                        variants[e.Name] = _options.CdnBaseUrl.TrimEnd('/') + "/" + e.Value.GetString();
                    }
                }
                media[id.ToString()] = new MediaEntry
                {
                    Url = _options.CdnBaseUrl.TrimEnd('/') + "/" + key,
                    Kind = kind,
                    Width = width,
                    Height = height,
                    Alt = alt,
                    Variants = variants,
                };
            }
        }
        snap.Media = media;

        // 8. icon library map — from the compiled library (not the database).
        var icons = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _icons.Map) icons[kv.Key] = kv.Value;
        snap.Icons = icons;

        _ = contentFromVersion;   // Silences the unused-warn — kept for future guard.

        return snap;
    }

    private static int ComputeLingerMs(decimal? amount, int perDollar, int minMs)
    {
        if (amount is null) return minMs;
        var computed = (int)Math.Round((double)amount.Value * perDollar, 0, MidpointRounding.AwayFromZero);
        return Math.Max(minMs, computed);
    }

    private static readonly JsonSerializerOptions ContentReadOptions = BuildContentReadOptions();
    private static JsonSerializerOptions BuildContentReadOptions()
    {
        // Deserialize the persisted document with the same options that wrote it;
        // unmapped-member handling is tolerant so a future field does not break
        // reads on older nodes.
        var o = new JsonSerializerOptions(CanonicalJson.Options)
        {
            UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Skip,
        };
        return o;
    }
}

// Values the caller commits: the object, its bytes, its content hash, the key
// under snapshots/, and the CDN URL.
public sealed record BuiltSnapshot(Snapshot Object, byte[] Bytes, string Sha256, string Key, string Url);

// The snapshot row after commit. Returned by RebuildAsync and by other admin
// handlers that finish the frame.
public sealed record SnapshotRowInfo(long Version, string Url, string Key, DateTimeOffset BuiltAt);
