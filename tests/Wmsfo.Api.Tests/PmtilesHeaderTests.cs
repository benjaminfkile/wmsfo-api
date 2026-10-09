using Wmsfo.Api.Contracts.Dtos;
using Wmsfo.Api.Endpoints;
using Wmsfo.Api.Objects;
using Wmsfo.Api.Themes;

namespace Wmsfo.Api.Tests;

// api.md 11.6 Confirm: the 127 byte PMTiles v3 header check on synthetic
// headers, and the package key rule of contracts 1.1.
public sealed class PmtilesHeaderTests
{
    private static readonly Bbox Box = new() { West = -114.75, South = 46.35, East = -113.30, North = 47.25 };
    private static readonly Bbox Wider = new() { West = -115.0, South = 46.0, East = -113.0, North = 47.5 };

    [Fact]
    public void A_good_header_parses_the_zooms_and_bounds_and_passes_the_check()
    {
        var bytes = PmtilesHeader.Build(3, 0, 15, Box);
        Assert.Equal(127, bytes.Length);
        var header = PmtilesHeader.TryParse(bytes, out var reason);
        Assert.Null(reason);
        Assert.NotNull(header);
        Assert.Equal(3, header!.Version);
        Assert.Equal(0, header.MinZoom);
        Assert.Equal(15, header.MaxZoom);
        Assert.Equal(-114.75, header.Bounds.West);
        Assert.Equal(46.35, header.Bounds.South);
        Assert.Equal(-113.30, header.Bounds.East);
        Assert.Equal(47.25, header.Bounds.North);
        Assert.Null(PmtilesHeader.Check(bytes, Box, 0, 15));
        Assert.Null(PmtilesHeader.Check(PmtilesHeader.Build(3, 0, 15, Wider), Box, 0, 15));
    }

    [Fact]
    public void The_bytes_sit_at_the_documented_offsets()
    {
        var bytes = PmtilesHeader.Build(3, 2, 14, Box);
        Assert.Equal("PMTiles"u8.ToArray(), bytes[..7]);
        Assert.Equal(3, bytes[7]);
        Assert.Equal(2, bytes[100]);
        Assert.Equal(14, bytes[101]);
        Assert.Equal(-1_147_500_000, BitConverter.ToInt32(bytes, 102));
        Assert.Equal(463_500_000, BitConverter.ToInt32(bytes, 106));
        Assert.Equal(-1_133_000_000, BitConverter.ToInt32(bytes, 110));
        Assert.Equal(472_500_000, BitConverter.ToInt32(bytes, 114));
    }

    [Fact]
    public void A_bad_magic_a_short_read_and_version_2_are_refused()
    {
        var bad = PmtilesHeader.Build(3, 0, 15, Box);
        bad[0] = (byte)'X';
        Assert.Equal("magic", PmtilesHeader.Check(bad, Box, 0, 15));
        Assert.Equal("magic", PmtilesHeader.Check(PmtilesHeader.Build(3, 0, 15, Box)[..100], Box, 0, 15));
        Assert.Equal("version", PmtilesHeader.Check(PmtilesHeader.Build(2, 0, 15, Box), Box, 0, 15));
    }

    [Fact]
    public void Bounds_not_containing_the_box_and_zoom_mismatches_yield_their_reasons()
    {
        var narrow = new Bbox { West = -114.70, South = 46.35, East = -113.30, North = 47.25 };
        Assert.Equal("bounds", PmtilesHeader.Check(PmtilesHeader.Build(3, 0, 15, narrow), Box, 0, 15));
        Assert.Equal("minZoom", PmtilesHeader.Check(PmtilesHeader.Build(3, 1, 15, Box), Box, 0, 15));
        Assert.Equal("maxZoom", PmtilesHeader.Check(PmtilesHeader.Build(3, 0, 14, Box), Box, 0, 15));
        // The terrain archive's min zoom is not checked.
        Assert.Null(PmtilesHeader.Check(PmtilesHeader.Build(3, 5, 13, Box), Box, null, 13));
        Assert.Equal("maxZoom", PmtilesHeader.Check(PmtilesHeader.Build(3, 0, 12, Box), Box, null, 13));
    }

    [Fact]
    public void The_valley_package_key_from_the_valley_constants_is_the_seeded_key()
    {
        var bbox = new Bbox
        {
            West = TrackerThemeSeed.ValleyWest,
            South = TrackerThemeSeed.ValleySouth,
            East = TrackerThemeSeed.ValleyEast,
            North = TrackerThemeSeed.ValleyNorth,
        };
        Assert.Equal(TrackerThemeSeed.ValleyPackageKey, AdminMapEndpoints.PackageKey(
            bbox, TrackerThemeSeed.ValleyMinZoom, TrackerThemeSeed.ValleyMaxZoom, TrackerThemeSeed.ValleyTerrainMaxZoom));
        Assert.NotEqual(TrackerThemeSeed.ValleyPackageKey, AdminMapEndpoints.PackageKey(
            bbox, TrackerThemeSeed.ValleyMinZoom, TrackerThemeSeed.ValleyMaxZoom, null));
    }
}
