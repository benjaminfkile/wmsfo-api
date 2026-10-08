using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Help;

// api.md 11a.9: the help topic seed. `help/topics.json` is a JSON array of
// { key, page, label, title, body, links: [{ label, to }] }, one entry per
// help popover of the admin panel. The file is located like `icons/` (beside
// the binary or up the tree from the content root) and validated at startup;
// any entry the rules refuse stops the boot with a message naming the entry.
public sealed partial class HelpTopicSeed
{
    public const string RelativePath = "help/topics.json";
    public const int TitleMax = 120;
    public const int BodyMax = 2000;
    public const int LinksMax = 6;
    public const int LinkLabelMax = 60;

    private static readonly Regex KeyPattern = MakeKeyRegex();

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false,
    };

    private HelpTopicSeed(IReadOnlyList<HelpTopicSeedEntry> entries) => Entries = entries;

    // The entries in file order.
    public IReadOnlyList<HelpTopicSeedEntry> Entries { get; }

    public static bool IsValidKey(string? key) => key is not null && KeyPattern.IsMatch(key);

    // A link target is a path of the admin panel (one leading `/`, not `//`)
    // or an absolute https URL with a host.
    public static bool IsValidLinkTarget(string? to)
    {
        if (string.IsNullOrEmpty(to) || to.Any(char.IsWhiteSpace)) return false;
        if (to.StartsWith('/')) return !to.StartsWith("//", StringComparison.Ordinal);
        return to.StartsWith("https://", StringComparison.Ordinal)
            && Uri.TryCreate(to, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && !string.IsNullOrEmpty(uri.Host);
    }

    // True when the text carries an em dash (U+2014) or an en dash (U+2013).
    public static bool HasDash(string? text) =>
        text is not null && (text.Contains('\u2014') || text.Contains('\u2013'));

    // The directory holding `help/topics.json`: each start directory and up to
    // seven of its parents are tried in turn; null when none holds the file.
    public static string? ResolveRoot(params string?[] starts)
    {
        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start)) continue;
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, "help", "topics.json"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    // Reads and validates `<root>/help/topics.json`.
    public static HelpTopicSeed Load(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var path = Path.Combine(root, "help", "topics.json");
        if (!File.Exists(path))
            throw new FileNotFoundException($"help topic seed missing: {path}");
        return Parse(File.ReadAllBytes(path), path);
    }

    // Validates the seed bytes; `source` names the file in the messages.
    public static HelpTopicSeed Parse(byte[] utf8, string source = RelativePath)
    {
        List<RawEntry?>? raw;
        try
        {
            raw = JsonSerializer.Deserialize<List<RawEntry?>>(utf8, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source}: not a JSON array of help topics: {ex.Message}", ex);
        }
        if (raw is null) throw new InvalidDataException($"{source}: not a JSON array of help topics");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var entries = new List<HelpTopicSeedEntry>(raw.Count);
        for (var i = 0; i < raw.Count; i++)
        {
            var e = raw[i] ?? throw new InvalidDataException($"{source}: entry {i} is null");
            var key = e.Key;
            var name = key is null ? $"entry {i}" : $"entry '{key}'";
            InvalidDataException Fail(string message) => new($"{source}: {name}: {message}");

            if (!IsValidKey(key)) throw Fail("key must match ^[a-z0-9-]+(\\.[a-z0-9-]+)*$");
            if (!seen.Add(key!)) throw Fail("duplicate key");
            if (string.IsNullOrWhiteSpace(e.Page)) throw Fail("page is empty");
            if (string.IsNullOrWhiteSpace(e.Label)) throw Fail("label is empty");
            if (string.IsNullOrWhiteSpace(e.Title)) throw Fail("title is empty");
            if (string.IsNullOrWhiteSpace(e.Body)) throw Fail("body is empty");
            if (e.Title!.Trim().Length > TitleMax) throw Fail($"title is longer than {TitleMax} characters");
            if (e.Body!.Trim().Length > BodyMax) throw Fail($"body is longer than {BodyMax} characters");
            var links = e.Links ?? throw Fail("links is missing");
            if (links.Count > LinksMax) throw Fail($"more than {LinksMax} links");

            var outLinks = new List<HelpLinkDto>(links.Count);
            for (var j = 0; j < links.Count; j++)
            {
                var l = links[j] ?? throw Fail($"links[{j}] is null");
                if (string.IsNullOrWhiteSpace(l.Label) || l.Label.Trim().Length > LinkLabelMax)
                    throw Fail($"links[{j}].label must be 1 to {LinkLabelMax} characters");
                if (!IsValidLinkTarget(l.To))
                    throw Fail($"links[{j}].to must be a / path or an https:// URL");
                if (HasDash(l.Label) || HasDash(l.To))
                    throw Fail($"links[{j}] carries an em dash or an en dash");
                outLinks.Add(new HelpLinkDto { Label = l.Label.Trim(), To = l.To! });
            }
            if (HasDash(e.Page) || HasDash(e.Label) || HasDash(e.Title) || HasDash(e.Body))
                throw Fail("carries an em dash or an en dash");

            entries.Add(new HelpTopicSeedEntry(
                key!, e.Page!.Trim(), e.Label!.Trim(), e.Title.Trim(), e.Body.Trim(), outLinks));
        }
        return new HelpTopicSeed(entries);
    }

    // contracts 13: `contracts/help-keys.json`, the seed's { key, page, label }
    // in file order as canonical JSON.
    public byte[] KeysToCanonicalJson() =>
        CanonicalJson.SerializeToUtf8Bytes(
            Entries.Select(e => new HelpKeyEntry { Key = e.Key, Page = e.Page, Label = e.Label }).ToList());

    [GeneratedRegex("^[a-z0-9-]+(\\.[a-z0-9-]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex MakeKeyRegex();

    private sealed class RawEntry
    {
        public string? Key { get; set; }
        public string? Page { get; set; }
        public string? Label { get; set; }
        public string? Title { get; set; }
        public string? Body { get; set; }
        public List<RawLink?>? Links { get; set; }
    }

    private sealed class RawLink
    {
        public string? Label { get; set; }
        public string? To { get; set; }
    }

    private sealed class HelpKeyEntry
    {
        public string Key { get; set; } = "";
        public string Page { get; set; } = "";
        public string Label { get; set; } = "";
    }
}

public sealed record HelpTopicSeedEntry(
    string Key, string Page, string Label, string Title, string Body, IReadOnlyList<HelpLinkDto> Links);

// sql.md 8.16 step 3: brings `help_topic` in line with the seed on every boot.
public sealed class HelpTopics
{
    public HelpTopics(HelpTopicSeed seed) => Seed = seed;

    public HelpTopicSeed Seed { get; }

    public sealed record EnsureResult(int Inserted, int Updated, int Unchanged, int Deleted);

    // Canonical JSON of a link list, the form `links` and `default_links` store.
    public static string LinksJson(IReadOnlyList<HelpLinkDto> links) =>
        Encoding.UTF8.GetString(CanonicalJson.SerializeToUtf8Bytes(links));

    // One transaction: every seed entry is upserted (page, label, and the
    // defaults; `default_updated_at` moves only when a default changed; the
    // shown title, body, and links follow the defaults while `edited_by` is
    // null), and every row whose key the seed no longer lists is deleted. A
    // row the seed leaves as it is gets no write.
    public async Task<EnsureResult> EnsureWrittenAsync(NpgsqlConnection conn, CancellationToken ct = default)
    {
        var entries = Seed.Entries;
        var keys = entries.Select(e => e.Key).ToArray();
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);

        int inserted = 0, updated = 0;
        await using (var upsert = new NpgsqlCommand(@"
insert into help_topic (key, page, label, title, body, links,
                        default_title, default_body, default_links, default_updated_at, updated_at)
select s.key, s.page, s.label, s.title, s.body, s.links::jsonb,
       s.title, s.body, s.links::jsonb, now(), now()
from unnest($1::text[], $2::text[], $3::text[], $4::text[], $5::text[], $6::text[])
  as s(key, page, label, title, body, links)
on conflict (key) do update set
  page = excluded.page,
  label = excluded.label,
  default_title = excluded.default_title,
  default_body = excluded.default_body,
  default_links = excluded.default_links,
  default_updated_at = case
    when (help_topic.default_title, help_topic.default_body, help_topic.default_links)
         is distinct from (excluded.default_title, excluded.default_body, excluded.default_links)
    then now() else help_topic.default_updated_at end,
  title = case when help_topic.edited_by is null then excluded.default_title else help_topic.title end,
  body = case when help_topic.edited_by is null then excluded.default_body else help_topic.body end,
  links = case when help_topic.edited_by is null then excluded.default_links else help_topic.links end,
  updated_at = now()
where (help_topic.page, help_topic.label, help_topic.default_title, help_topic.default_body, help_topic.default_links)
      is distinct from (excluded.page, excluded.label, excluded.default_title, excluded.default_body, excluded.default_links)
   or (help_topic.edited_by is null
       and (help_topic.title, help_topic.body, help_topic.links)
           is distinct from (excluded.default_title, excluded.default_body, excluded.default_links))
returning (xmax = 0);", conn, tx))
        {
            upsert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = keys });
            upsert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = entries.Select(e => e.Page).ToArray() });
            upsert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = entries.Select(e => e.Label).ToArray() });
            upsert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = entries.Select(e => e.Title).ToArray() });
            upsert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = entries.Select(e => e.Body).ToArray() });
            upsert.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = entries.Select(e => LinksJson(e.Links)).ToArray() });
            await using var reader = await upsert.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetBoolean(0)) inserted++;
                else updated++;
            }
        }

        int deleted;
        await using (var prune = new NpgsqlCommand(
            "delete from help_topic where not (key = any($1));", conn, tx))
        {
            prune.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text, Value = keys });
            deleted = await prune.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return new EnsureResult(inserted, updated, entries.Count - inserted - updated, deleted);
    }
}
