using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Contracts;

// Emits the canonical fixtures (contracts 13) through CanonicalJson so each file is
// byte-for-byte what the API would write for the same data.
public static class FixtureExport
{
    public static IEnumerable<(string Name, byte[] Bytes)> BuildAll()
    {
        yield return ("live-object",      CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildLiveObject()));
        yield return ("snapshot",         CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildSnapshot()));
        yield return ("route",            CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildRoute()));
        yield return ("location",         CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildLocation()));
        yield return ("heartbeat",        CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildHeartbeat()));
        yield return ("content-document", CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildContentDocument()));
        yield return ("qr-code",          CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildQrCode()));
        yield return ("qr-code-detail",   CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildQrCodeDetail()));
        yield return ("place",            CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildPlace()));
        yield return ("place-pin",        CanonicalJson.SerializeToUtf8Bytes(FixtureData.BuildPlacePin()));
    }

    public static void WriteAll(string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        foreach (var (name, bytes) in BuildAll())
        {
            File.WriteAllBytes(Path.Combine(outputDir, name + ".json"), bytes);
        }
    }
}
