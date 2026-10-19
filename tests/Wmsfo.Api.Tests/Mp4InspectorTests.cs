using System.Buffers.Binary;
using System.Text;
using Wmsfo.Api.Media;

namespace Wmsfo.Api.Tests;

// api.md 11.3, the video confirm: Mp4Inspector over hand-built box streams.
// The streams live in a sparse virtual file so a multi-gigabyte mdat costs
// nothing, and every ranged read is counted, so a test can see that only the
// box headers and moov were read.
public sealed class Mp4InspectorTests
{
    [Fact]
    public async Task Moov_first_reads_the_frame_size_and_duration()
    {
        var file = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .Append(Mp4Boxes.Moov(Mp4Boxes.Mvhd(0, 600, 25_500), Mp4Boxes.Trak(Mp4Boxes.Tkhd(0, 1280, 720))))
            .Append(Mp4Boxes.Box("mdat", new byte[4096]));

        var info = await file.InspectAsync();

        Assert.Equal(1280, info.Width);
        Assert.Equal(720, info.Height);
        Assert.Equal(42_500, info.DurationMs);
    }

    [Fact]
    public async Task Moov_last_after_a_large_mdat_skips_the_mdat_by_its_size()
    {
        const long mdatSize = 300L * 1024 * 1024;
        var file = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .AppendHeaderOnly("mdat", mdatSize)
            .Append(Mp4Boxes.Moov(Mp4Boxes.Mvhd(0, 1000, 61_000), Mp4Boxes.Trak(Mp4Boxes.Tkhd(0, 1920, 1080))));

        var info = await file.InspectAsync();

        Assert.Equal(1920, info.Width);
        Assert.Equal(1080, info.Height);
        Assert.Equal(61_000, info.DurationMs);
        // The 16 head bytes, one header read for the mdat and one for moov,
        // and moov itself: nothing of the mdat body.
        Assert.True(file.BytesRead < 1024, $"read {file.BytesRead} bytes");
        Assert.DoesNotContain(file.Reads, r => r.From > 64 && r.From < 32 + mdatSize);
    }

    [Fact]
    public async Task A_64_bit_box_size_is_honoured()
    {
        const long mdatSize = 5L * 1024 * 1024 * 1024;
        var file = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .AppendLargeHeaderOnly("mdat", mdatSize)
            .Append(Mp4Boxes.LargeBox("moov", Mp4Boxes.Concat(
                Mp4Boxes.Mvhd(1, 90_000, 90_000L * 3600 * 2),
                Mp4Boxes.Trak(Mp4Boxes.Tkhd(1, 3840, 2160)))));

        var info = await file.InspectAsync();

        Assert.Equal(3840, info.Width);
        Assert.Equal(2160, info.Height);
        Assert.Equal(7_200_000, info.DurationMs);
        Assert.True(file.BytesRead < 1024, $"read {file.BytesRead} bytes");
    }

    [Fact]
    public async Task The_first_track_with_a_frame_size_is_the_video_track()
    {
        var file = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .Append(Mp4Boxes.Moov(
                Mp4Boxes.Mvhd(0, 1000, 5_000),
                Mp4Boxes.Trak(Mp4Boxes.Tkhd(0, 0, 0)),
                Mp4Boxes.Trak(Mp4Boxes.Tkhd(0, 640, 360)),
                Mp4Boxes.Trak(Mp4Boxes.Tkhd(0, 320, 180))));

        var info = await file.InspectAsync();

        Assert.Equal(640, info.Width);
        Assert.Equal(360, info.Height);
    }

    [Fact]
    public async Task No_video_track_is_refused_with_reason_track()
    {
        var file = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .Append(Mp4Boxes.Moov(Mp4Boxes.Mvhd(0, 1000, 5_000), Mp4Boxes.Trak(Mp4Boxes.Tkhd(0, 0, 0))))
            .Append(Mp4Boxes.Box("mdat", new byte[64]));

        var ex = await Assert.ThrowsAsync<Mp4Exception>(file.InspectAsync);
        Assert.Equal(Mp4Inspector.ReasonTrack, ex.Reason);
    }

