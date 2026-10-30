using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class VideoStreamDemuxTests
{
    [Theory]
    [InlineData(true, 24)]
    [InlineData(false, 8)]
    [InlineData(false, 100)]
    public void SniffUsesBoundedContainerBytesAndRestoresTheVerifiedHandle(bool expected, int boxLength)
    {
        byte[] bytes = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)boxLength);
        "ftypisom"u8.CopyTo(bytes.AsSpan(4));
        using MemoryStream stream = new(bytes);
        stream.Position = 7;
        Assert.Equal(expected, VideoStreamDemux.HasIsoBaseMediaHeader(stream));
        Assert.Equal(7, stream.Position);
        Assert.True(stream.CanRead);
    }

    [Fact]
    public void ALabelOrEmbeddedFtypDoesNotSelectAMp4Demux()
    {
        using MemoryStream stream = new("not mp4 ftypisom but named clip.mp4"u8.ToArray());
        Assert.False(VideoStreamDemux.HasIsoBaseMediaHeader(stream));
        Assert.Equal(0, stream.Position);
    }

    [Fact]
    public void ShortInputIsNotAnIsoContainer()
    {
        using MemoryStream stream = new("ftyp"u8.ToArray());
        stream.Position = 2;
        Assert.False(VideoStreamDemux.HasIsoBaseMediaHeader(stream));
        Assert.Equal(2, stream.Position);
    }

    [Fact]
    public void LegalShortReadsDoNotChangeSniffOrHandleOwnership()
    {
        byte[] bytes = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, 24);
        "ftypisom"u8.CopyTo(bytes.AsSpan(4));
        using ShortReadStream stream = new(bytes);
        stream.Position = 9;
        Assert.True(VideoStreamDemux.HasIsoBaseMediaHeader(stream));
        Assert.Equal(9, stream.Position);
        Assert.True(stream.CanRead);
    }

    private sealed class ShortReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(2, buffer.Length)]);
    }
}
