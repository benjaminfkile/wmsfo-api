using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Icons;

namespace Wmsfo.Api.Content;

// api.md 11a.2 semantic checks for publish-level validation. Given the working set
// materialised as a WorkingSet (pages + sections + items + site settings), walks
// every primitive value that carries a reference, resolves them, and returns a
// list of ProblemRef entries suitable for POST /admin/content/publish and
// GET /admin/content/status:
//
//   - MediaRef mediaId names a media_asset row with state = 'ready'
//   - media-sourced Icon names a ready media_asset
//   - a video asset is named only by a rich_text `media` block's `media` or a
//     `media` section item's `media` (contracts 1.3a); everywhere else it is
//     "media asset `<id>` is a video and cannot be used here"
//   - library-sourced Icon id exists in IconLibrary (a settings landmark's icon included)
//   - Link.href and inline `[label](href)` matches the href rule
//   - a `/<slug>` href names an existing, non-hidden `none` page (or role page's slug)
//   - Presentation.anchor is unique across the sections of a page
//   - `map` sections sit on the page with role `live`
//   - Every Inline parses without malformed constructs
//   - settings.route.routeId, when set, names a route row
//
// The walker recognises the primitive shapes, not the kinds, so a new section
// kind picks up the checks without changes.
public sealed class ReferenceChecker
{
    private readonly IconLibrary? _iconLibrary;

    public ReferenceChecker(IconLibrary? iconLibrary)
    {
        _iconLibrary = iconLibrary;
    }

    // The full working-set snapshot the checker walks. DocumentBuilder produces
    // this (with hidden rows omitted).
    public sealed record PageInput(long Id, string Slug, string Role, IReadOnlyList<SectionInput> Sections, JsonNode? Icon = null);
    public sealed record SectionInput(long Id, string Kind, JsonNode? Data, JsonNode? Presentation, IReadOnlyList<ItemInput> Items);
    public sealed record ItemInput(long Id, JsonNode? Data);
    public sealed record WorkingSet(IReadOnlyList<PageInput> Pages, JsonNode? Settings);

    // Every page slug the working set exposes (hidden pages omitted). Passed into
    // Check so a site-path href can be resolved without reading the database twice.
    public sealed record PageSlug(string Slug, bool IsRolePage);

    public async Task<IReadOnlyList<ProblemRefDto>> CheckAsync(
        WorkingSet workingSet,
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        CancellationToken ct)
    {
        var problems = new List<ProblemRefDto>();

        // Collect visible page slugs (working set already omits hidden pages).
        var visibleSlugs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in workingSet.Pages)
        {
            visibleSlugs.Add(page.Slug);
        }

        // First pass: walk every value and collect media ids for a single lookup.
        var visitor = new Visitor(_iconLibrary, visibleSlugs);
        visitor.WalkWorkingSet(workingSet);

        // Resolve every referenced media asset in one query.
        var mediaRows = await ReadMediaRowsAsync(visitor.MediaIds, connection, transaction, ct).ConfigureAwait(false);

        // Second pass: report media-ref problems now that we know each id's state.
        foreach (var ref_ in visitor.MediaRefs)
        {
            if (!Guid.TryParse(ref_.MediaId, out var g))
            {
                problems.Add(NewProblem(ref_.Path, $"media id `{ref_.MediaId}` is not a UUID",
                    ref_.PageId, ref_.SectionId, ref_.ItemId));
                continue;
            }
            if (!mediaRows.TryGetValue(g, out var row))
            {
                problems.Add(NewProblem(ref_.Path, $"media asset `{ref_.MediaId}` does not exist",
                    ref_.PageId, ref_.SectionId, ref_.ItemId));
                continue;
            }
            if (!string.Equals(row.State, "ready", StringComparison.Ordinal))
            {
                problems.Add(NewProblem(ref_.Path,
                    $"media asset `{ref_.MediaId}` is not ready (state={row.State})",
                    ref_.PageId, ref_.SectionId, ref_.ItemId));
                continue;
            }
            if (row.Kind == "video" && !ref_.AllowsVideo)
            {
                problems.Add(NewProblem(ref_.Path, VideoMessage(ref_.MediaId),
                    ref_.PageId, ref_.SectionId, ref_.ItemId));
            }
        }

