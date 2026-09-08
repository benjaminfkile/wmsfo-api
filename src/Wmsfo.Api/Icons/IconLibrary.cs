using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Wmsfo.Api.Data;
using Wmsfo.Api.Media;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Icons;

// api.md 11a.7: loads icons/library.json and every icons/<id>.svg at boot, validates each
// with SvgValidator, computes each file's sha256 and the library hash (sha256 over the
// sorted `id:sha256` lines), and exposes Map (id → WMSFO_CDN_BASE_URL/icons/{sha256}.svg)
// and Infos for GET /admin/icons. EnsureWrittenAsync (sql.md 8.16 step 2) PUTs every icon
// with the immutable cache header when the compiled hash differs from the last-written
// hash the caller supplies, and reports whether it wrote so the migrator can rebuild the
// snapshot afterwards. The database compare-and-swap lives in the migrator (A4).
public sealed partial class IconLibrary
{
    public const string CdnPathPrefix = "icons/";
    public const string SvgContentType = "image/svg+xml";
    public const string ImmutableCacheControl = "public, max-age=31536000, immutable";

    private static readonly Regex IdPattern = MakeIdRegex();

    private readonly IReadOnlyList<IconFile> _files;
    private readonly IReadOnlyDictionary<string, string> _map;
    private readonly IReadOnlyList<IconInfo> _infos;
    private readonly string _libraryHash;

    private IconLibrary(
        IReadOnlyList<IconFile> files,
        IReadOnlyDictionary<string, string> map,
        IReadOnlyList<IconInfo> infos,
        string libraryHash)
    {
        _files = files;
        _map = map;
        _infos = infos;
        _libraryHash = libraryHash;
    }

    public IReadOnlyList<IconFile> Files => _files;
    public IReadOnlyDictionary<string, string> Map => _map;
    public IReadOnlyList<IconInfo> Infos => _infos;
    public string LibraryHash => _libraryHash;

    public bool Contains(string id) => _map.ContainsKey(id);

    public static IconLibrary Load(string rootDir, string cdnBaseUrl)
    {
        ArgumentNullException.ThrowIfNull(rootDir);
        ArgumentNullException.ThrowIfNull(cdnBaseUrl);
        if (!Directory.Exists(rootDir))
            throw new DirectoryNotFoundException($"icon library directory not found: {rootDir}");

        var manifestPath = Path.Combine(rootDir, "library.json");
        if (!File.Exists(manifestPath))
            throw new FileNotFoundException($"icon library manifest missing: {manifestPath}");

        var manifestBytes = File.ReadAllBytes(manifestPath);
        var manifest = JsonSerializer.Deserialize<LibraryManifest>(manifestBytes, ManifestOptions)
            ?? throw new InvalidDataException($"icon library manifest is empty: {manifestPath}");
        if (manifest.Icons is null || manifest.Icons.Count == 0)
            throw new InvalidDataException($"icon library manifest lists no icons: {manifestPath}");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<IconFile>(manifest.Icons.Count);
        var infos = new List<IconInfo>(manifest.Icons.Count);
        var normalizedCdn = cdnBaseUrl.TrimEnd('/');

        foreach (var entry in manifest.Icons)
        {
            if (entry is null) throw new InvalidDataException("icon library manifest has a null entry");
            var id = entry.Id ?? throw new InvalidDataException("icon library entry missing id");
            var name = entry.Name ?? throw new InvalidDataException($"icon '{id}' missing name");
            var tags = entry.Tags is null
                ? Array.Empty<string>()
                : entry.Tags.ToArray();

            if (!IdPattern.IsMatch(id))
                throw new InvalidDataException($"icon id does not match ^[a-z0-9]+(-[a-z0-9]+)*$: '{id}'");
            if (!seen.Add(id))
                throw new InvalidDataException($"duplicate icon id in library.json: '{id}'");

            var svgPath = Path.Combine(rootDir, id + ".svg");
            if (!File.Exists(svgPath))
                throw new FileNotFoundException($"icon file missing for '{id}': {svgPath}");

            var bytes = File.ReadAllBytes(svgPath);
            var validation = SvgValidator.Validate(bytes);
            if (!validation.IsValid)
                throw new InvalidDataException($"icon '{id}' failed SvgValidator: {validation.Reason}");

            var sha = ToLowerHex(SHA256.HashData(bytes));
            var key = CdnPathPrefix + sha + ".svg";
            var url = normalizedCdn + "/" + key;

            files.Add(new IconFile(id, bytes, sha, key));
            infos.Add(new IconInfo(id, name, tags, url));
        }

        var sorted = files.OrderBy(f => f.Id, StringComparer.Ordinal).ToArray();
        var mapBuilder = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in sorted)
            mapBuilder[f.Id] = normalizedCdn + "/" + f.Key;

        var libraryHash = ComputeLibraryHash(sorted);

        infos.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));

        return new IconLibrary(sorted, mapBuilder, infos, libraryHash);
    }

    // sql.md 8.16 step 2 / api.md 11a.7: PUT every icon with the immutable cache header when
    // the caller's last-written hash differs from the compiled library hash. Returns true when
    // any bytes were written, so the migrator can trigger a snapshot rebuild.
    public async Task<bool> EnsureWrittenAsync(
        IObjectStore store,
        string? existingHash,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (string.Equals(existingHash, _libraryHash, StringComparison.Ordinal))
            return false;

        foreach (var file in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await store.PutObjectAsync(
                file.Key,
                file.Bytes,
                SvgContentType,
                ImmutableCacheControl,
                tag: null,
                cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // Boot migrator wrapper: reads the last-written hash from icon_library_state, PUTs every
    // icon when it differs from the compiled hash, updates the row on success, and returns
    // whether it wrote. The migrator runs under pg_advisory_lock, so no `for update` is needed
    // here.
    public async Task<bool> EnsureWrittenAsync(
        IObjectStore store,
        WmsfoDbContext db,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(db);

        var state = await db.IconLibraryState.SingleAsync(x => x.Id == 1, cancellationToken).ConfigureAwait(false);
        var existing = state.LibrarySha256?.Trim();
        var wrote = await EnsureWrittenAsync(store, existing, cancellationToken).ConfigureAwait(false);
        if (!wrote) return false;

        state.LibrarySha256 = _libraryHash;
        state.WrittenAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static string ComputeLibraryHash(IReadOnlyList<IconFile> sortedFiles)
    {
        var sb = new StringBuilder(sortedFiles.Count * 80);
        foreach (var f in sortedFiles)
        {
            sb.Append(f.Id);
            sb.Append(':');
            sb.Append(f.Sha256);
            sb.Append('\n');
        }
        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        return ToLowerHex(SHA256.HashData(bytes));
    }

    private static string ToLowerHex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = false,
    };

    private sealed class LibraryManifest
    {
        public List<LibraryEntry>? Icons { get; set; }
    }

    private sealed class LibraryEntry
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public List<string>? Tags { get; set; }
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex MakeIdRegex();
}

public sealed record IconFile(string Id, byte[] Bytes, string Sha256, string Key);

public sealed record IconInfo(string Id, string Name, IReadOnlyList<string> Tags, string Url);
