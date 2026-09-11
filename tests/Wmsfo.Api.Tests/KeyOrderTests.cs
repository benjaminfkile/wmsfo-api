using System.Text.Json;
using Wmsfo.Api.Contracts;
using Wmsfo.Api.Objects;

namespace Wmsfo.Api.Tests;

// Acceptance criterion 687: every DTO property order matches the key order documented in
// contracts 1.2, 1.3, 1.4 (a test asserts the fixture key order).
public class KeyOrderTests
{
    [Fact]
    public void Live_object_fixture_keys_match_contracts_1_2_order()
    {
        var expected = new[]
        {
            "schemaVersion", "eventId", "eventStatusId", "pollIntervalMs", "snapshotUrl",
            "cookieTally", "seq", "lat", "lng", "speedMps", "altitudeM", "headingDeg", "accuracyM",
            "recordedAt", "receivedAt", "publishedAt",
        };
        var actual = ReadTopLevelKeys(Path.Combine(ContractsPaths.FixturesDir, "live-object.json"));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Snapshot_fixture_keys_match_contracts_1_3_order()
    {
        var expected = new[]
        {
            "schemaVersion", "event", "sponsors", "cookieTypes", "content", "media", "icons",
        };
        var actual = ReadTopLevelKeys(Path.Combine(ContractsPaths.FixturesDir, "snapshot.json"));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Snapshot_event_keys_match_contracts_1_3_order()
    {
        var expectedEvent = new[]
        {
            "id", "year", "name", "statusId", "scheduledAt", "wentLiveAt", "endedAt",
            "fundsPercent", "routeImageMediaId", "flightHistory", "latestMessage",
        };
        var expectedFlightHistory = new[] { "routeId", "name", "points" };
        var expectedFlightPoint = new[] { "lat", "lng", "recordedAt" };
        var expectedLatestMessage = new[] { "id", "body", "eventTime", "createdAt" };
        var expectedSponsor = new[]
        {
            "id", "name", "websiteUrl", "fbUrl", "igUrl", "logoMediaId",
            "latestYear", "yearsAsSponsor", "lingerMs",
        };
        var expectedCookieType = new[] { "id", "name", "icon", "sort" };

        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContractsPaths.FixturesDir, "snapshot.json")));
        var root = doc.RootElement;
        Assert.Equal(expectedEvent, KeysOf(root.GetProperty("event")));
        Assert.Equal(expectedFlightHistory, KeysOf(root.GetProperty("event").GetProperty("flightHistory")));
        Assert.Equal(expectedFlightPoint, KeysOf(root.GetProperty("event").GetProperty("flightHistory").GetProperty("points")[0]));
        Assert.Equal(expectedLatestMessage, KeysOf(root.GetProperty("event").GetProperty("latestMessage")));
        Assert.Equal(expectedSponsor, KeysOf(root.GetProperty("sponsors")[0]));
        Assert.Equal(expectedCookieType, KeysOf(root.GetProperty("cookieTypes")[0]));
    }

    [Fact]
    public void Route_fixture_keys_match_contracts_1_4_order()
    {
        var expected = new[] { "schemaVersion", "name", "points" };
        var expectedPoint = new[] { "lat", "lng", "recordedAt" };
        var actual = ReadTopLevelKeys(Path.Combine(ContractsPaths.FixturesDir, "route.json"));
        Assert.Equal(expected, actual);

        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(ContractsPaths.FixturesDir, "route.json")));
        Assert.Equal(expectedPoint, KeysOf(doc.RootElement.GetProperty("points")[0]));
    }

    [Fact]
    public void CookieTally_keys_are_emitted_in_ascending_numeric_order()
    {
        var live = new LiveObject
        {
            SchemaVersion = 1,
            PollIntervalMs = 5000,
            SnapshotUrl = "https://cdn.example/snapshots/0.json",
            CookieTally = new SortedDictionary<long, int> { { 20, 1 }, { 3, 2 }, { 100, 3 } },
            PublishedAt = DateTimeOffset.Parse("2026-01-01T00:00:00.000Z"),
        };
        var bytes = CanonicalJson.SerializeToUtf8Bytes(live);
        using var doc = JsonDocument.Parse(bytes);
        var keys = KeysOf(doc.RootElement.GetProperty("cookieTally"));
        Assert.Equal(new[] { "3", "20", "100" }, keys);
    }

    [Fact]
    public void Snapshot_media_keys_are_emitted_in_ascending_string_order()
    {
        var snap = FixtureData.BuildSnapshot();
        snap.Media["aa"] = new MediaEntry { Url = "https://cdn.example/a", Kind = "svg", Width = null, Height = null, Alt = "" };
        snap.Media["cc"] = new MediaEntry { Url = "https://cdn.example/c", Kind = "svg", Width = null, Height = null, Alt = "" };
        snap.Media["bb"] = new MediaEntry { Url = "https://cdn.example/b", Kind = "svg", Width = null, Height = null, Alt = "" };
        var bytes = CanonicalJson.SerializeToUtf8Bytes(snap);
        using var doc = JsonDocument.Parse(bytes);
        var mediaKeys = KeysOf(doc.RootElement.GetProperty("media"));
        // The two seeded UUIDs plus the three we added, in ordinal string order.
        Assert.Equal(
            new[] { "5f2a7c9e-1b4d-4e8a-9c3f-7d6e2a1b0c44", "8c1d5e2a-7b3f-4c9e-9a1d-2f6e8b4c0a11", "aa", "bb", "cc" },
            mediaKeys);
    }

    private static string[] ReadTopLevelKeys(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        return KeysOf(doc.RootElement);
    }

    private static string[] KeysOf(JsonElement e)
    {
        var list = new List<string>();
        foreach (var p in e.EnumerateObject()) list.Add(p.Name);
        return list.ToArray();
    }
}
