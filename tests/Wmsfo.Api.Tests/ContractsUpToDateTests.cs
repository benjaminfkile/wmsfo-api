using Wmsfo.Api.Contracts;

namespace Wmsfo.Api.Tests;

// Acceptance criterion 685: re-running the export produces no diff. Regenerating the
// artifacts and byte-comparing them to what is checked in is the same check the CI step
// `git diff --exit-code contracts/` runs, minus the diff step.
public class ContractsUpToDateTests
{
    [Fact]
    public void Every_schema_regenerates_byte_identically()
    {
        foreach (var (name, json) in SchemaExport.BuildAll())
        {
            var path = Path.Combine(ContractsPaths.SchemaDir, name + ".schema.json");
            Assert.True(File.Exists(path), $"missing schema: {path}");
            var onDisk = NormalizeNewlines(File.ReadAllText(path));
            var fresh = NormalizeNewlines(json);
            Assert.True(string.Equals(onDisk, fresh, StringComparison.Ordinal),
                $"schema {name} is out of date; run `dotnet run --project src/Wmsfo.Api -- export-contracts contracts`");
        }
    }

    [Fact]
    public void Every_fixture_regenerates_byte_identically()
    {
        foreach (var (name, bytes) in FixtureExport.BuildAll())
        {
            var path = Path.Combine(ContractsPaths.FixturesDir, name + ".json");
            Assert.True(File.Exists(path), $"missing fixture: {path}");
            var onDisk = NormalizeNewlineBytes(File.ReadAllBytes(path));
            var fresh = NormalizeNewlineBytes(bytes);
            Assert.True(fresh.SequenceEqual(onDisk),
                $"fixture {name}.json is out of date; run `dotnet run --project src/Wmsfo.Api -- export-contracts contracts`");
        }
    }

    [Fact]
    public async Task Openapi_regenerates_byte_identically()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"openapi-{Guid.NewGuid():N}.json");
        try
        {
            await OpenApiExport.WriteAsync(tmp);
            var fresh = NormalizeNewlines(await File.ReadAllTextAsync(tmp));
            var onDisk = NormalizeNewlines(await File.ReadAllTextAsync(ContractsPaths.OpenApiPath));
            Assert.True(string.Equals(fresh, onDisk, StringComparison.Ordinal),
                "openapi.json is out of date; run `dotnet run --project src/Wmsfo.Api -- export-contracts contracts`");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void AdminThresholds_regenerates_byte_identically()
    {
        var fresh = NormalizeNewlines(AdminThresholds.ToJson());
        var onDisk = NormalizeNewlines(File.ReadAllText(ContractsPaths.ThresholdsPath));
        Assert.True(string.Equals(fresh, onDisk, StringComparison.Ordinal),
            "admin-thresholds.json is out of date; run `dotnet run --project src/Wmsfo.Api -- export-contracts contracts`");
    }

    // A Windows checkout with `core.autocrlf=true` rewrites LF to CRLF on the working copy.
    // The exporter writes LF and `.gitattributes` pins the checked-in files to LF, but the
    // comparison must tolerate CRLF so the tests still pass in that environment.
    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n");

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