    [Fact]
    public async Task No_moov_is_refused_with_reason_moov()
    {
        var file = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .Append(Mp4Boxes.Box("free", new byte[16]))
            .AppendHeaderOnly("mdat", 50L * 1024 * 1024);

        var ex = await Assert.ThrowsAsync<Mp4Exception>(file.InspectAsync);
        Assert.Equal(Mp4Inspector.ReasonMoov, ex.Reason);
        Assert.True(file.BytesRead < 1024, $"read {file.BytesRead} bytes");
    }

    [Fact]
    public async Task A_size_zero_box_runs_to_the_end()
    {
        var withMoovLast = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .Append(Mp4Boxes.Box("mdat", new byte[128]))
            .Append(Mp4Boxes.WithSizeZero(Mp4Boxes.Moov(Mp4Boxes.Mvhd(0, 1000, 1_500), Mp4Boxes.Trak(Mp4Boxes.Tkhd(0, 16, 16)))));
        Assert.Equal(1_500, (await withMoovLast.InspectAsync()).DurationMs);

        var mdatToTheEnd = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .Append(Mp4Boxes.WithSizeZero(Mp4Boxes.Box("mdat", new byte[128])));
        var ex = await Assert.ThrowsAsync<Mp4Exception>(mdatToTheEnd.InspectAsync);
        Assert.Equal(Mp4Inspector.ReasonMoov, ex.Reason);
    }

    [Fact]
    public async Task A_zero_duration_is_refused_with_reason_duration()
    {
        var file = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .Append(Mp4Boxes.Moov(Mp4Boxes.Mvhd(0, 1000, 0), Mp4Boxes.Trak(Mp4Boxes.Tkhd(0, 640, 480))));

        var ex = await Assert.ThrowsAsync<Mp4Exception>(file.InspectAsync);
        Assert.Equal(Mp4Inspector.ReasonDuration, ex.Reason);
    }

    [Fact]
    public async Task A_moov_over_16_MB_is_refused_without_being_read()
    {
        var file = new VirtualFile()
            .Append(Mp4Boxes.Ftyp())
            .AppendHeaderOnly("moov", Mp4Inspector.MoovCap + 1);

        var ex = await Assert.ThrowsAsync<Mp4Exception>(file.InspectAsync);
        Assert.Equal(Mp4Inspector.ReasonMoov, ex.Reason);
        Assert.True(file.BytesRead < 1024, $"read {file.BytesRead} bytes");
    }

