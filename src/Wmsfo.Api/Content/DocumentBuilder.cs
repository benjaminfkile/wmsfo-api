using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Content;

// api.md 11a.3: reads the working set in document order and produces the
// ContentDocument the publisher hashes and the snapshot embeds. `Load` walks the
// same rows but produces the internal WorkingSet the ReferenceChecker consumes;
// the two share the same query so callers get one round trip.
public sealed class DocumentBuilder
{
    // Reserved page slugs that must never appear as a `none` page. From contracts 4.5 Pages.
    public static readonly HashSet<string> ReservedSlugs = new(StringComparer.Ordinal)
    {
        "auth", "preview", "api", "admin", "assets",
    };

    // Page-role ordering for document output: role pages first in status order
    // (no_event first), then `none` by navPosition then id. `none` is not in the
    // role sort table so callers detect it separately.
    private static readonly Dictionary<string, int> RoleSortOrder = new(StringComparer.Ordinal)
    {
        ["no_event"] = 0,
        ["planned"] = 1,
        ["scheduled"] = 2,
        ["live"] = 3,
        ["ended"] = 4,
        ["cancelled"] = 5,
    };

    public sealed record LoadResult(
        ReferenceChecker.WorkingSet WorkingSet,
        ContentDocument Document,
        JsonNode? SettingsData,
        DateTimeOffset? DraftUpdatedAt);

