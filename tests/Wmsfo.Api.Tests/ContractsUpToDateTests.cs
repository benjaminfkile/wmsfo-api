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
            var onDisk = File.ReadAllText(path);
            Assert.True(string.Equals(onDisk, json, StringComparison.Ordinal),
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
            var onDisk = File.ReadAllBytes(path);
            Assert.True(bytes.SequenceEqual(onDisk),
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
            var fresh = await File.ReadAllTextAsync(tmp);
            var onDisk = await File.ReadAllTextAsync(ContractsPaths.OpenApiPath);
            Assert.True(string.Equals(fresh, onDisk, StringComparison.Ordinal),
                "openapi.json is out of date; run `dotnet run --project src/Wmsfo.Api -- export-openapi contracts/openapi.json`");
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    [Fact]
    public void AdminThresholds_regenerates_byte_identically()
    {
        var fresh = AdminThresholds.ToJson();
        var onDisk = File.ReadAllText(ContractsPaths.ThresholdsPath);
        Assert.True(string.Equals(fresh, onDisk, StringComparison.Ordinal),
            "admin-thresholds.json is out of date");
    }
}
