using System.Buffers.Binary;
using System.Text;
using MeowChan.PluginHost.Filters;

namespace MeowChan.PluginHost.Header.Tests;

public sealed class ImageHeaderTests
{
    public static IEnumerable<object[]> Headers()
    {
        yield return [Png(17, 29), 17, 29, 24];
        yield return [Jpeg(17, 29), 17, 29, 12];
        yield return [Gif("GIF87a", 17, 29), 17, 29, 13];
        yield return [Gif("GIF89a", 17, 29), 17, 29, 13];
        yield return [Webp("VP8X", Extended(17, 29)), 17, 29, 30];
        yield return [Webp("VP8X", Extended(17, 29, 2)), 17, 29, 30]; // animated canvas
        yield return [Webp("VP8 ", Lossy(17, 29)), 17, 29, 30];
        yield return [Webp("VP8L", Lossless(17, 29)), 17, 29, 25];
    }
    public static IEnumerable<object[]> HeaderBytes() => Headers().Select(row => new[] { row[0] });
    public static IEnumerable<object[]> HeaderReadCounts() => Headers().Select(row => new[] { row[0], row[3] });

    [Theory]
    [MemberData(nameof(Headers))]
    public async Task ReadsOnlyBoundedCanvasHeadersWithoutDecoding(byte[] bytes, int width, int height, int reads)
    {
        using TrackingStream input = new(bytes);
        Assert.Equal((width, height), await ImageHeader.ReadAsync(input, CancellationToken.None));
        Assert.Equal(reads, input.BytesRead);
        Assert.InRange(input.MaximumRead, 1, 24);
    }

    [Theory]
    [MemberData(nameof(HeaderBytes))]
    public async Task TruncatedRequiredHeaderCannotProduceDimensions(byte[] bytes)
    {
        // JPEG retains its existing SOF-only policy; the full declared segment is
        // present in its fixture. PNG similarly needs just the dimension header.
        for (int count = 0; count < bytes.Length; count++)
        {
            using MemoryStream input = new(bytes[..count]);
            Exception? error = await Record.ExceptionAsync(async () => await ImageHeader.ReadAsync(input, CancellationToken.None));
            Assert.True(error is InvalidDataException or EndOfStreamException, $"Length {count}: {error}");
        }
    }

    [Theory]
    [MemberData(nameof(HeaderBytes))]
    public async Task CancellationBeforeAnyReadIsRespected(byte[] bytes)
    {
        using CancellationTokenSource cancellation = new(); cancellation.Cancel();
        using TrackingStream input = new(bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageHeader.ReadAsync(input, cancellation.Token));
        Assert.Equal(0, input.BytesRead);
    }