    // Reads every row in a single connection. `includeHidden` is false for the
    // publish-time document (which omits hidden rows) and true for admin list
    // endpoints (which include them to be paged).
    public async Task<LoadResult> LoadAsync(
        NpgsqlConnection conn,
        NpgsqlTransaction? tx,
        bool includeHidden,
        CancellationToken ct)
    {
        var pages = new List<PageRow>();
        await using (var cmd = new NpgsqlCommand(
            @"select id, slug, title, nav_label, nav_position, is_hidden, role
              from page
              order by role, nav_position, id;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                pages.Add(new PageRow
                {
                    Id = reader.GetInt64(0),
                    Slug = reader.GetString(1),
                    Title = reader.GetString(2),
                    NavLabel = reader.IsDBNull(3) ? null : reader.GetString(3),
                    NavPosition = reader.GetInt32(4),
                    IsHidden = reader.GetBoolean(5),
                    Role = reader.GetString(6),
                });
            }
        }

        var pageById = pages.ToDictionary(p => p.Id);

        var sections = new List<SectionRow>();
        await using (var cmd = new NpgsqlCommand(
            @"select id, page_id, kind, position, is_hidden, data, presentation
              from section
              order by page_id, position, id;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                sections.Add(new SectionRow
                {
                    Id = reader.GetInt64(0),
                    PageId = reader.GetInt64(1),
                    Kind = reader.GetString(2),
                    Position = reader.GetInt32(3),
                    IsHidden = reader.GetBoolean(4),
                    Data = JsonNode.Parse(reader.GetString(5)),
                    Presentation = JsonNode.Parse(reader.GetString(6)),
                });
            }
        }

        var items = new List<ItemRow>();
        await using (var cmd = new NpgsqlCommand(
            @"select id, section_id, position, is_hidden, data
              from section_item
              order by section_id, position, id;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                items.Add(new ItemRow
                {
                    Id = reader.GetInt64(0),
                    SectionId = reader.GetInt64(1),
                    Position = reader.GetInt32(2),
                    IsHidden = reader.GetBoolean(3),
                    Data = JsonNode.Parse(reader.GetString(4)),
                });
            }
        }

        JsonNode? settingsData = null;
        DateTimeOffset? draftUpdatedAt = null;
        await using (var cmd = new NpgsqlCommand(
            "select data, updated_at from site_setting_draft where id = 1;", conn, tx))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                settingsData = JsonNode.Parse(reader.GetString(0));
                if (!reader.IsDBNull(1)) draftUpdatedAt = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        // Group items by section.
        var itemsBySection = new Dictionary<long, List<ItemRow>>();
        foreach (var it in items)
        {
            if (!itemsBySection.TryGetValue(it.SectionId, out var list))
            {
                list = new List<ItemRow>();
                itemsBySection[it.SectionId] = list;
            }
            list.Add(it);
        }

        // Group sections by page.
        var sectionsByPage = new Dictionary<long, List<SectionRow>>();
        foreach (var s in sections)
        {
            if (!sectionsByPage.TryGetValue(s.PageId, out var list))
            {
                list = new List<SectionRow>();
                sectionsByPage[s.PageId] = list;
            }
            list.Add(s);
        }

        // Sort into contract order: role pages by RoleSortOrder, then `none` by navPosition, id.
        var orderedPages = pages
            .Where(p => includeHidden || !p.IsHidden)
            .OrderBy(p => IsRolePage(p) ? 0 : 1)
            .ThenBy(p => IsRolePage(p) ? RoleSortOrder.TryGetValue(p.Role, out var so) ? so : 100 : 0)
            .ThenBy(p => p.NavPosition)
            .ThenBy(p => p.Id)
            .ToList();

        // Build the document + the WorkingSet in parallel.
        var docPages = new List<ContentPage>();
        var wsPages = new List<ReferenceChecker.PageInput>();
        foreach (var page in orderedPages)
        {
            var docSections = new List<ContentSection>();
            var wsSections = new List<ReferenceChecker.SectionInput>();
            if (sectionsByPage.TryGetValue(page.Id, out var pageSections))
            {
                foreach (var section in pageSections)
                {
                    if (!includeHidden && section.IsHidden) continue;
                    var docItems = new List<ContentItem>();
                    var wsItems = new List<ReferenceChecker.ItemInput>();
                    if (itemsBySection.TryGetValue(section.Id, out var sectionItems))
                    {
                        foreach (var item in sectionItems)
                        {
                            if (!includeHidden && item.IsHidden) continue;
                            docItems.Add(new ContentItem
                            {
                                Id = item.Id,
                                Data = item.Data ?? new JsonObject(),
                            });
                            wsItems.Add(new ReferenceChecker.ItemInput(item.Id, item.Data));
                        }
                    }
                    docSections.Add(new ContentSection
                    {
                        Id = section.Id,
                        Kind = section.Kind,
                        Presentation = DeserializePresentation(section.Presentation),
                        Data = section.Data ?? new JsonObject(),
                        Items = docItems,
                    });
                    wsSections.Add(new ReferenceChecker.SectionInput(
                        section.Id, section.Kind, section.Data, section.Presentation, wsItems));
                }
            }
            docPages.Add(new ContentPage
            {
                Id = page.Id,
                Slug = page.Slug,
                Title = page.Title,
                NavLabel = page.NavLabel,
                NavPosition = page.NavPosition,
                Role = page.Role,
                Sections = docSections,
            });
            wsPages.Add(new ReferenceChecker.PageInput(
                page.Id, page.Slug, page.Role, wsSections));
        }

        var settings = ParseSiteSettings(settingsData);
        var document = new ContentDocument
        {
            SchemaVersion = 1,
            Settings = settings,
            Pages = docPages,
        };
        var workingSet = new ReferenceChecker.WorkingSet(wsPages, settingsData);
        return new LoadResult(workingSet, document, settingsData, draftUpdatedAt);
    }

    // Collects every media id the document references. Called after LoadAsync so
    // the caller can insert content_version.media_ids.
    public static Guid[] CollectReferencedMediaIds(ReferenceChecker.WorkingSet ws)
    {
        var ids = new HashSet<Guid>();
        // Walk pages + sections + items.
        foreach (var page in ws.Pages)
        {
            foreach (var section in page.Sections)
            {
                if (section.Data is not null) CollectFromNode(section.Data, ids);
                if (section.Presentation is not null) CollectFromNode(section.Presentation, ids);
                foreach (var item in section.Items)
                {
                    if (item.Data is not null) CollectFromNode(item.Data, ids);
                }
            }
        }
        if (ws.Settings is not null) CollectFromNode(ws.Settings, ids);
        return ids.ToArray();
    }

