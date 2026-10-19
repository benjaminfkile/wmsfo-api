using System.Buffers.Binary;

namespace Wmsfo.Api.Media;

// api.md 11.3, contracts 4.5 Media: reads an MP4's frame size and duration
// through ranged reads, never the whole object. The first 16 bytes must carry
// `ftyp` at bytes 4 to 8 (any brand). The top-level boxes are then walked by
// reading each header (32-bit size and type; the 64-bit largesize when size is
// 1; size 0 runs to the end of the object) and skipping each box by its size
// until `moov`, which is read whole (at most MoovCap bytes). Inside `moov`,
// `mvhd` (versions 0 and 1) gives the timescale and duration, and the first
// `trak` whose `tkhd` has a non-zero width and height (16.16 fixed; the matrix
// is ignored) is the video track.
public static class Mp4Inspector
{
    public const string ContentType = "video/mp4";
    public const long MoovCap = 16L * 1024 * 1024;

    // details.reason values of a refused upload.
    public const string ReasonMoov = "moov";
    public const string ReasonTrack = "track";
    public const string ReasonDuration = "duration";

    // Reads bytes `from` to `to` inclusive (fewer when the object is shorter).
    public delegate Task<byte[]> RangeReader(long from, long to, CancellationToken ct);

    public sealed record Mp4Info(int Width, int Height, int DurationMs);

    // The first 16 bytes carry `ftyp` at bytes 4 to 8.
    public static bool HasFtyp(ReadOnlySpan<byte> head) =>
        head.Length >= 8 && head[4] == (byte)'f' && head[5] == (byte)'t' && head[6] == (byte)'y' && head[7] == (byte)'p';

    // Walks the top-level boxes of an object of `length` bytes whose first
    // bytes are `head`, reads `moov`, and answers the frame size and duration.
    // Throws Mp4Exception with the reason when there is no `moov`, no video
    // track, or no usable duration.
    public static async Task<Mp4Info> InspectAsync(
        byte[] head, long length, RangeReader read, CancellationToken ct)
    {
        long offset = 0;
        while (offset + 8 <= length)
        {
            byte[] header = offset == 0 && head.Length >= 16
                ? head
                : await read(offset, Math.Min(offset + 15, length - 1), ct).ConfigureAwait(false);
            if (header.Length < 8)
                throw new Mp4Exception(ReasonMoov, "the box headers end before a moov box");
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            var type = BoxType(header.AsSpan(4, 4));
            var headerLength = 8;
            if (size == 1)
            {
                if (header.Length < 16)
                    throw new Mp4Exception(ReasonMoov, "a 64-bit box size is cut short");
                var large = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                if (large > long.MaxValue)
                    throw new Mp4Exception(ReasonMoov, "a box size is out of range");
                size = (long)large;
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = length - offset;
            }
            if (size < headerLength)
                throw new Mp4Exception(ReasonMoov, "a box size is smaller than its header");

            if (type == "moov")
            {
                if (size > MoovCap)
                    throw new Mp4Exception(ReasonMoov, $"the moov box is larger than {MoovCap} bytes");
                if (offset + size > length)
                    throw new Mp4Exception(ReasonMoov, "the moov box runs past the end of the file");
                var moov = await read(offset, offset + size - 1, ct).ConfigureAwait(false);
                if (moov.LongLength != size)
                    throw new Mp4Exception(ReasonMoov, "the moov box could not be read whole");
                return ParseMoov(moov.AsSpan(headerLength));
            }
            if (size > length - offset) break;
            offset += size;
        }
        throw new Mp4Exception(ReasonMoov, "no moov box");
    }