        // The site route: a `routeId` must name a route row. The row is read
        // `for share`, so a concurrent route delete either waits for this
        // transaction (and then sees the version naming the route) or has
        // already removed the row (and this check reports it).
        if (SiteRoute.RouteId((workingSet.Settings as JsonObject)?["route"]) is long routeId)
        {
            await using var cmd = new NpgsqlCommand(
                "select 1 from route where id = $1 for share;", connection, transaction);
            cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bigint, Value = routeId });
            var found = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (found is null || found is DBNull)
                problems.Add(NewProblem(SiteRouteIdPath, "route not found", null, null, null));
        }

        // Then every problem the visitor already collected (library icons, hrefs,
        // anchors, inline grammar, `map` on non-live pages).
        problems.AddRange(visitor.Problems);
        return problems;
    }

    // Where a missing site route is reported.
    public const string SiteRouteIdPath = "/settings/route/routeId";

    // The problem a video reports on a path that does not allow one.
    public static string VideoMessage(string mediaId) =>
        $"media asset `{mediaId}` is a video and cannot be used here";

    // The video rule alone for a section write (contracts 1.3a): paths are
    // `/data/...` and `/presentation/...`. Draft writes check nothing else
    // about references, so a missing or pending asset is not reported here.
    public static Task<IReadOnlyList<ProblemDto>> VideoProblemsInSectionAsync(
        string kind, JsonNode? data, JsonNode? presentation,
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var visitor = new Visitor(null, new HashSet<string>(StringComparer.Ordinal));
        visitor.WalkWrite(kind, inItem: false, ("/data", data), ("/presentation", presentation));
        return VideoProblemsAsync(visitor, connection, transaction, ct);
    }

    // The video rule alone for an item write; paths are `/data/...`.
    public static Task<IReadOnlyList<ProblemDto>> VideoProblemsInItemAsync(
        string kind, JsonNode? data,
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var visitor = new Visitor(null, new HashSet<string>(StringComparer.Ordinal));
        visitor.WalkWrite(kind, inItem: true, ("/data", data));
        return VideoProblemsAsync(visitor, connection, transaction, ct);
    }

    // The video rule alone for a site settings write; paths are relative to
    // the settings (`/logoMedia/mediaId`, `/landmarks/0/icon/id`).
    public static Task<IReadOnlyList<ProblemDto>> VideoProblemsInSettingsAsync(
        JsonNode? settings,
        NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var visitor = new Visitor(null, new HashSet<string>(StringComparer.Ordinal));
        visitor.WalkSettingsWrite(settings);
        return VideoProblemsAsync(visitor, connection, transaction, ct);
    }

    private static async Task<IReadOnlyList<ProblemDto>> VideoProblemsAsync(
        Visitor visitor, NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var problems = new List<ProblemDto>();
        if (visitor.MediaRefs.All(r => r.AllowsVideo)) return problems;
        var rows = await ReadMediaRowsAsync(visitor.MediaIds, connection, transaction, ct).ConfigureAwait(false);
        foreach (var ref_ in visitor.MediaRefs)
        {
            if (ref_.AllowsVideo || !Guid.TryParse(ref_.MediaId, out var g)) continue;
            if (rows.TryGetValue(g, out var row) && row.Kind == "video")
                problems.Add(new ProblemDto { Path = ref_.Path, Message = VideoMessage(ref_.MediaId) });
        }
        return problems;
    }

    private sealed record MediaRow(string State, string Kind);

    private static async Task<Dictionary<Guid, MediaRow>> ReadMediaRowsAsync(
        HashSet<Guid> ids, NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken ct)
    {
        var rows = new Dictionary<Guid, MediaRow>();
        if (ids.Count == 0) return rows;
        await using var cmd = new NpgsqlCommand(
            "select id, state, kind from media_asset where id = any($1);", connection, transaction);
        cmd.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid,
            Value = ids.ToArray(),
        });
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            rows[reader.GetGuid(0)] = new MediaRow(reader.GetString(1), reader.GetString(2));
        }
        return rows;
    }


    private static ProblemRefDto NewProblem(string path, string message, long? pageId, long? sectionId, long? itemId) =>
        new()
        {
            Path = path,
            Message = message,
            PageId = pageId,
            SectionId = sectionId,
            ItemId = itemId,
        };

    // Walker over primitives. Data and Presentation are treated as opaque JSON
    // trees; the walker recognises MediaRef by `{ mediaId, alt }`, Icon by
    // `{ source, id }`, Link by `{ label, href, icon, newTab }`, and every
    // string in a known Inline slot is parsed with InlineText.
    private sealed class Visitor
    {
        private readonly IconLibrary? _library;
        private readonly HashSet<string> _visibleSlugs;

        public List<ProblemRefDto> Problems { get; } = new();
        public HashSet<Guid> MediaIds { get; } = new();
        public List<MediaAssetRef> MediaRefs { get; } = new();

        // Context tracked as we descend.
        private long? _pageId;
        private long? _sectionId;
        private long? _itemId;
        private string? _sectionKind;
        private bool _inItem;
        // The `kind` of the nearest enclosing object that has one (a block's kind).
        private string? _blockKind;
        private string _basePath = "";
        private HashSet<string>? _pageAnchors;

        // AllowsVideo: the ref sits on one of the two paths a video may take.
        public sealed record MediaAssetRef(string MediaId, string Path, long? PageId, long? SectionId, long? ItemId, bool AllowsVideo = false);

        public Visitor(IconLibrary? library, HashSet<string> visibleSlugs)
        {
            _library = library;
            _visibleSlugs = visibleSlugs;
        }

        public void WalkWorkingSet(WorkingSet ws)
        {
            // Settings first.
            _pageId = null; _sectionId = null; _itemId = null;
            if (ws.Settings is not null)
            {
                _basePath = "/settings";
                _pageAnchors = null;
                WalkSettings(ws.Settings);
            }
            foreach (var page in ws.Pages)
            {
                _pageAnchors = new HashSet<string>(StringComparer.Ordinal);
                _pageId = page.Id;
                _sectionId = null;
                _itemId = null;
                // The page's own icon.
                if (page.Icon is not null)
                {
                    _basePath = "/icon";
                    WalkNode(page.Icon);
                }
                foreach (var section in page.Sections)
                {
                    _sectionId = section.Id;
                    _sectionKind = section.Kind;
                    _itemId = null;
                    _inItem = false;

                    // Map-on-live rule.
                    if (section.Kind == "map" && page.Role != "live")
                    {
                        Problems.Add(new ProblemRefDto
                        {
                            Path = "/",
                            Message = "map section is allowed only on the live page",
                            PageId = _pageId,
                            SectionId = _sectionId,
                        });
                    }

                    // Presentation.
                    if (section.Presentation is not null)
                    {
                        _basePath = "/presentation";
                        WalkNode(section.Presentation);
                        CheckAnchor(section.Presentation);
                    }
                    // Section data.
                    if (section.Data is not null)
                    {
                        _basePath = "/data";
                        WalkNode(section.Data);
                    }
                    // Items.
                    foreach (var item in section.Items)
                    {
                        _itemId = item.Id;
                        _inItem = true;
                        if (item.Data is not null)
                        {
                            _basePath = "/data";
                            WalkNode(item.Data);
                        }
                    }
                    _itemId = null;
                    _inItem = false;
                }
                _sectionId = null;
                _sectionKind = null;
            }
        }

        // One section or item write: each node walked under its base path.
        public void WalkWrite(string sectionKind, bool inItem, params (string BasePath, JsonNode? Node)[] nodes)
        {
            _sectionKind = sectionKind;
            _inItem = inItem;
            foreach (var (basePath, node) in nodes)
            {
                if (node is null) continue;
                _basePath = basePath;
                WalkNode(node);
            }
        }

        // One site settings write, paths relative to the settings.
        public void WalkSettingsWrite(JsonNode? settings)
        {
            if (settings is null) return;
            _basePath = "";
            WalkSettings(settings);
        }

        // The two paths a video may take: a rich_text `media` block's `media`
        // (the block kind is the one WalkNode recorded on the way down) and a
        // `media` section item's `media`.
        private bool AllowsVideoAt(string path) =>
            _inItem
                ? _sectionKind == "media" && path == "/data/media"
                : _sectionKind == "rich_text" && _blockKind == "media" && RichTextMediaBlockPath.IsMatch(path);

        private static readonly System.Text.RegularExpressions.Regex RichTextMediaBlockPath =
            new("^/data/blocks/[0-9]+/media$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        private void CheckAnchor(JsonNode presentation)
        {
            if (presentation is not JsonObject obj) return;
            if (!obj.TryGetPropertyValue("anchor", out var anchorNode) || anchorNode is null) return;
            if (anchorNode is JsonValue v && v.TryGetValue<string>(out var value) && !string.IsNullOrEmpty(value))
            {
                if (_pageAnchors is null) return;
                if (!_pageAnchors.Add(value))
                {
                    Problems.Add(new ProblemRefDto
                    {
                        Path = "/presentation/anchor",
                        Message = $"anchor `{value}` is not unique within this page",
                        PageId = _pageId,
                        SectionId = _sectionId,
                    });
                }
            }
        }

        private void WalkSettings(JsonNode settings)
        {
            // The site settings are a plain object; recognise the primitives it holds.
            if (settings is JsonObject obj)
            {
                foreach (var kv in obj)
                {
                    if (kv.Value is null) continue;
                    var childPath = _basePath + "/" + JsonPointerEscape(kv.Key);
                    if (kv.Key == "landmarks")
                    {
                        WalkLandmarks(kv.Value, childPath);
                        continue;
                    }
                    // `route` holds an id and display knobs, no primitives;
                    // CheckAsync resolves its id.
                    if (kv.Key == "route") continue;
                    var saved = _basePath;
                    _basePath = childPath;
                    WalkNode(kv.Value);
                    _basePath = saved;
                }
            }
        }

        // `settings.landmarks`: plain text names and descriptions, so only each
        // landmark's icon is checked, reported at `.../landmarks/<i>/icon/id`.
        private void WalkLandmarks(JsonNode landmarks, string path)
        {
            if (landmarks is not JsonArray arr) return;
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is not JsonObject landmark) continue;
                if (!landmark.TryGetPropertyValue("icon", out var iconNode) || iconNode is not JsonObject icon) continue;
                if (icon["source"] is not JsonValue sv || !sv.TryGetValue<string>(out var source)) continue;
                if (icon["id"] is not JsonValue iv || !iv.TryGetValue<string>(out var id)) continue;
                CheckIconRef($"{path}/{i}/icon/id", source, id);
            }
        }

        private void WalkNode(JsonNode node)
        {
            switch (node)
            {
                case JsonObject obj:
                    var savedBlockKind = _blockKind;
                    if (obj["kind"] is JsonValue kv0 && kv0.TryGetValue<string>(out var objKind)) _blockKind = objKind;
                    TryMediaRef(obj);
                    TryIcon(obj);
                    TryLink(obj);
                    foreach (var kv in obj)
                    {
                        if (kv.Value is null) continue;
                        var saved = _basePath;
                        _basePath = saved + "/" + JsonPointerEscape(kv.Key);
                        WalkNode(kv.Value);
                        _basePath = saved;
                    }
                    _blockKind = savedBlockKind;
                    break;
                case JsonArray arr:
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var el = arr[i];
                        if (el is null) continue;
                        var saved = _basePath;
                        _basePath = saved + "/" + i.ToString();
                        WalkNode(el);
                        _basePath = saved;
                    }
                    break;
                case JsonValue val:
                    // Inline strings are validated where their parents indicate they hold
                    // Inline content. Free-form strings (like enum values) are ignored.
                    if (val.TryGetValue<string>(out var s) && IsInlineSlot(_basePath))
                    {
                        var parseResult = InlineText.Parse(s);
                        foreach (var problem in parseResult.Problems)
                        {
                            Problems.Add(new ProblemRefDto
                            {
                                Path = _basePath,
                                Message = problem,
                                PageId = _pageId,
                                SectionId = _sectionId,
                                ItemId = _itemId,
                            });
                        }
                        foreach (var link in parseResult.Links)
                        {
                            CheckHref(_basePath, link.Href);
                        }
                        foreach (var icon in parseResult.Icons)
                        {
                            CheckIconRef(_basePath, icon.Source, icon.Id);
                        }
                    }
                    break;
            }
        }

        // MediaRef shape: `{ "mediaId": "<uuid>", "alt": <string|null>, "display"?: Display|null }`.
        private void TryMediaRef(JsonObject obj)
        {
            if (!HasTwoKeysAndOptionalDisplay(obj)) return;
            if (!obj.TryGetPropertyValue("mediaId", out var mediaIdNode)) return;
            if (!obj.TryGetPropertyValue("alt", out _)) return;
            if (mediaIdNode is not JsonValue mv || !mv.TryGetValue<string>(out var mediaId)) return;
            if (Guid.TryParse(mediaId, out var g)) MediaIds.Add(g);
            MediaRefs.Add(new MediaAssetRef(mediaId, _basePath + "/mediaId", _pageId, _sectionId, _itemId,
                AllowsVideoAt(_basePath)));
        }

        // Icon shape: `{ "source": "library"|"media", "id": <string>, "display"?: Display|null }`.
        private void TryIcon(JsonObject obj)
        {
            if (!HasTwoKeysAndOptionalDisplay(obj)) return;
            if (!obj.TryGetPropertyValue("source", out var sourceNode)) return;
            if (!obj.TryGetPropertyValue("id", out var idNode)) return;
            if (sourceNode is not JsonValue sv || !sv.TryGetValue<string>(out var source)) return;
            if (idNode is not JsonValue iv || !iv.TryGetValue<string>(out var id)) return;
            CheckIconRef(_basePath, source, id);
        }

        private static bool HasTwoKeysAndOptionalDisplay(JsonObject obj) =>
            obj.Count == 2 || (obj.Count == 3 && obj.ContainsKey("display"));

        // Link shape: `{ "label": <string>, "href": <string>, "icon": <Icon|null>, "newTab": <bool> }`.
        // The icon is handled by the recursion; here we check the href only.
        private void TryLink(JsonObject obj)
        {
            if (obj.Count != 4) return;
            if (!obj.TryGetPropertyValue("href", out var hrefNode)) return;
            if (!obj.ContainsKey("label") || !obj.ContainsKey("newTab") || !obj.ContainsKey("icon")) return;
            if (hrefNode is not JsonValue v || !v.TryGetValue<string>(out var href)) return;
            CheckHref(_basePath + "/href", href);
        }

        private void CheckHref(string path, string href)
        {
            if (string.IsNullOrEmpty(href))
            {
                Problems.Add(new ProblemRefDto
                {
                    Path = path,
                    Message = "href is empty",
                    PageId = _pageId,
                    SectionId = _sectionId,
                    ItemId = _itemId,
                });
                return;
            }
            if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (href.StartsWith("/", StringComparison.Ordinal))
            {
                // /<slug>[#anchor]
                var afterSlash = href.Substring(1);
                var hashIdx = afterSlash.IndexOf('#');
                var slug = hashIdx < 0 ? afterSlash : afterSlash.Substring(0, hashIdx);
                if (string.IsNullOrEmpty(slug))
                {
                    // A bare `/` is not accepted by the schema. The publish walker
                    // still reports it as a reference-level problem for clarity.
                    Problems.Add(new ProblemRefDto
                    {
                        Path = path,
                        Message = "site path href must include a page slug",
                        PageId = _pageId,
                        SectionId = _sectionId,
                        ItemId = _itemId,
                    });
                    return;
                }
                if (!_visibleSlugs.Contains(slug))
                {
                    Problems.Add(new ProblemRefDto
                    {
                        Path = path,
                        Message = $"site path href references unknown or hidden page `{slug}`",
                        PageId = _pageId,
                        SectionId = _sectionId,
                        ItemId = _itemId,
                    });
                }
                return;
            }
            Problems.Add(new ProblemRefDto
            {
                Path = path,
                Message = $"href `{href}` is not an http(s) URL, mailto:, or /slug",
                PageId = _pageId,
                SectionId = _sectionId,
                ItemId = _itemId,
            });
        }

        private void CheckIconRef(string path, string source, string id)
        {
            switch (source)
            {
                case "library":
                    if (_library is null) return;
                    if (!_library.Contains(id))
                    {
                        Problems.Add(new ProblemRefDto
                        {
                            Path = path,
                            Message = $"library icon `{id}` is not in the built-in library",
                            PageId = _pageId,
                            SectionId = _sectionId,
                            ItemId = _itemId,
                        });
                    }
                    break;
                case "media":
                    if (Guid.TryParse(id, out var g))
                    {
                        MediaIds.Add(g);
                    }
                    MediaRefs.Add(new MediaAssetRef(id, path, _pageId, _sectionId, _itemId));
                    break;
            }
        }

        // Recognises the slots the schemas mark as Inline (the primitives file).
        // A key ending in one of these names holds Inline content.
        private static readonly HashSet<string> InlineKeyNames = new(StringComparer.Ordinal)
        {
            "text", "heading", "title", "tagline", "caption", "attribution",
            "description", "label", "copy", "signedOutCopy", "closedCopy",
            "successText", "emptyText", "footerText", "siteName", "homeNavLabel",
            "scheduledAt", "wentLiveAt", "endedAt", "airborneFor", "body",
        };

        // A path names an Inline slot when its last segment is in InlineKeyNames
        // OR its parent segment names an array of inline strings (`items`, `content`).
        private static bool IsInlineSlot(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var lastSep = path.LastIndexOf('/');
            if (lastSep < 0) return false;
            var lastSegment = path.Substring(lastSep + 1);
            if (int.TryParse(lastSegment, out _))
            {
                // Path ends with an array index - check parent segment.
                var parentPath = path.Substring(0, lastSep);
                var parentSep = parentPath.LastIndexOf('/');
                if (parentSep < 0) return false;
                var parentSegment = parentPath.Substring(parentSep + 1);
                return parentSegment == "items";
            }
            return InlineKeyNames.Contains(lastSegment);
        }
    }

    private static string JsonPointerEscape(string s) =>
        s.Replace("~", "~0").Replace("/", "~1");
}
