using System.Text.Json;
using Wmsfo.Api.Icons;
using Wmsfo.Api.Media;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

public class IconLibraryTests
{
    private const string CdnBase = "https://cdn.example";

    public static IEnumerable<object[]> IconFiles()
    {
        foreach (var path in Directory.EnumerateFiles(IconsPaths.IconsDir, "*.svg"))
            yield return new object[] { Path.GetFileName(path) };
    }

    // Acceptance criterion 689 (file side): every SVG file on disk passes the validator.
    [Theory]
    [MemberData(nameof(IconFiles))]
    public void Every_icon_file_passes_SvgValidator(string filename)
    {
        var bytes = File.ReadAllBytes(Path.Combine(IconsPaths.IconsDir, filename));
        var result = SvgValidator.Validate(bytes);
        Assert.True(result.IsValid, $"{filename} did not validate: {result.Reason}");
    }

    // Acceptance criterion 689 (count and manifest side): at least 55 icons in icons/, every
    // one listed in library.json.
    [Fact]
    public void Library_lists_every_svg_and_meets_minimum_count()
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);

        Assert.True(lib.Files.Count >= 55, $"expected >= 55 icons, saw {lib.Files.Count}");

        var manifestIds = lib.Files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var svgFiles = Directory.EnumerateFiles(IconsPaths.IconsDir, "*.svg")
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .ToHashSet(StringComparer.Ordinal);

        var unlisted = svgFiles.Except(manifestIds).OrderBy(s => s).ToList();
        Assert.True(unlisted.Count == 0, "SVGs on disk missing from library.json: " + string.Join(", ", unlisted));

        var missingFiles = manifestIds.Except(svgFiles).OrderBy(s => s).ToList();
        Assert.True(missingFiles.Count == 0, "library.json ids without an SVG file: " + string.Join(", ", missingFiles));
    }

    // Acceptance criterion 690: the seeds and starter content depend on these ids.
    [Theory]
    [InlineData("cookie")]
    [InlineData("gingerbread")]
    [InlineData("cookie-swirl")]
    [InlineData("cookie-star")]
    [InlineData("cookie-smile")]
    [InlineData("sleigh")]
    [InlineData("santa-hat")]
    [InlineData("helicopter")]
    [InlineData("candy-cane")]
    [InlineData("star")]
    [InlineData("heart")]
    [InlineData("facebook")]
    [InlineData("instagram")]
    [InlineData("snowflake")]
    [InlineData("map-pin")]
    public void Required_ids_are_present(string id)
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);
        Assert.True(lib.Contains(id), $"required icon id '{id}' missing from library.json");
    }

    // Acceptance criterion 691: the library hash is deterministic across two loads.
    [Fact]
    public void LibraryHash_is_deterministic_across_two_loads()
    {
        var a = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);
        var b = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);
        Assert.Equal(a.LibraryHash, b.LibraryHash);
        Assert.Equal(64, a.LibraryHash.Length);
        Assert.All(a.LibraryHash, c => Assert.True(
            (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'),
            "library hash must be lowercase hex"));
    }

    // Map values must be the CDN base + icons/{sha256}.svg for the id's sha256.
    [Fact]
    public void Map_urls_use_cdn_base_and_file_sha256()
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);
        foreach (var file in lib.Files)
        {
            var expected = $"{CdnBase}/icons/{file.Sha256}.svg";
            Assert.Equal(expected, lib.Map[file.Id]);
        }
    }

    // Map iteration order is ascending id (see api.md 11a.7: "id to CDN URL, ascending").
    [Fact]
    public void Map_iteration_order_is_ascending_id()
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);
        var seen = lib.Map.Keys.ToArray();
        var sorted = seen.OrderBy(s => s, StringComparer.Ordinal).ToArray();
        Assert.Equal(sorted, seen);
    }

    // Ids in the manifest must match ^[a-z0-9]+(-[a-z0-9]+)*$.
    [Fact]
    public void Manifest_ids_match_the_id_grammar()
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);
        var re = new System.Text.RegularExpressions.Regex("^[a-z0-9]+(-[a-z0-9]+)*$");
        foreach (var info in lib.Infos)
            Assert.True(re.IsMatch(info.Id), $"id '{info.Id}' does not match the id grammar");
    }

    // Manifest name and tags round-trip into Infos.
    [Fact]
    public void Infos_carry_name_and_tags_from_manifest()
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(IconsPaths.LibraryJson));
        var entries = doc.RootElement.GetProperty("icons").EnumerateArray().ToDictionary(
            e => e.GetProperty("id").GetString()!,
            e => (e.GetProperty("name").GetString()!,
                  e.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!).ToArray()));
        foreach (var info in lib.Infos)
        {
            var (name, tags) = entries[info.Id];
            Assert.Equal(name, info.Name);
            Assert.Equal(tags, info.Tags.ToArray());
        }
    }

    // EnsureWrittenAsync writes every icon exactly once when the caller's hash differs.
    [Fact]
    public async Task EnsureWrittenAsync_writes_when_hash_differs()
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);
        var store = new RecordingObjectStore();
        var wrote = await lib.EnsureWrittenAsync(store, existingHash: null);
        Assert.True(wrote);
        Assert.Equal(lib.Files.Count, store.Puts.Count);
        foreach (var file in lib.Files)
        {
            Assert.Contains(store.Puts, p => p.Key == file.Key
                                             && p.ContentType == IconLibrary.SvgContentType
                                             && p.CacheControl == IconLibrary.ImmutableCacheControl
                                             && p.Bytes.SequenceEqual(file.Bytes));
        }
    }

    // EnsureWrittenAsync skips the PUTs when the hash already matches.
    [Fact]
    public async Task EnsureWrittenAsync_skips_when_hash_matches()
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, CdnBase);
        var store = new RecordingObjectStore();
        var wrote = await lib.EnsureWrittenAsync(store, existingHash: lib.LibraryHash);
        Assert.False(wrote);
        Assert.Empty(store.Puts);
    }

    private sealed class RecordingObjectStore : IObjectStore
    {
        public List<(string Key, byte[] Bytes, string ContentType, string CacheControl)> Puts { get; } = new();

        public Task PutObjectAsync(string key, ReadOnlyMemory<byte> bytes, string contentType, string cacheControl, CancellationToken cancellationToken = default)
        {
            Puts.Add((key, bytes.ToArray(), contentType, cacheControl));
            return Task.CompletedTask;
        }
    }
}
