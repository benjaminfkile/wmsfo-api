namespace Wmsfo.Api.Tests;

// The icon library lives at <repo>/icons/. ContractsPaths already walks up to the repo root
// from this source file, so reuse it.
internal static class IconsPaths
{
    public static string IconsDir => Path.Combine(ContractsPaths.RepoRoot, "icons");
    public static string LibraryJson => Path.Combine(IconsDir, "library.json");
}