    [Theory]
    [MemberData(nameof(HeaderReadCounts))]
    public async Task CancellationOnFinalRequiredReadIsRespected(byte[] bytes, int reads)
    {
        using CancellationTokenSource cancellation = new();
        using TrackingStream input = new(bytes) { CancelAfterBytes = reads, Cancellation = cancellation };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ImageHeader.ReadAsync(input, cancellation.Token));
    }

    [Fact]
    public async Task GifGlobalPaletteMustFitWithoutReadingIt()
    {
        byte[] bytes = Gif("GIF89a", 1, 2); bytes[10] = 0x80;
        for (int palette = 0; palette < 6; palette++)
        {
            using MemoryStream truncated = new(bytes.Concat(new byte[palette]).ToArray());
            await Assert.ThrowsAsync<InvalidDataException>(() => ImageHeader.ReadAsync(truncated, CancellationToken.None));
        }
        using TrackingStream complete = new(bytes.Concat(new byte[6]).ToArray());
        Assert.Equal((1, 2), await ImageHeader.ReadAsync(complete, CancellationToken.None));
        Assert.Equal(13, complete.BytesRead);
    }

    [Fact]
    public async Task OversizedCanvasesRemainExactForTheExistingHostPixelGate()
    {
        foreach ((byte[] bytes, int width, int height) in new[] {
            (Gif("GIF89a", 65535, 65535), 65535, 65535),
            (Webp("VP8X", Extended(16777216, 16777216)), 16777216, 16777216),
            (Webp("VP8L", Lossless(16384, 16384)), 16384, 16384),
            (Webp("VP8 ", Lossy(16383, 16383)), 16383, 16383),
            (Png(int.MaxValue, int.MaxValue), int.MaxValue, int.MaxValue) })
        {
            using MemoryStream input = new(bytes);
            var dimensions = await ImageHeader.ReadAsync(input, CancellationToken.None);
            Assert.Equal((width, height), dimensions);
            Assert.True((long)dimensions.Width * dimensions.Height > 16_000_000);
        }
        // ImageHeader only reads dimensions. FilterStageCoordinator owns and
        // retains the configured long-product pixel budget; no limit is raised.
    }

    [Fact]
    public async Task LargeWebpPayloadIsNotReadOrDecoded()
    {
        byte[] payload = Lossy(11, 13).Concat(new byte[1024 * 1024]).ToArray();
        using TrackingStream input = new(Webp("VP8 ", payload));
        Assert.Equal((11, 13), await ImageHeader.ReadAsync(input, CancellationToken.None));
        Assert.Equal(30, input.BytesRead);
    }

    public static IEnumerable<object[]> InvalidHeaders()
    {
        yield return [Encoding.ASCII.GetBytes("unknown image bytes")];
        yield return [Gif("GIF90a", 1, 1)];
        yield return [Gif("GIF89a", 0, 1)];
        yield return [Gif("GIF89a", 1, 0)];
        yield return [Png(0, 1)];
        yield return [Png(uint.MaxValue, 1)];
        yield return [Jpeg(0, 1)];
        yield return [Webp("VP8 ", Lossy(0, 1))];
        yield return [Webp("VP8 ", Lossy(1, 0))];
        yield return [Webp("ABCD", new byte[10])];
        yield return [Webp("VP8X", new byte[9])];
        yield return [Webp("VP8X", Extended(1, 1, 1))];
        yield return [Webp("VP8X", Extended(1, 1, 64))];
        byte[] reserved = Extended(1, 1); reserved[2] = 1;
        yield return [Webp("VP8X", reserved)];
        yield return [Webp("VP8L", new byte[5])];
        byte[] badVersion = Lossless(1, 1); badVersion[4] |= 32;
        yield return [Webp("VP8L", badVersion)];
        byte[] interframe = Lossy(1, 1); interframe[0] = 1;
        yield return [Webp("VP8 ", interframe)];
        byte[] badMarker = Lossy(1, 1); badMarker[3] = 0;
        yield return [Webp("VP8 ", badMarker)];
        byte[] wrongType = Webp("VP8X", Extended(1, 1)); wrongType[8] = (byte)'A';
        yield return [wrongType];
        byte[] wrongSize = Webp("VP8X", Extended(1, 1)); wrongSize[4]++;
        yield return [wrongSize];
        byte[] hugeChunk = Webp("VP8X", Extended(1, 1)); BinaryPrimitives.WriteUInt32LittleEndian(hugeChunk.AsSpan(16), uint.MaxValue);
        yield return [hugeChunk];
        byte[] missingPadding = Webp("VP8L", Lossless(1, 1))[..^1];
        BinaryPrimitives.WriteUInt32LittleEndian(missingPadding.AsSpan(4), (uint)missingPadding.Length - 8);
        yield return [missingPadding];
    }

    [Theory]
    [MemberData(nameof(InvalidHeaders))]
    public async Task InvalidContainersAndDimensionsAreRejected(byte[] bytes)
    {
        using MemoryStream input = new(bytes);
        Exception? error = await Record.ExceptionAsync(async () => await ImageHeader.ReadAsync(input, CancellationToken.None));
        Assert.True(error is InvalidDataException or EndOfStreamException, $"Unexpected result: {error}");
    }

    private static byte[] Png(uint width, uint height)
    {
        byte[] bytes = new byte[24];
        new byte[] { 137,80,78,71,13,10,26,10,0,0,0,13,73,72,68,82 }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), height);
        return bytes;
    }
    private static byte[] Jpeg(int width, int height) =>
        [255, 216, 255, 192, 0, 11, 8, (byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width, 1, 1, 0x11, 0];
    private static byte[] Gif(string signature, int width, int height)
    {
        byte[] bytes = new byte[13]; Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), (ushort)height);
        return bytes;
    }
    private static byte[] Extended(int width, int height, byte flags = 0) =>
        [flags, 0, 0, 0, (byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16),
            (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16)];
    private static byte[] Lossy(int width, int height) =>
        [0x10, 0, 0, 157, 1, 42, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8)];
    private static byte[] Lossless(int width, int height)
    {
        uint packed = (uint)(width - 1) | (uint)(height - 1) << 14;
        byte[] bytes = new byte[5]; bytes[0] = 47;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1), packed); return bytes;
    }
    private static byte[] Webp(string type, byte[] payload)
    {
        byte[] bytes = new byte[20 + payload.Length + (payload.Length & 1)];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(bytes, 0); Encoding.ASCII.GetBytes("WEBP").CopyTo(bytes, 8);
        Encoding.ASCII.GetBytes(type).CopyTo(bytes, 12);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)bytes.Length - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), (uint)payload.Length);
        payload.CopyTo(bytes, 20); return bytes;
    }
    private sealed class TrackingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int BytesRead { get; private set; }
        internal int MaximumRead { get; private set; }
        internal int CancelAfterBytes { get; init; } = int.MaxValue;
        internal CancellationTokenSource? Cancellation { get; init; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            MaximumRead = Math.Max(MaximumRead, buffer.Length);
            int read = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += read;
            if (BytesRead >= CancelAfterBytes) Cancellation?.Cancel();
            return read;
        }
    }
}