    [Fact]
    public void Ftyp_must_sit_at_bytes_4_to_8()
    {
        Assert.True(Mp4Inspector.HasFtyp(Mp4Boxes.Ftyp().AsSpan(0, 16)));
        Assert.False(Mp4Inspector.HasFtyp(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0, 0, 0, 0, 0 }));
        Assert.False(Mp4Inspector.HasFtyp(Encoding.ASCII.GetBytes("ftyp")));
    }

    // A sparse file: the bytes appended are stored, the regions declared by a
    // header only read as zeros. Every read is recorded.
    private sealed class VirtualFile
    {
        private readonly List<(long Offset, byte[] Bytes)> _chunks = new();

        public long Length { get; private set; }
        public List<(long From, long To)> Reads { get; } = new();
        public long BytesRead { get; private set; }

        public VirtualFile Append(byte[] bytes)
        {
            _chunks.Add((Length, bytes));
            Length += bytes.Length;
            return this;
        }

        // A box whose 32-bit header says `size`; its body is not stored.
        public VirtualFile AppendHeaderOnly(string type, long size)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)size));
            Encoding.ASCII.GetBytes(type, header.AsSpan(4));
            _chunks.Add((Length, header));
            Length += size;
            return this;
        }

        // A box whose header carries the 64-bit largesize.
        public VirtualFile AppendLargeHeaderOnly(string type, long size)
        {
            var header = new byte[16];
            BinaryPrimitives.WriteUInt32BigEndian(header, 1);
            Encoding.ASCII.GetBytes(type, header.AsSpan(4));
            BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(8), (ulong)size);
            _chunks.Add((Length, header));
            Length += size;
            return this;
        }

        public Task<byte[]> ReadAsync(long from, long to, CancellationToken ct)
        {
            Reads.Add((from, to));
            var end = Math.Min(to, Length - 1);
            var buffer = new byte[Math.Max(0, end - from + 1)];
            foreach (var (offset, bytes) in _chunks)
            {
                var start = Math.Max(from, offset);
                var stop = Math.Min(end, offset + bytes.Length - 1);
                for (var i = start; i <= stop; i++) buffer[i - from] = bytes[i - offset];
            }
            BytesRead += buffer.Length;
            return Task.FromResult(buffer);
        }

        public async Task<Mp4Inspector.Mp4Info> InspectAsync()
        {
            var head = await ReadAsync(0, Math.Min(15, Length - 1), CancellationToken.None);
            Assert.True(Mp4Inspector.HasFtyp(head));
            return await Mp4Inspector.InspectAsync(head, Length, ReadAsync, CancellationToken.None);
        }
    }
}

// Hand-built ISO base media boxes: ftyp, moov, mvhd and tkhd in versions 0
// and 1, trak, and plain or 64-bit sized boxes.
internal static class Mp4Boxes
{
    public static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    public static byte[] Box(string type, params byte[][] body)
    {
        var payload = Concat(body);
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        payload.CopyTo(box, 8);
        return box;
    }

    public static byte[] LargeBox(string type, byte[] payload)
    {
        var box = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, 1);
        Encoding.ASCII.GetBytes(type, box.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(box.AsSpan(8), (ulong)box.Length);
        payload.CopyTo(box, 16);
        return box;
    }

    // The same box with its 32-bit size set to 0 (the box runs to the end).
    public static byte[] WithSizeZero(byte[] box)
    {
        var copy = (byte[])box.Clone();
        BinaryPrimitives.WriteUInt32BigEndian(copy, 0);
        return copy;
    }

    public static byte[] Ftyp() =>
        Box("ftyp", Encoding.ASCII.GetBytes("isom"), U32(512), Encoding.ASCII.GetBytes("isomiso2avc1mp41"));

    public static byte[] Moov(params byte[][] children) => Box("moov", children);

    public static byte[] Trak(params byte[][] children) => Box("trak", children);

    // mvhd: version and flags, the times, timescale, duration, then the 80
    // bytes of rate, volume, matrix, and next track id (zeros here).
    public static byte[] Mvhd(byte version, uint timescale, ulong duration) => version == 1
        ? Box("mvhd", new byte[] { 1, 0, 0, 0 }, new byte[16], U32(timescale), U64(duration), new byte[80])
        : Box("mvhd", new byte[] { 0, 0, 0, 0 }, new byte[8], U32(timescale), U32((uint)duration), new byte[80]);

    // tkhd: version and flags, the times, track id, duration, then 52 bytes
    // of reserved, layer, group, volume, and matrix, then width and height in
    // 16.16 fixed point.
    public static byte[] Tkhd(byte version, int width, int height) => version == 1
        ? Box("tkhd", new byte[] { 1, 0, 0, 7 }, new byte[32], new byte[52], U32((uint)width << 16), U32((uint)height << 16))
        : Box("tkhd", new byte[] { 0, 0, 0, 7 }, new byte[20], new byte[52], U32((uint)width << 16), U32((uint)height << 16));

    private static byte[] U32(uint value)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        return b;
    }

    private static byte[] U64(ulong value)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(b, value);
        return b;
    }
}
