using System.Buffers.Binary;
using Wmsfo.Api.Contracts.Dtos;

namespace Wmsfo.Api.Objects;

// api.md 11.6 Confirm: the fixed 127 byte header of a PMTiles v3 archive.
// Bytes 0 to 6 hold the magic `PMTiles`, byte 7 the version, byte 100 the min
// zoom, byte 101 the max zoom, and bytes 102 to 117 the bounds as four
// little-endian int32 values in E7 degrees (min lon, min lat, max lon, max
// lat). Check compares a header with what a map row declares and answers the
// failed check's reason (`magic`, `version`, `bounds`, `minZoom`, `maxZoom`),
// or null when the archive matches.
public sealed record PmtilesHeader(int Version, int MinZoom, int MaxZoom, Bbox Bounds)
{
    public const int Length = 127;
    public const int SupportedVersion = 3;

    private static ReadOnlySpan<byte> Magic => "PMTiles"u8;

    // The header in the first 127 bytes, or the reason it cannot be read:
    // `magic` when the bytes are short or do not start with the magic,
    // `version` when the version is not 3.
    public static PmtilesHeader? TryParse(ReadOnlySpan<byte> bytes, out string? reason)
    {
        if (bytes.Length < Length || !bytes[..Magic.Length].SequenceEqual(Magic))
        {
            reason = "magic";
            return null;
        }
        int version = bytes[7];
        if (version != SupportedVersion)
        {
            reason = "version";
            return null;
        }
        reason = null;
        return new PmtilesHeader(
            version,
            bytes[100],
            bytes[101],
            new Bbox
            {
                West = E7(bytes, 102),
                South = E7(bytes, 106),
                East = E7(bytes, 110),
                North = E7(bytes, 114),
            });
    }

    // The reason an archive does not match the row, or null. minZoom null
    // leaves the min zoom unchecked (the terrain archive).
    public static string? Check(ReadOnlySpan<byte> bytes, Bbox bbox, int? minZoom, int maxZoom)
    {
        var header = TryParse(bytes, out var reason);
        if (header is null) return reason;
        var b = header.Bounds;
        if (!(b.West <= bbox.West && b.South <= bbox.South && b.East >= bbox.East && b.North >= bbox.North))
            return "bounds";
        if (minZoom is int min && header.MinZoom != min) return "minZoom";
        if (header.MaxZoom != maxZoom) return "maxZoom";
        return null;
    }

    // A header with the given fields and every other byte zero, the start of
    // the synthetic archives the tests upload.
    public static byte[] Build(int version, int minZoom, int maxZoom, Bbox bounds)
    {
        var bytes = new byte[Length];
        Magic.CopyTo(bytes);
        bytes[7] = (byte)version;
        bytes[100] = (byte)minZoom;
        bytes[101] = (byte)maxZoom;
        WriteE7(bytes, 102, bounds.West);
        WriteE7(bytes, 106, bounds.South);
        WriteE7(bytes, 110, bounds.East);
        WriteE7(bytes, 114, bounds.North);
        return bytes;
    }

    private static double E7(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)) / 10_000_000d;

    private static void WriteE7(byte[] bytes, int offset, double degrees) =>
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), (int)Math.Round(degrees * 10_000_000d));
}
