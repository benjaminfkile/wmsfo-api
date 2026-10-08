using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Npgsql;
using Wmsfo.Api.Http;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Themes;

// The seeded tracker theme style bodies (api.md 11a.10 "Seeded style objects";
// sql.md 8.16 step 2b). Load reads contracts/fixtures/themes/<key>.json for the
// eight rows of TrackerThemeSeed, canonicalizes each, and refuses a body whose
// hash differs from its row's styleSha256. EnsureWrittenAsync PUTs the object
// of every tracker_theme row whose themes/{style_sha256}.json is absent and
// whose body is a seeded one.
public sealed class ThemeStyles
{
    public const string RelativePath = "contracts/fixtures/themes";
    public const string ContentType = "application/json; charset=utf-8";
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";
    public static readonly TimeSpan ObjectCallTimeout = TimeSpan.FromSeconds(3);

    public sealed record Body(string Key, string Sha256, byte[] Bytes);

    public sealed record EnsureResult(int Written, int Present, int Missing);

    private readonly Dictionary<string, Body> _bySha;

    private ThemeStyles(IReadOnlyList<Body> bodies)
    {
        Bodies = bodies;
        _bySha = bodies.ToDictionary(b => b.Sha256, StringComparer.Ordinal);
    }

    // The eight bodies in seed order.
    public IReadOnlyList<Body> Bodies { get; }

    public static string ObjectKey(string styleSha256) => "themes/" + styleSha256 + ".json";

    // The directory holding contracts/fixtures/themes/: each start directory and
    // up to seven of its parents are tried in turn; null when none holds it.
    public static string? ResolveRoot(params string?[] starts)
    {
        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start)) continue;
            var dir = new DirectoryInfo(start);
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                if (File.Exists(Path.Combine(dir.FullName, RelativePath, "seed.json"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    // Reads, canonicalizes, and hash-checks the eight seeded bodies under
    // `<root>/contracts/fixtures/themes/`.
    public static ThemeStyles Load(string root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var bodies = new List<Body>(TrackerThemeSeed.Themes.Count);
        foreach (var theme in TrackerThemeSeed.Themes)
        {
            var path = Path.Combine(root, RelativePath, theme.Key + ".json");
            if (!File.Exists(path))
                throw new FileNotFoundException($"tracker theme style missing: {path}");
            byte[] canonical;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                canonical = Canonicalize(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"{path}: not a JSON style body: {ex.Message}", ex);
            }
            var sha = CanonicalJson.Sha256Hex(canonical);
            if (!string.Equals(sha, theme.StyleSha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"{path}: canonical sha256 {sha} differs from the seeded style_sha256 {theme.StyleSha256} " +
                    $"of theme {theme.Renderer}/{theme.Key}; a style body change is a migration");
            }
            bodies.Add(new Body(theme.Key, sha, canonical));
        }
        return new ThemeStyles(bodies);
    }

    // The canonical style body: object properties in ordinal order at every
    // depth, no whitespace, numbers as read, and only the escapes JSON requires,
    // so the attribution's copyright sign and the < and > of the seeded MapLibre
    // bodies stay as the seeded hashes have them.
    public static byte[] Canonicalize(JsonElement style) =>
        CanonicalJson.SerializeOpaqueToUtf8Bytes(style, JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

    // sql.md 8.16 step 2b: HEAD every tracker_theme row's style object; PUT the
    // seeded body when it is absent (one attempt, 3 s; a failure throws so the
    // boot retries); log a Warning with the theme id when an absent object's
    // body is not a seeded one; stamp tracker_theme_state when anything was
    // written.
    public async Task<EnsureResult> EnsureWrittenAsync(
        NpgsqlConnection conn, IObjectStore store, ILogger logger, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);

        var rows = new List<(long Id, string Sha)>();
        await using (var cmd = new NpgsqlCommand("select id, style_sha256 from tracker_theme order by id;", conn))
        await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
                rows.Add((reader.GetInt64(0), reader.GetString(1).Trim()));
        }

        int written = 0, present = 0, missing = 0;
        var checkedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, sha) in rows)
        {
            var key = ObjectKey(sha);
            if (!checkedKeys.Add(key))
            {
                present++;
                continue;
            }

            ObjectHead? head;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(ObjectCallTimeout);
                head = await store.HeadObjectAsync(key, cts.Token).ConfigureAwait(false);
            }
            if (head is not null)
            {
                present++;
                continue;
            }

            if (!_bySha.TryGetValue(sha, out var body))
            {
                missing++;
                logger.LogWarning(
                    "theme style object missing themeId={ThemeId} key={Key}; marker={Marker}",
                    id, key, LogMarkers.ThemeStyleMissing);
                continue;
            }

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(ObjectCallTimeout);
                await store.PutObjectAsync(key, body.Bytes, ContentType, ImmutableCacheControl, tag: null, cts.Token)
                    .ConfigureAwait(false);
            }
            written++;
        }

        if (written > 0)
        {
            await using var stamp = new NpgsqlCommand(
                "update tracker_theme_state set written_at = now() where id = 1;", conn);
            await stamp.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        return new EnsureResult(written, present, missing);
    }
}
