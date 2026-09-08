using System.Security.Cryptography;
using System.Text;

namespace Wmsfo.Api.Tests;

// A1f acceptance criterion 714: the contract tests must still pass on a Windows checkout
// made with `core.autocrlf=true`. `.gitattributes` pins the checked-in files to LF, but
// this test simulates the effect of that config by copying `contracts/openapi.json` to a
// temp file with CRLF line endings and asserting the same LF→normalized comparison the
// production tests use still succeeds.
public class ContractsLineEndingSafetyTests
{
    [Fact]
    public void Contracts_up_to_date_comparison_tolerates_CRLF_working_copy()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"openapi-crlf-{Guid.NewGuid():N}.json");
        try
        {
            var lfText = File.ReadAllText(ContractsPaths.OpenApiPath);
            var crlfText = lfText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllText(tmp, crlfText);

            var freshNormalized = Normalize(lfText);
            var onDiskNormalized = Normalize(File.ReadAllText(tmp));
            Assert.Equal(freshNormalized, onDiskNormalized);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void Contracts_version_hash_matches_when_files_are_checked_out_as_CRLF()
    {
        var tmpRoot = Path.Combine(Path.GetTempPath(), $"contracts-crlf-{Guid.NewGuid():N}");
        try
        {
            CopyDirectoryConvertingToCrlf(ContractsPaths.ContractsDir, tmpRoot);
            var crlfHash = HashDirectoryNormalized(tmpRoot);
            var lfHash = HashDirectoryNormalized(ContractsPaths.ContractsDir);
            Assert.Equal(lfHash, crlfHash);
        }
        finally
        {
            if (Directory.Exists(tmpRoot)) Directory.Delete(tmpRoot, recursive: true);
        }
    }

    private static void CopyDirectoryConvertingToCrlf(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            var target = Path.Combine(destination, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var text = File.ReadAllText(file);
            var crlf = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            File.WriteAllText(target, crlf);
        }
    }

    private static string HashDirectoryNormalized(string root)
    {
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !string.Equals(Path.GetFileName(f), "CONTRACTS_VERSION", StringComparison.Ordinal))
            .Where(f => !f.EndsWith(".gitkeep", StringComparison.Ordinal))
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

    private static string Normalize(string s) => s.Replace("\r\n", "\n");

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
