using System.Text;
using MeowChan.Interop.Sidecars;

namespace MeowChan.Tests;

public sealed class PassiveSidecarDeterminismBoundaryTests
{
    [Fact]
    public void HydrusGoldenBytesPinEscapingWithoutSortingDeduplicationOrNormalization()
    {
        string[] tags = ["z", "A", "z", "", " a ", "é", "e\u0301", "\0\b\f\n\r\t", "\\", "\""];
        // Literal oracle, deliberately not produced by another JSON serializer.
        const string golden = """{"metadata":{"tags":["z","A","z",""," a ","\u00E9","e\u0301","\u0000\b\f\n\r\t","\\","\u0022"]}}""";
        AssertGolden(tags, golden);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlternateKeyAndValueSpellingsConvergeToOneHydrusImage(bool gallery)
    {
        string input = gallery
            ? """ { "t\u0061gs" : ["\u007A","A","z","e\u0301","\u00e9"], "i\u0064" : 101 } """
            : """ { "met\u0061data" : { "t\u0061gs" : ["\u007A","A","z","e\u0301","\u00e9"] } } """;
        var parsed = Parse(input, gallery);
        Assert.True(parsed.Success);
        Assert.Equal(gallery ? (long?)101 : null, parsed.ClaimId);
        string[] expected = ["z", "A", "z", "e\u0301", "é"];
        Assert.Equal(expected, parsed.Tags.ToArray());
        // Explicit chosen-tag export to Hydrus, not a gallery-dl roundtrip or claim export.
        AssertGolden(parsed.Tags.ToArray(), """{"metadata":{"tags":["z","A","z","e\u0301","\u00E9"]}}""");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateTagValuesAreValidButDecodedDuplicateTagKeysAreNot(bool gallery)
    {
        string valid = gallery ? """{"id":101,"tags":["x","\u0078","x"]}"""
            : """{"metadata":{"tags":["x","\u0078","x"]}}""";
        var accepted = Parse(valid, gallery);
        Assert.True(accepted.Success);
        Assert.Equal(new[] { "x", "x", "x" }, accepted.Tags.ToArray());
        string duplicate = gallery ? """{"tags":["x"],"id":101,"t\u0061gs":[]}"""
            : """{"metadata":{"t\u0061gs":["x"],"tags":[]}}""";
        var rejected = Parse(duplicate, gallery);
        Assert.False(rejected.Success);
        Assert.Equal(SidecarError.DuplicateProperty, rejected.Error);
        Assert.Empty(rejected.Tags);
        Assert.Null(rejected.ClaimId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void ControlEscapeExpansionHitsExactOutputCapWithSmallDecodedTags(int delta)
    {
        const string prefix = "{\"metadata\":{\"tags\":[";
        const string suffix = "]}}";
        const int cap = 1048576;
        // Each 1024-byte decoded tag expands to 6144 ASCII escape bytes plus quotes.
        string[] head = Enumerable.Repeat(new string('\0', 1024), 170).ToArray();
        string encodedHead = string.Join(",", Enumerable.Repeat("\"" +
            string.Concat(Enumerable.Repeat("\\u0000", 1024)) + "\"", 170));
        int tailLength = cap + delta - prefix.Length - encodedHead.Length - 1 - 2 - suffix.Length;
        Assert.InRange(tailLength, 1, 4096);
        string[] tags = head.Append(new string('a', tailLength)).ToArray();
        byte[] expected = Encoding.UTF8.GetBytes(prefix + encodedHead + ",\"" + tags[^1] + "\"" + suffix);
        Assert.Equal(cap + delta, expected.Length);
        var result = PassiveSidecarCodec.SerializeChosenTags(tags);
        if (delta > 0)
        {
            Assert.Equal(SidecarError.PayloadTooLarge, result.Error);
            Assert.Empty(result.Utf8);
            return;
        }
        Assert.Equal(SidecarError.None, result.Error);
        Assert.Equal(expected, result.Utf8.ToArray());
        var parsed = PassiveSidecarCodec.Parse(expected, SidecarProfile.HydrusJsonTagsV1);
        Assert.True(parsed.Success);
        Assert.Equal(tags, parsed.Tags.ToArray());
    }

    private static SidecarParseResult Parse(string json, bool gallery) =>
        PassiveSidecarCodec.Parse(Encoding.UTF8.GetBytes(json), gallery
            ? SidecarProfile.GalleryDlDanbooruIdTagsV1 : SidecarProfile.HydrusJsonTagsV1);

    private static void AssertGolden(string[] tags, string golden)
    {
        byte[] expected = Encoding.UTF8.GetBytes(golden);
        var first = PassiveSidecarCodec.SerializeChosenTags(tags);
        Assert.Equal(SidecarError.None, first.Error);
        Assert.Equal(expected, first.Utf8.ToArray());
        var parsed = PassiveSidecarCodec.Parse(first.Utf8.ToArray(), SidecarProfile.HydrusJsonTagsV1);
        Assert.True(parsed.Success);
        Assert.Null(parsed.ClaimId);
        Assert.Equal(tags, parsed.Tags.ToArray());
        var second = PassiveSidecarCodec.SerializeChosenTags(parsed.Tags.ToArray());
        Assert.Equal(SidecarError.None, second.Error);
        Assert.Equal(expected, second.Utf8.ToArray());
    }
}
