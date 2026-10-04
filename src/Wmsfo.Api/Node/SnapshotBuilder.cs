using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Config;
using Wmsfo.Api.Content;
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
                "snapshot PUT failed; marker={Marker} key={Key}", LogMarkers.SnapshotWriteFailed, key);
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
        long? currentRouteId = null;
        int lingerMsPerDollar = 40;
        int lingerMinMs = 2000;
        int flightHistoryMaxPoints = 2000;

        // 1. settings (linger constants + flight history cap). Read up-front so
        // the sponsor and flight-history reads compose the final numbers in a
        // single pass.
        await using (var cmd = new NpgsqlCommand(@"
select key, value from app_setting
where key in ('sponsor_linger_ms_per_dollar', 'sponsor_linger_min_ms', 'flight_history_max_points');", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var key = reader.GetString(0);
                var value = reader.GetString(1);
                using var doc = JsonDocument.Parse(value);
                if (doc.RootElement.ValueKind == JsonValueKind.Number && doc.RootElement.TryGetInt32(out var parsed))
                {
                    switch (key)
                    {
                        case "sponsor_linger_ms_per_dollar": lingerMsPerDollar = parsed; break;
                        case "sponsor_linger_min_ms":        lingerMinMs = parsed; break;
                        case "flight_history_max_points":    flightHistoryMaxPoints = parsed; break;
                    }
                }
            }
        }

        // 2. current event (nullable).
        await using (var cmd = new NpgsqlCommand(@"
select e.id, e.year, e.name, e.status_id, e.scheduled_at, e.went_live_at, e.ended_at,
       e.funds_percent, e.route_image_media_id, e.route_id, e.route_map_config
from event e
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
                    RouteImageMediaId = reader.IsDBNull(8) ? null : reader.GetGuid(8).ToString(),
                    FlightHistory = null,
                    RouteMap = null,
                    RouteMapConfig = reader.IsDBNull(10) ? null : RouteMapConfigRules.FromStored(reader.GetString(10)),
                    LatestMessage = null,
                };
                currentYear = currentEvent.Year;
                currentRouteId = reader.IsDBNull(9) ? null : reader.GetInt64(9);
            }
        }

        // 3. latest message of the current event.
        if (currentEvent is not null)
        {
            await using var cmd = new NpgsqlCommand(@"
select id, body, created_at
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
                    CreatedAt = reader.GetFieldValue<DateTimeOffset>(2),
                };
            }
        }

        // 4. flight history and route map: when the current event links a
        // route (route_id), read the route row's `name` and `s3_key`, then read
        // the route object from the object store, thin its points, and build
        // the route map from them. The linked route is small
        // (at most 50,000 points, cap of 5 MB - contracts 1.4), so the read
        // stays cheap enough to run inside the admin transaction; api.md 10.2
        // documents this choice as the default.
        if (currentEvent is not null && currentRouteId is long linkedRouteId)
        {
            string? routeName = null;
            string? routeS3Key = null;
            await using (var cmd = new NpgsqlCommand(
                "select name, s3_key from route where id = $1;", conn, tx))
            {
                cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = linkedRouteId });
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    routeName = reader.GetString(0);
                    routeS3Key = reader.GetString(1);
                }
            }
            if (routeS3Key is not null)
            {
                var route = await LoadRouteObjectAsync(linkedRouteId, routeS3Key, ct).ConfigureAwait(false);
                if (route is not null)
                {
                    currentEvent.FlightHistory = BuildFlightHistory(
                        linkedRouteId, routeName ?? "", route, flightHistoryMaxPoints);
                    // The route map (contracts 1.3) is built from the same points.
                    var mapSettings = await RouteMapSettings.ReadAsync(conn, tx, ct).ConfigureAwait(false);
                    currentEvent.RouteMap = BuildRouteMap(linkedRouteId, route, mapSettings);
                }
            }
        }

        snap.Event = currentEvent;

        // 5. sponsors of the current event's year. Order (contracts 1.3):
        // rows with `pinned_position` first (asc), then the rest by
        // `amount_donated` desc (nulls last), `name` asc, `id` asc.
        // `lingerMs` uses `linger_ms_override` when set, otherwise the formula
        // `max(sponsor_linger_min_ms, round(amount * sponsor_linger_ms_per_dollar))`
        // with the floor when `amount_donated` is null.
        var sponsors = new List<SnapshotSponsor>();
        if (currentYear is not null)
        {
            await using var cmd = new NpgsqlCommand(@"
select s.id, s.name, s.website_url, s.fb_url, s.ig_url, s.logo_media_id,
       y.amount_donated, y.pinned_position, y.linger_ms_override,
       agg.latest_year, agg.years_as_sponsor
from sponsor s
join sponsor_year y on y.sponsor_id = s.id and y.event_year = $1
join lateral (
  select max(event_year) as latest_year, count(distinct event_year) as years_as_sponsor
  from sponsor_year where sponsor_id = s.id
) agg on true
where y.active and not y.anonymous and y.can_advertise
order by
  case when y.pinned_position is null then 1 else 0 end,
  y.pinned_position asc,
  y.amount_donated desc nulls last,
  s.name asc,
  s.id asc;", conn, tx);
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
                    LatestYear = reader.GetInt32(9),
                    YearsAsSponsor = (int)reader.GetInt64(10),
                };
                var amount = reader.IsDBNull(6) ? (decimal?)null : reader.GetDecimal(6);
                var lingerOverride = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8);
                s.LingerMs = ComputeLingerMs(amount, lingerOverride, lingerMsPerDollar, lingerMinMs);
                sponsors.Add(s);
            }
        }

        // 6. cookie types.
        var cookieTypes = new List<SnapshotCookieType>();
        await using (var cmd = new NpgsqlCommand(@"
select id, name, icon, sort from cookie_type where active order by sort, id;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                IconValue? icon = null;
                if (!reader.IsDBNull(2))
                {
                    using var doc = JsonDocument.Parse(reader.GetString(2));
                    icon = IconValue.FromStored(doc.RootElement);
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

        snap.Sponsors = sponsors;
        snap.CookieTypes = cookieTypes;

        // 6a. qrCodes: every active printed code already resolved (contracts 4.5a).
        // A code resolves its own opens setting, else the nearest ancestor of its
        // open attachment's place, else null (the home page, meaning absent from
        // the map). Each active code with an open attachment triggers one
        // recursive walk of the place tree (up to 32 levels) to resolve the
        // ancestor's opens setting.
        var qrCodes = new SortedDictionary<string, SnapshotQrCode>(StringComparer.Ordinal);
        var attachedCodes = new List<(string Tag, long? CodePage, string? CodeUrl, long? StartPlace)>();
        await using (var cmd = new NpgsqlCommand(@"
select c.tag, c.opens_page_id, c.forward_url, a.place_id
from qr_code c
left join qr_attachment a on a.qr_code_id = c.id and a.to_at is null
where c.active;", conn, tx))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var tag = reader.GetString(0);
                long? codePage = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                string? codeUrl = reader.IsDBNull(2) ? null : reader.GetString(2);
                long? startPlace = reader.IsDBNull(3) ? null : reader.GetInt64(3);
                attachedCodes.Add((tag, codePage, codeUrl, startPlace));
            }
        }
        // Build a page-id -> slug map for role pages ('/') and non-role pages.
        var pageSlug = new Dictionary<long, string>();
        var pageRole = new Dictionary<long, string>();
        var hiddenPages = new HashSet<long>();
        await using (var cmd = new NpgsqlCommand(
            "select id, slug, role, is_hidden from page;", conn, tx))
        {
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var pid = reader.GetInt64(0);
                pageSlug[pid] = reader.GetString(1);
                pageRole[pid] = reader.GetString(2);
                if (reader.GetBoolean(3)) hiddenPages.Add(pid);
            }
        }
        foreach (var code in attachedCodes)
        {
            long? resolvedPage = code.CodePage;
            string? resolvedUrl = code.CodeUrl;
            if (resolvedPage is null && resolvedUrl is null && code.StartPlace is long startId)
            {
                var (p, u) = await ResolvePlaceOpensAsync(conn, tx, startId, ct).ConfigureAwait(false);
                resolvedPage = p;
                resolvedUrl = u;
            }
            string? slug = null;
            string? url = null;
            if (resolvedPage is long pageId && pageSlug.TryGetValue(pageId, out var s) && !hiddenPages.Contains(pageId))
            {
                var role = pageRole.TryGetValue(pageId, out var rl) ? rl : "none";
                slug = role == "none" ? s : "/";
            }
            else if (resolvedUrl is not null)
            {
                url = resolvedUrl;
            }
            // Codes that resolve to the home page (no page and no URL) are
            // listed with pageSlug "/" (contracts 1.3): the site's /q/:tag
            // route reads the snapshot for every active code, home-resolving
            // included.
            if (slug is null && url is null) slug = "/";
            qrCodes[code.Tag] = new SnapshotQrCode { PageSlug = slug, ForwardUrl = url };
        }
        snap.QrCodes = qrCodes;

        // 7. the published content document (newest content_version row) verbatim.
        Guid[] contentMediaIds = Array.Empty<Guid>();
        ContentDocument content = new ContentDocument
        {
            SchemaVersion = 1,
            Settings = new SiteSettings { SiteName = "", HomeNavLabel = "" },
            Pages = new List<ContentPage>(),
        };
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
            }
        }
        snap.Content = content;

        // 8. media map: content media ids + sponsor logos + cookie type media
        // icons + the current event's route poster (contracts 1.3).
        var mediaIds = new HashSet<Guid>();
        foreach (var id in contentMediaIds) mediaIds.Add(id);
        foreach (var id in await CollectSnapshotLevelMediaIdsAsync(conn, tx, ct).ConfigureAwait(false))
            mediaIds.Add(id);

        var media = new SortedDictionary<string, MediaEntry>(StringComparer.Ordinal);
        if (mediaIds.Count > 0)
        {
            await using var cmd = new NpgsqlCommand(MediaMapRows.Select + @"
where m.id = any($1) and m.state = 'ready'
order by m.id;", conn, tx);
            var idsArray = mediaIds.ToArray();
            cmd.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
                Value = idsArray,
            });
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var cdn = _options.CdnBaseUrl.TrimEnd('/');
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var (id, entry) = MediaMapRows.Read(reader, cdn);
                media[id.ToString()] = entry;
            }
        }
        snap.Media = media;

        // 9. icon library map - from the compiled library (not the database).
        var icons = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in _icons.Map) icons[kv.Key] = kv.Value;
        snap.Icons = icons;

        return snap;
    }

    // The media ids the snapshot carries beyond the content document: logos of
    // the sponsors the snapshot lists (the current event's year, active, not
    // anonymous, can advertise), media icons of active cookie types, the
    // current event's route poster, and the media icons of the current event's
    // route map landmarks. The snapshot media map, the preview
    // document, and the draft response all add this set to the document's
    // referenced media.
    public static async Task<Guid[]> CollectSnapshotLevelMediaIdsAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, CancellationToken ct)
    {
        var ids = new HashSet<Guid>();
        await using (var cmd = new NpgsqlCommand(@"
select s.logo_media_id
from sponsor s
join sponsor_year y on y.sponsor_id = s.id
join event e on e.is_current and y.event_year = e.year
where y.active and not y.anonymous and y.can_advertise and s.logo_media_id is not null
union
select route_image_media_id from event
where is_current and route_image_media_id is not null;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false)) ids.Add(reader.GetGuid(0));
        }
        await using (var cmd = new NpgsqlCommand(
            "select icon from cookie_type where active and icon is not null;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                using var doc = JsonDocument.Parse(reader.GetString(0));
                var icon = IconValue.FromStored(doc.RootElement);
                if (icon is { Source: "media" } && Guid.TryParse(icon.Id, out var mediaId)) ids.Add(mediaId);
            }
        }
        await using (var cmd = new NpgsqlCommand(
            "select route_map_config from event where is_current and route_map_config is not null;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var config = RouteMapConfigRules.FromStored(reader.GetString(0));
                if (config is not null) ids.UnionWith(RouteMapConfigRules.MediaIds(config));
            }
        }
        return ids.ToArray();
    }

    // contracts 4.5a: walk the place chain from placeId up to at most 32 levels,
    // returning the first ancestor's opens_page_id or forward_url that is set;
    // (null, null) when the chain is exhausted with no setter (the home page).
    private static async Task<(long? PageId, string? Url)> ResolvePlaceOpensAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, long placeId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(@"
with recursive chain(id, parent_id, opens_page_id, forward_url, depth) as (
  select id, parent_id, opens_page_id, forward_url, 0
  from place where id = $1
  union all
  select p.id, p.parent_id, p.opens_page_id, p.forward_url, c.depth + 1
  from place p
  join chain c on p.id = c.parent_id
  where c.depth < 32
)
select opens_page_id, forward_url
from chain
where opens_page_id is not null or forward_url is not null
order by depth
limit 1;", conn, tx);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = placeId });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return (null, null);
        var pageId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
        var url = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (pageId, url);
    }

    private static int ComputeLingerMs(decimal? amount, int? lingerOverride, int perDollar, int minMs)
    {
        if (lingerOverride is int over) return over;
        if (amount is null) return minMs;
        var computed = (int)Math.Round((double)amount.Value * perDollar, 0, MidpointRounding.AwayFromZero);
        return Math.Max(minMs, computed);
    }

    // Fetches the linked route's stored object from the object store and
    // decodes it as a RouteObject. A missing or unreadable object logs at
    // Warning and returns null, which leaves flightHistory and routeMap null so
    // the snapshot still commits.
    private async Task<RouteObject?> LoadRouteObjectAsync(long routeId, string s3Key, CancellationToken ct)
    {
        try
        {
            var content = await _store.GetObjectAsync(s3Key, ct).ConfigureAwait(false);
            if (content is null)
            {
                _logger.LogWarning(
                    "flight history route object missing; routeId={RouteId} key={Key}", routeId, s3Key);
                return null;
            }
            return JsonSerializer.Deserialize<RouteObject>(content.Bytes, ContentReadOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "flight history load failed; routeId={RouteId} key={Key}", routeId, s3Key);
            return null;
        }
    }

    // The route's points thinned to at most `maxPoints` by keeping every
    // `ceil(n / max)`-th point starting from the first and always including
    // the last (contracts 1.3, api.md 10.2).
    private static SnapshotFlightHistory BuildFlightHistory(
        long routeId, string routeName, RouteObject route, int maxPoints) => new()
    {
        RouteId = routeId,
        Name = string.IsNullOrEmpty(routeName) ? route.Name : routeName,
        Points = ThinPoints(route.Points, maxPoints),
    };

    // The route map of contracts 1.3; a build failure logs at Warning and
    // leaves routeMap null so the snapshot still commits.
    private RouteMap? BuildRouteMap(long routeId, RouteObject route, RouteMapSettings settings)
    {
        try
        {
            return RouteMapBuilder.Build(route.Points, settings);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "route map build failed; routeId={RouteId}", routeId);
            return null;
        }
    }

    // contracts 1.3: keep every ceil(n / max)-th point starting from the first
    // and always include the last. Exposed for tests; the docs example is
    // 7 at max 3 -> 1, 4, 7.
    internal static List<SnapshotFlightPoint> ThinPoints(IList<RoutePoint> source, int maxPoints)
    {
        var n = source.Count;
        var result = new List<SnapshotFlightPoint>();
        if (n == 0 || maxPoints <= 0) return result;
        if (n <= maxPoints)
        {
            foreach (var p in source) result.Add(Convert(p));
            return result;
        }
        var step = (n + maxPoints - 1) / maxPoints; // ceil(n / max)
        int lastKept = -1;
        for (var i = 0; i < n; i += step)
        {
            result.Add(Convert(source[i]));
            lastKept = i;
        }
        if (lastKept != n - 1) result.Add(Convert(source[n - 1]));
        return result;

        static SnapshotFlightPoint Convert(RoutePoint p) => new()
        {
            Lat = p.Lat,
            Lng = p.Lng,
            RecordedAt = p.RecordedAt,
        };
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
