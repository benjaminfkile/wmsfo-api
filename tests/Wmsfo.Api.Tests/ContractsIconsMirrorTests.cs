using System.Security.Cryptography;

namespace Wmsfo.Api.Tests;

// Contracts 6 (A24): contracts/icons/ is a byte-identical mirror of the repo's
// icons/ so consumers vendor the library through the contracts folder.
public class ContractsIconsMirrorTests
{
    [Fact]
    public void Contracts_icons_mirror_matches_icons_byte_for_byte()
    {
        var sourceDir = Path.Combine(ContractsPaths.RepoRoot, "icons");
        var mirrorDir = Path.Combine(ContractsPaths.ContractsDir, "icons");

        Assert.True(Directory.Exists(mirrorDir), $"contracts/icons/ does not exist: {mirrorDir}");

        var sourceEntries = Enumerate(sourceDir);
        var mirrorEntries = Enumerate(mirrorDir);

        var onlyInSource = sourceEntries.Keys.Except(mirrorEntries.Keys, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();
        var onlyInMirror = mirrorEntries.Keys.Except(sourceEntries.Keys, StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToArray();

        Assert.True(onlyInSource.Length == 0,
            "contracts/icons/ is missing files that exist in icons/: " + string.Join(", ", onlyInSource));
        Assert.True(onlyInMirror.Length == 0,
            "contracts/icons/ has files that do not exist in icons/: " + string.Join(", ", onlyInMirror));

        foreach (var key in sourceEntries.Keys)
        {
            var sourceBytes = File.ReadAllBytes(sourceEntries[key]);
            var mirrorBytes = File.ReadAllBytes(mirrorEntries[key]);
            var sourceHash = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
            var mirrorHash = Convert.ToHexStringLower(SHA256.HashData(mirrorBytes));
            Assert.True(string.Equals(sourceHash, mirrorHash, StringComparison.Ordinal),
                $"contracts/icons/{key} differs from icons/{key}");
        }
    }

    private static Dictionary<string, string> Enumerate(string root)
    {
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(
                path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'),
                path => path,
                StringComparer.Ordinal);
    }
}
