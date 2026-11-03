using System.Buffers;
using System.Text;
using MeowChan.Interop.Sidecars;

namespace MeowChan.Tests.Interop.Sidecars;

public sealed class PassiveSidecarInputOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidationAndMaterializationUseOnlyTheFirstMemoryImage(bool gallery)
    {
        string first = gallery ? "{\"id\":101,\"tags\":[\"first\"]}" : "{\"metadata\":{\"tags\":[\"first\"]}}";
        string later = first.Replace("first", "later").Replace("101", "202");
        using var memory = new SwitchingMemory(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(later));

        var result = PassiveSidecarCodec.Parse(memory.Input, Profile(gallery));

        Assert.True(result.Success);
        Assert.Equal("first", Assert.Single(result.Tags));
        Assert.Equal(gallery ? (long?)101 : null, result.ClaimId);
        Assert.Equal(1, memory.Accesses);
        memory.OverwriteFirst();
        Assert.Equal("first", Assert.Single(result.Tags));
    }

    [Fact]
    public void DuplicatePropertyCannotBeIntroducedOnASecondMemoryAccess()
    {
        const string later = "{\"metadata\":{\"tags\":[\"first\"],\"tags\":[\"later\"]}}";
        string first = "{\"metadata\":{\"tags\":[\"first\"]}}".PadRight(later.Length);
        using var memory = new SwitchingMemory(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(later));

        var result = PassiveSidecarCodec.Parse(memory.Input, SidecarProfile.HydrusJsonTagsV1);

        Assert.True(result.Success);
        Assert.Equal("first", Assert.Single(result.Tags));
        Assert.Equal(1, memory.Accesses);
    }

    [Fact]
    public void InvalidFirstImageIsRejectedWithoutReadingAValidReplacement()
    {
        const string first = "{\"id\":101,\"tags\":[],\"tags\":[]}";
        string later = "{\"id\":202,\"tags\":[]}".PadRight(first.Length);
        using var memory = new SwitchingMemory(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(later));

        var result = PassiveSidecarCodec.Parse(memory.Input, SidecarProfile.GalleryDlDanbooruIdTagsV1);

        Failed(result, SidecarError.DuplicateProperty);
        Assert.Equal(1, memory.Accesses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArraySliceAndReturnedValuesSurviveCallerBufferReuse(bool gallery)
    {
        string json = gallery ? "{\"id\":101,\"tags\":[\"first\",\"first\",\" a \"]}" :
            "{\"metadata\":{\"tags\":[\"first\",\"first\",\" a \"]}}";
        byte[] payload = Encoding.UTF8.GetBytes(json);
        byte[] buffer = new byte[payload.Length + 8];
        Array.Fill(buffer, (byte)0xff); // invalid bytes outside the selected slice must be ignored
        payload.CopyTo(buffer, 4);

        var result = PassiveSidecarCodec.Parse(buffer.AsMemory(4, payload.Length), Profile(gallery));
        Array.Fill(buffer, (byte)0xff);

        Assert.True(result.Success);
        Assert.Equal(new[] { "first", "first", " a " }, result.Tags.ToArray());
        Assert.Equal(gallery ? (long?)101 : null, result.ClaimId);
    }

    [Fact]
    public void OversizeAndUnsupportedInputsAreRejectedBeforeAccessingMemory()
    {
        byte[] oversized = new byte[PassiveSidecarCodec.MaxPayloadBytes + 1];
        using var memory = new SwitchingMemory(oversized, oversized);
        Failed(PassiveSidecarCodec.Parse(memory.Input, SidecarProfile.HydrusJsonTagsV1), SidecarError.PayloadTooLarge);
        Failed(PassiveSidecarCodec.Parse(memory.Input, SidecarProfile.GrabberUnsupported), SidecarError.UnsupportedProfile);
        Failed(PassiveSidecarCodec.Parse(memory.Input, SidecarProfile.GalleryDlDanbooruIdTagsV1,
            SidecarDirection.Export), SidecarError.UnsupportedDirection);
        Assert.Equal(0, memory.Accesses);
    }

    private static SidecarProfile Profile(bool gallery) => gallery ?
        SidecarProfile.GalleryDlDanbooruIdTagsV1 : SidecarProfile.HydrusJsonTagsV1;

    private static void Failed(SidecarParseResult result, SidecarError error)
    {
        Assert.False(result.Success);
        Assert.Equal(error, result.Error);
        Assert.Empty(result.Tags);
        Assert.Null(result.ClaimId);
    }

    // No timers or racing threads: every span access after the first supplies a different image.
    private sealed class SwitchingMemory(byte[] first, byte[] later) : MemoryManager<byte>
    {
        public int Accesses { get; private set; }
        // CreateMemory avoids the Memory getter's initial GetSpan call contaminating the counter.
        public ReadOnlyMemory<byte> Input => CreateMemory(first.Length);
        public override Span<byte> GetSpan() => ++Accesses == 1 ? first : later;
        public void OverwriteFirst() => Array.Fill(first, (byte)0xff);
        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();
        public override void Unpin() { }
        protected override void Dispose(bool disposing) { }
    }
}
