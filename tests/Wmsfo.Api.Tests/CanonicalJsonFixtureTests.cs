using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// Acceptance criterion 686: bytes emitted by the canonical serializer match the checked-in
// fixtures. Also covers criterion 685 (the fixtures exist).
public class CanonicalJsonFixtureTests
{
    public static IEnumerable<object[]> Fixtures()
    {
        foreach (var (name, bytes) in FixtureExport.BuildAll())
            yield return new object[] { name, bytes };
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Fixture_bytes_are_byte_equal_to_serializer_output(string name, byte[] canonical)
    {
        var path = Path.Combine(ContractsPaths.FixturesDir, name + ".json");
        Assert.True(File.Exists(path), $"missing fixture: {path}");
        var onDisk = File.ReadAllBytes(path);
        Assert.Equal(canonical, onDisk);
    }

    [Fact]
    public void Live_object_fixture_matches_canonical_bytes()
    {
        var live = FixtureData.BuildLiveObject();
        var bytes = CanonicalJson.SerializeToUtf8Bytes(live);
        var onDisk = File.ReadAllBytes(Path.Combine(ContractsPaths.FixturesDir, "live-object.json"));
        Assert.Equal(bytes, onDisk);
    }

    [Fact]
    public void Sha256Hex_matches_known_vector()
    {
        // Empty input SHA-256, well-known: e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            CanonicalJson.Sha256Hex(Array.Empty<byte>()));
    }
}