    // The children of `moov`: `mvhd` for the duration, each `trak` for its
    // `tkhd` frame size.
    public static Mp4Info ParseMoov(ReadOnlySpan<byte> payload)
    {
        ulong timescale = 0;
        ulong duration = 0;
        var haveMvhd = false;
        int? width = null, height = null;
        foreach (var (type, body) in Children(payload))
        {
            if (type == "mvhd" && !haveMvhd)
            {
                haveMvhd = ReadMvhd(payload.Slice(body.Start, body.Length), out timescale, out duration);
            }
            else if (type == "trak" && width is null)
            {
                var trak = payload.Slice(body.Start, body.Length);
                foreach (var (childType, childBody) in Children(trak))
                {
                    if (childType != "tkhd") continue;
                    if (ReadTkhd(trak.Slice(childBody.Start, childBody.Length), out var w, out var h) && w > 0 && h > 0)
                    {
                        width = w;
                        height = h;
                    }
                    break;
                }
            }
        }
        if (width is null || height is null)
            throw new Mp4Exception(ReasonTrack, "no video track");
        if (!haveMvhd || timescale == 0 || duration == 0)
            throw new Mp4Exception(ReasonDuration, "the duration is zero");
        var ms = (UInt128)duration * 1000 / timescale;
        if (ms == 0 || ms > int.MaxValue)
            throw new Mp4Exception(ReasonDuration, "the duration is zero or out of range");
        return new Mp4Info(width.Value, height.Value, (int)ms);
    }

    // mvhd: version 0 holds 32-bit creation, modification, timescale, and
    // duration; version 1 holds 64-bit times, a 32-bit timescale, and a
    // 64-bit duration.
    private static bool ReadMvhd(ReadOnlySpan<byte> box, out ulong timescale, out ulong duration)
    {
        timescale = 0;
        duration = 0;
        if (box.Length < 4) return false;
        var version = box[0];
        if (version == 1)
        {
            if (box.Length < 4 + 8 + 8 + 4 + 8) return false;
            timescale = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(20, 4));
            duration = BinaryPrimitives.ReadUInt64BigEndian(box.Slice(24, 8));
            return true;
        }
        if (box.Length < 4 + 4 + 4 + 4 + 4) return false;
        timescale = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(12, 4));
        duration = BinaryPrimitives.ReadUInt32BigEndian(box.Slice(16, 4));
        return true;
    }

    // tkhd: after the version and flags, version 0 holds 20 bytes of times,
    // id, and duration, version 1 holds 32; then 52 bytes of reserved, layer,
    // alternate group, volume, and matrix; then width and height in 16.16.
    private static bool ReadTkhd(ReadOnlySpan<byte> box, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (box.Length < 4) return false;
        var at = box[0] == 1 ? 4 + 32 + 52 : 4 + 20 + 52;
        if (box.Length < at + 8) return false;
        width = (int)(BinaryPrimitives.ReadUInt32BigEndian(box.Slice(at, 4)) >> 16);
        height = (int)(BinaryPrimitives.ReadUInt32BigEndian(box.Slice(at + 4, 4)) >> 16);
        return true;
    }

    // The child boxes of a container payload: type and the body's start and
    // length within the payload. Sizes follow the same rules as the top
    // level; a box that runs past the payload ends the walk.
    private static List<(string Type, (int Start, int Length) Body)> Children(ReadOnlySpan<byte> payload)
    {
        var boxes = new List<(string, (int, int))>();
        var offset = 0;
        while (offset + 8 <= payload.Length)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(offset, 4));
            var type = BoxType(payload.Slice(offset + 4, 4));
            var headerLength = 8;
            if (size == 1)
            {
                if (offset + 16 > payload.Length) break;
                var large = BinaryPrimitives.ReadUInt64BigEndian(payload.Slice(offset + 8, 8));
                if (large > (ulong)(payload.Length - offset)) break;
                size = (long)large;
                headerLength = 16;
            }
            else if (size == 0)
            {
                size = payload.Length - offset;
            }
            if (size < headerLength || size > payload.Length - offset) break;
            boxes.Add((type, (offset + headerLength, (int)size - headerLength)));
            offset += (int)size;
        }
        return boxes;
    }

    private static string BoxType(ReadOnlySpan<byte> bytes) =>
        string.Create(4, bytes.ToArray(), static (span, b) =>
        {
            for (var i = 0; i < 4; i++) span[i] = (char)b[i];
        });
}

// A refused MP4: `Reason` is the details.reason of the 400 (moov, track, or
// duration).
public sealed class Mp4Exception(string reason, string message) : Exception(message)
{
    public string Reason { get; } = reason;
}
