using System.Runtime.CompilerServices;

namespace Wmsfo.Api.IntegrationTests;

// Mirrors the unit test project's helper so integration tests can locate the
// repository root regardless of the working directory the runner uses.
internal static class TestPaths
{
    public static string RepoRoot { get; } = FindRepoRoot();
    public static string IconsDir => Path.Combine(RepoRoot, "icons");
    public static string ContractsDir => Path.Combine(RepoRoot, "contracts");
    public static string EmailTemplatesDir => Path.Combine(RepoRoot, "templates", "email");

    private static string FindRepoRoot([CallerFilePath] string? here = null)
    {
        var dir = Path.GetDirectoryName(here!) ?? throw new InvalidOperationException();
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Wmsfo.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("could not locate repo root from " + here);
    }
}