    private static void CollectFromNode(JsonNode node, HashSet<Guid> into)
    {
        switch (node)
        {
            case JsonObject obj:
                // MediaRef: { mediaId, alt }
                if (obj.Count == 2 &&
                    obj.TryGetPropertyValue("mediaId", out var mediaIdNode) &&
                    obj.ContainsKey("alt") &&
                    mediaIdNode is JsonValue mv && mv.TryGetValue<string>(out var mediaId) &&
                    Guid.TryParse(mediaId, out var mediaGuid))
                {
                    into.Add(mediaGuid);
                }
                // Icon: { source: "media", id }
                if (obj.Count == 2 &&
                    obj.TryGetPropertyValue("source", out var sourceNode) &&
                    obj.TryGetPropertyValue("id", out var idNode) &&
                    sourceNode is JsonValue sv && sv.TryGetValue<string>(out var source) &&
                    source == "media" &&
                    idNode is JsonValue iv && iv.TryGetValue<string>(out var iconId) &&
                    Guid.TryParse(iconId, out var iconGuid))
                {
                    into.Add(iconGuid);
                }
                foreach (var kv in obj)
                {
                    if (kv.Value is not null) CollectFromNode(kv.Value, into);
                }
                // Inline icons.
                if (obj.Count > 0)
                {
                    // No-op — inline scanning below relies on WalkStringsForInlineIcons.
                }
                break;
            case JsonArray arr:
                foreach (var el in arr)
                {
                    if (el is not null) CollectFromNode(el, into);
                }
                break;
            case JsonValue val:
                if (val.TryGetValue<string>(out var s))
                {
                    var parsed = InlineText.Parse(s);
                    foreach (var icon in parsed.Icons)
                    {
                        if (icon.Source == "media" && Guid.TryParse(icon.Id, out var g))
                        {
                            into.Add(g);
                        }
                    }
                }
                break;
        }
    }

    // The presentation column is jsonb — the row's raw value is the
    // authoritative Presentation. Deserialize into the DTO so the snapshot
    // fixture ordering carries through; unknown properties are preserved via
    // JsonNode when persisted so a future field survives a round trip.
    private static Presentation DeserializePresentation(JsonNode? node)
    {
        if (node is null) return new Presentation();
        try
        {
            var parsed = node.Deserialize<Presentation>(CanonicalJson.Options);
            return parsed ?? new Presentation();
        }
        catch (JsonException)
        {
            return new Presentation();
        }
    }

    private static SiteSettings ParseSiteSettings(JsonNode? node)
    {
        if (node is null) return new SiteSettings();
        try
        {
            var parsed = node.Deserialize<SiteSettings>(CanonicalJson.Options);
            return parsed ?? new SiteSettings();
        }
        catch (JsonException)
        {
            return new SiteSettings();
        }
    }

    private static bool IsRolePage(PageRow p) =>
        !string.Equals(p.Role, "none", StringComparison.Ordinal);

    private sealed class PageRow
    {
        public long Id;
        public string Slug = "";
        public string Title = "";
        public string? NavLabel;
        public int NavPosition;
        public bool IsHidden;
        public string Role = "";
    }

    private sealed class SectionRow
    {
        public long Id;
        public long PageId;
        public string Kind = "";
        public int Position;
        public bool IsHidden;
        public JsonNode? Data;
        public JsonNode? Presentation;
    }

    private sealed class ItemRow
    {
        public long Id;
        public long SectionId;
        public int Position;
        public bool IsHidden;
        public JsonNode? Data;
    }
}
