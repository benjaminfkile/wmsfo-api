namespace Wmsfo.Api.Tests;

// A20 acceptance criterion 840: no em dash (U+2014) or en dash (U+2013) is allowed
// under src/ or tests/. Replacements are a plain hyphen, a comma, a colon, or a
// rewritten sentence; ranges become "to" (5 to 10).
public class DashCharacterScanTests
{
    private const char EmDash = '\u2014';
    private const char EnDash = '\u2013';

    [Fact]
    public void Src_and_tests_contain_no_em_or_en_dashes()
    {
        var roots = new[]
        {
            Path.Combine(ContractsPaths.RepoRoot, "src"),
            Path.Combine(ContractsPaths.RepoRoot, "tests"),
        };

        var hits = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in EnumerateSourceFiles(root))
            {
                var text = File.ReadAllText(file);
                var line = 1;
                for (var i = 0; i < text.Length; i++)
                {
                    var c = text[i];
                    if (c == '\n') { line++; continue; }
                    if (c == EmDash || c == EnDash)
                    {
                        var name = c == EmDash ? "em dash" : "en dash";
                        hits.Add($"{Path.GetRelativePath(ContractsPaths.RepoRoot, file)}:{line}: {name} (U+{(int)c:X4})");
                    }
                }
            }
        }

        Assert.True(hits.Count == 0,
            "em/en dashes must be replaced with a plain hyphen, comma, colon, or rewritten sentence "
            + "(ranges become \"to\"). Offenders:\n  "
            + string.Join("\n  ", hits));
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.StartsWith("bin/", StringComparison.Ordinal)) continue;
            if (rel.Contains("/obj/", StringComparison.Ordinal) || rel.StartsWith("obj/", StringComparison.Ordinal)) continue;
            yield return file;
        }
    }
}
