using System.Security.Cryptography;
using System.Text;

namespace Wmsfo.Api.Tests;

// Contracts 13: `CONTRACTS_VERSION` is an integer bumped on every change under `contracts/`.
// The map below records the version-to-hash pairs; bump the integer and add an entry when the
// hash changes. The test fails when the directory hash changes and the version does not.
public class ContractsVersionTests
{
    // Add a new entry every time you bump CONTRACTS_VERSION.
    private static readonly IReadOnlyDictionary<int, string> KnownHashes = new Dictionary<int, string>
    {
        { 1, "b0886551ff8f90ea1ff7389a5b6b6b4d87033d51bbdff6647abc82acbd730ae2" },
        { 2, "22bf1982b10c9298e1775beef60db2168f18d11fa2ded7a264aaf1383e4341ce" },
        { 3, "dfd2bb18bc84d70aa6f71cad7d189bfe70aa8e54bf7d7e6c0f2c1dac9f024c2b" },
        { 4, "4670e8cebc4b85437ce184b19f277ad320890ae5c01b7480a1e05f29b8fc509a" },
        { 5, "5bbe5defbbb6b1c42bab831d6ed516d89e78c57ccee551ea9a237a263536fa62" },
        { 6, "8f73adf31084954549ea4010f34201ff725bd9b218346280c198c575e0efc18e" },
        { 7, "464168042a138edea4f3113e5e17d85f62de39dfca290b46d042c4ddee084744" },
        { 8, "fa3b812f683c37452d56f9f4c1dc03104040e8939e8df2e4d7c2c35fbe3dca0a" },
    };

    [Fact]
    public void Contracts_version_matches_directory_hash()
    {
        var version = int.Parse(File.ReadAllText(ContractsPaths.VersionPath).Trim());
        var currentHash = HashDirectory(ContractsPaths.ContractsDir);

        Assert.True(KnownHashes.ContainsKey(version),
            $"CONTRACTS_VERSION={version} has no recorded hash; add it to KnownHashes in ContractsVersionTests. Current hash: {currentHash}");

        var expected = KnownHashes[version];
        Assert.True(string.Equals(expected, currentHash, StringComparison.Ordinal),
            $"contracts/ contents changed (current hash {currentHash}) but CONTRACTS_VERSION is still {version}. " +
            $"Bump CONTRACTS_VERSION to {version + 1} and add the new entry {{ {version + 1}, \"{currentHash}\" }} in ContractsVersionTests.KnownHashes.");
    }

    // A deterministic hash of every file under contracts/ except CONTRACTS_VERSION itself.
    // Byte contents plus the repo-relative POSIX path go into the hash, so a rename or a
    // content edit both count as a change. Bytes are normalized from CRLF to LF first so a
    // checkout under `core.autocrlf=true` (Windows) produces the same hash as a plain LF one.
    private static string HashDirectory(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFileName(f), "CONTRACTS_VERSION", StringComparison.Ordinal))
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + ".gitkeep", StringComparison.Ordinal)
                        && !f.EndsWith(".gitkeep", StringComparison.Ordinal))
            .Select(f => (Path: f, Rel: Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/')))
            .OrderBy(p => p.Rel, StringComparer.Ordinal)
            .ToList();

        using var sha = SHA256.Create();
        foreach (var (path, rel) in files)
        {
            var pathBytes = Encoding.UTF8.GetBytes(rel + "\n");
            sha.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
            var content = NormalizeNewlineBytes(File.ReadAllBytes(path));
            sha.TransformBlock(content, 0, content.Length, null, 0);
            sha.TransformBlock(new byte[] { 0 }, 0, 1, null, 0);
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    private static byte[] NormalizeNewlineBytes(byte[] bytes)
    {
        var output = new List<byte>(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (bytes[i] == 0x0D && i + 1 < bytes.Length && bytes[i + 1] == 0x0A)
            {
                continue;
            }
            output.Add(bytes[i]);
        }
        return output.ToArray();
    }
}
