using Wmsfo.Api.Icons;

namespace Wmsfo.Api.Tests;

// api.md 11a.7 last sentence: removing an icon is a breaking change for any published
// document that uses it, so this test fails when an id disappears from library.json unless
// the change is marked deliberate in `DeliberatelyRemoved` below. To remove an id:
//   1. delete the .svg and remove it from icons/library.json
//   2. delete the line from `KnownIds` here
//   3. add the id to `DeliberatelyRemoved` in the same commit
// The commit review then makes the breaking change explicit.
public class IconLibraryRemovalGuardTests
{
    // Every id that has ever shipped in a released version of the library. Add new ids as they
    // ship; only remove one when the id also moves to DeliberatelyRemoved.
    private static readonly string[] KnownIds =
    {
        "angel",
        "bauble",
        "beanie",
        "bell",
        "bow",
        "calendar",
        "candy-cane",
        "cloud",
        "compass",
        "cookie",
        "cookie-smile",
        "cookie-star",
        "cookie-swirl",
        "drum",
        "earmuffs",
        "envelope",
        "facebook",
        "fireplace",
        "gift",
        "gift-tag",
        "gingerbread",
        "globe",
        "heart",
        "helicopter",
        "holly",
        "instagram",
        "jingle-bells",
        "lollipop",
        "map-pin",
        "mistletoe",
        "mitten",
        "moon",
        "mug",
        "north-star",
        "nutcracker",
        "ornament",
        "peppermint",
        "phone",
        "pine-branch",
        "pinecone",
        "reindeer",
        "reindeer-face",
        "ribbon",
        "santa",
        "santa-hat",
        "scarf",
        "skate",
        "skis",
        "sled",
        "sleigh",
        "snow-cloud",
        "snowboard",
        "snowflake",
        "snowglobe",
        "snowman",
        "sparkles",
        "star",
        "stocking",
        "tree",
        "wreath",
    };

    // Ids intentionally dropped from the library. Empty on day one; grows only with a
    // deliberate breaking-change commit that also updates every reference that used the id.
    private static readonly HashSet<string> DeliberatelyRemoved = new(StringComparer.Ordinal)
    {
    };

    [Fact]
    public void No_id_disappears_without_being_marked_deliberate()
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, "https://cdn.example");
        var current = new HashSet<string>(lib.Files.Select(f => f.Id), StringComparer.Ordinal);

        var missing = KnownIds
            .Where(id => !current.Contains(id) && !DeliberatelyRemoved.Contains(id))
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0,
            "icon ids disappeared from library.json without being marked deliberate: "
            + string.Join(", ", missing)
            + ". Add them to DeliberatelyRemoved in IconLibraryRemovalGuardTests if this was intentional.");
    }

    [Fact]
    public void KnownIds_stays_in_sync_with_the_current_library()
    {
        var lib = IconLibrary.Load(IconsPaths.IconsDir, "https://cdn.example");
        var current = lib.Files.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var known = KnownIds.ToHashSet(StringComparer.Ordinal);

        var added = current.Except(known).Except(DeliberatelyRemoved).OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.True(added.Count == 0,
            "new icon ids in library.json must also be listed in IconLibraryRemovalGuardTests.KnownIds: "
            + string.Join(", ", added));
    }
}
