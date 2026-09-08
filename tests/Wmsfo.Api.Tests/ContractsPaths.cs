using System.Runtime.CompilerServices;

namespace Wmsfo.Api.Tests;

// Locates the repo's contracts/ folder relative to this source file so tests do not
// depend on the working directory (grunt runs from the repository root, CI from
// $GITHUB_WORKSPACE, and IDEs from bin/).
internal static class ContractsPaths
{
    public static string ContractsDir => Path.Combine(RepoRoot, "contracts");
    public static string SchemaDir    => Path.Combine(ContractsDir, "schema");
    public static string FixturesDir  => Path.Combine(ContractsDir, "fixtures");
    public static string OpenApiPath  => Path.Combine(ContractsDir, "openapi.json");
    public static string ThresholdsPath => Path.Combine(ContractsDir, "admin-thresholds.json");
    public static string VersionPath  => Path.Combine(ContractsDir, "CONTRACTS_VERSION");

    public static string RepoRoot { get; } = FindRepoRoot();

    private static string FindRepoRoot([CallerFilePath] string? here = null)
    {
        var dir = Path.GetDirectoryName(here!) ?? throw new InvalidOperationException();
        // Walk up until we see the solution file.
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Wmsfo.slnx"))) return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("could not locate repo root from " + here);
    }
}
