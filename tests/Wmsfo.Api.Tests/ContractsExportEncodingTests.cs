using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// A1g acceptance criterion 719: every writer used by `export-contracts` must produce
// bytes with no carriage return and no UTF-8 BOM, otherwise a Windows checkout of
// this repo would rewrite the LF-pinned `contracts/` files with CRLF (or a BOM) and
// `git diff --exit-code contracts/` would fail there while Linux CI stays clean.
public class ContractsExportEncodingTests
{
    [Fact]
    public void SchemaExport_writes_no_cr_and_no_bom()
    {
        var dir = MakeTempDir();
        try
        {
            SchemaExport.WriteAll(dir);
            var files = Directory.EnumerateFiles(dir, "*.schema.json").ToList();
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                AssertNoCrAndNoBom(File.ReadAllBytes(file), file);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FixtureExport_writes_no_cr_and_no_bom()
    {
        var dir = MakeTempDir();
        try
        {
            FixtureExport.WriteAll(dir);
            var files = Directory.EnumerateFiles(dir, "*.json").ToList();
            Assert.NotEmpty(files);
            foreach (var file in files)
            {
                AssertNoCrAndNoBom(File.ReadAllBytes(file), file);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AdminThresholds_writes_no_cr_and_no_bom()
    {
        var dir = MakeTempDir();
        try
        {
            var path = Path.Combine(dir, "admin-thresholds.json");
            AdminThresholds.WriteTo(path);
            AssertNoCrAndNoBom(File.ReadAllBytes(path), path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void StarterContent_writes_no_cr_and_no_bom()
    {
        var dir = MakeTempDir();
        try
        {
            var path = Path.Combine(dir, "starter-content.json");
            File.WriteAllBytes(path, CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildContentDocument()));
            AssertNoCrAndNoBom(File.ReadAllBytes(path), path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task OpenApiExport_writes_no_cr_and_no_bom()
    {
        var dir = MakeTempDir();
        try
        {
            var path = Path.Combine(dir, "openapi.json");
            await OpenApiExport.WriteAsync(path);
            AssertNoCrAndNoBom(File.ReadAllBytes(path), path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wmsfo-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void AssertNoCrAndNoBom(byte[] bytes, string path)
    {
        Assert.False(HasUtf8Bom(bytes), $"{path} starts with a UTF-8 BOM");
        var crIndex = Array.IndexOf(bytes, (byte)0x0D);
        Assert.True(crIndex < 0, $"{path} contains a carriage return at byte offset {crIndex}");
    }

    private static bool HasUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
}
