using System.Text;
using System.Text.Json;
using MeowChan.Interop.Sidecars;

namespace MeowChan.Tests.Interop.Sidecars;

public sealed class PassiveSidecarCodecTests
{
    private static SidecarParseResult Parse(string json, SidecarProfile profile = SidecarProfile.HydrusJsonTagsV1) =>
        PassiveSidecarCodec.Parse(Encoding.UTF8.GetBytes(json), profile);
    private static string Hydrus(params string[] tags) => JsonSerializer.Serialize(new { metadata = new { tags } });
    private static void Failed(SidecarParseResult result, SidecarError error)
    {
        Assert.Equal(error, result.Error);
        Assert.False(result.Success);
        Assert.Empty(result.Tags);
        Assert.Null(result.ClaimId);
    }

    [Fact]
    public void HydrusPreservesExactOrdinalTagsAndDuplicatesThroughDeterministicExport()
    {
        string[] tags = ["creator:synthetic_author", "synthetic_tag", "A", "a", "é", "e\u0301", "猫😀", "", " a ", "x_y", "x:y", "A", "\"\\\n\t", "\\uD800"];
        var first = PassiveSidecarCodec.SerializeChosenTags(tags);
        var second = PassiveSidecarCodec.SerializeChosenTags(tags);
        Assert.Equal(SidecarError.None, first.Error);
        Assert.Equal(first.Utf8.ToArray(), second.Utf8.ToArray());
        var result = PassiveSidecarCodec.Parse(first.Utf8.ToArray(), SidecarProfile.HydrusJsonTagsV1);
        Assert.True(result.Success);
        Assert.Equal(tags, result.Tags.ToArray());
        Assert.Null(result.ClaimId);
        tags[0] = "changed";
        Assert.Equal("creator:synthetic_author", result.Tags[0]);
        Assert.Equal("{\"metadata\":{\"tags\":[]}}", Encoding.UTF8.GetString(PassiveSidecarCodec.SerializeChosenTags(Array.Empty<string>()).Utf8.ToArray()));
    }

    [Fact]
    public void GallerySyntheticFixtureIsInputOnly()
    {
        var result = Parse("{\"id\":101,\"tags\":[\"synthetic_tag\",\"creator:synthetic_author\"]}", SidecarProfile.GalleryDlDanbooruIdTagsV1);
        Assert.True(result.Success);
        Assert.Equal(101L, result.ClaimId);
        Assert.Equal(new[] { "synthetic_tag", "creator:synthetic_author" }, result.Tags.ToArray());
        Failed(PassiveSidecarCodec.Parse("{}"u8.ToArray(), SidecarProfile.GalleryDlDanbooruIdTagsV1, SidecarDirection.Export), SidecarError.UnsupportedDirection);
        Assert.Equal(SidecarError.UnsupportedDirection, PassiveSidecarCodec.SerializeChosenTags([], SidecarProfile.GalleryDlDanbooruIdTagsV1).Error);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"metadata\":null}")]
    [InlineData("{\"metadata\":[]}")]
    [InlineData("{\"metadata\":{}}")]
    [InlineData("{\"metadata\":{\"tags\":null}}")]
    [InlineData("{\"metadata\":{\"tags\":\"tag\"}}")]
    [InlineData("{\"metadata\":{\"tags\":[null]}}")]
    [InlineData("{\"metadata\":{\"tags\":[{}]}}")]
    [InlineData("{\"metadata\":{\"tags\":[[]]}}")]
    [InlineData("{\"metadata\":{\"tags\":[true]}}")]
    [InlineData("{\"metadata\":{\"tags\":[1]}}")]
    public void RejectsMissingNullAndNestedWrongTypes(string json) => Failed(Parse(json), SidecarError.InvalidSchema);

    [Theory]
    [InlineData("{\"metadata\":{\"tags\":[],\"tags\":[]}}")]
    [InlineData("{\"metadata\":{\"tags\":[]},\"metadata\":{\"tags\":[]}}")]
    [InlineData("{\"metadata\":{\"tags\":[],\"t\\u0061gs\":[]}}")]
    public void DuplicateDecodedPropertiesFail(string json) => Failed(Parse(json), SidecarError.DuplicateProperty);

    [Theory]
    [InlineData("{\"metadata\":{\"tags\":[]}} {}")]
    [InlineData("{\"metadata\":{\"tags\":[],}}")]
    [InlineData("/*comment*/{\"metadata\":{\"tags\":[]}}")]
    [InlineData("")]
    public void RejectsNonStrictJson(string json) => Failed(Parse(json), SidecarError.InvalidJson);

    [Theory]
    [InlineData("{\"metadata\":{\"tags\":[\"\\uD800\"]}}")]
    [InlineData("{\"metadata\":{\"tags\":[\"\\uDC00\"]}}")]
    [InlineData("{\"metadata\":{\"tags\":[\"\\uD800x\"]}}")]
    [InlineData("{\"metadata\":{\"tags\":[\"\\uD800\\uD800\"]}}")]
    [InlineData("{\"metadata\":{\"tags\":[]},\"late\\uD800\":0}")]
    [InlineData("{\"metadata\":{\"tags\":[]},\"unknown\":{\"late\\uDC00\":0}}")]
    [InlineData("{\"metadata\":{\"tags\":[]},\"unknown\":[\"\\uDFFF\"]}")]
    public void ValidatesEscapedUnicodeEvenInLateUnknownFields(string json) => Failed(Parse(json), SidecarError.InvalidUnicode);

    [Fact]
    public void AcceptsPairedEscapesAndRejectsMalformedUtf8AndBom()
    {
        Assert.Equal("😀", Assert.Single(Parse("{\"metadata\":{\"tags\":[\"\\uD83D\\uDE00\"]}}").Tags));
        foreach (byte[] bad in new[] { new byte[] { 0xff }, new byte[] { 0xc0, 0xaf }, new byte[] { 0xed, 0xa0, 0x80 }, new byte[] { 0xf4, 0x90, 0x80, 0x80 }, new byte[] { 0xe2, 0x82 } })
            Failed(PassiveSidecarCodec.Parse(bad, SidecarProfile.HydrusJsonTagsV1), SidecarError.InvalidUtf8);
        byte[] late = Encoding.UTF8.GetBytes("{\"metadata\":{\"tags\":[]},\"late").Concat(new byte[] { 0xff }).Concat("\":0}"u8.ToArray()).ToArray();
        Failed(PassiveSidecarCodec.Parse(late, SidecarProfile.HydrusJsonTagsV1), SidecarError.InvalidUtf8);
        Failed(Parse("\uFEFF" + Hydrus()), SidecarError.BomNotAllowed);
        Assert.Equal(SidecarError.InvalidUnicode, PassiveSidecarCodec.SerializeChosenTags(["\uD800"]).Error);
        Assert.Equal(SidecarError.InvalidSchema, PassiveSidecarCodec.SerializeChosenTags(null).Error);
        Assert.Equal(SidecarError.InvalidSchema, PassiveSidecarCodec.SerializeChosenTags([null!]).Error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("9007199254740992")]
    [InlineData("1.0")]
    [InlineData("1e0")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("\"101\"")]
    public void GalleryClaimIdIsBoundedIntegerToken(string id) =>
        Failed(Parse("{\"id\":" + id + ",\"tags\":[]}", SidecarProfile.GalleryDlDanbooruIdTagsV1), SidecarError.InvalidSchema);

    [Fact]
    public void GalleryClaimBoundariesAndDumpRejection()
    {
        foreach (long id in new[] { 1L, PassiveSidecarCodec.MaxClaimId })
            Assert.Equal(id, Parse("{\"tags\":[],\"id\":" + id + "}", SidecarProfile.GalleryDlDanbooruIdTagsV1).ClaimId);
        Failed(Parse("[[3,\"https://example.invalid/\",{\"id\":101}]]", SidecarProfile.GalleryDlDanbooruIdTagsV1), SidecarError.InvalidSchema);
        Failed(Parse("{\"id\":101}", SidecarProfile.GalleryDlDanbooruIdTagsV1), SidecarError.InvalidSchema);
        Failed(Parse("{\"id\":101,\"tags\":null}", SidecarProfile.GalleryDlDanbooruIdTagsV1), SidecarError.InvalidSchema);
    }

    [Fact]
    public void UnknownProfilesAndDirectionsAreExplicit()
    {
        foreach (var profile in new[] { SidecarProfile.GrabberUnsupported, (SidecarProfile)999 })
        {
            Failed(PassiveSidecarCodec.Parse("{}"u8.ToArray(), profile), SidecarError.UnsupportedProfile);
            Assert.Equal(SidecarError.UnsupportedProfile, PassiveSidecarCodec.SerializeChosenTags([], profile).Error);
        }
        Failed(PassiveSidecarCodec.Parse("{}"u8.ToArray(), SidecarProfile.HydrusJsonTagsV1, (SidecarDirection)999), SidecarError.UnsupportedDirection);
        Assert.Equal(SidecarError.UnsupportedDirection, PassiveSidecarCodec.SerializeChosenTags([], direction: SidecarDirection.Import).Error);
        Assert.Equal(SidecarError.UnsupportedDirection, PassiveSidecarCodec.SerializeChosenTags([], direction: (SidecarDirection)999).Error);
    }

    [Theory]
    [InlineData("{\"metadata\":{\"tags\":[],\"urls\":[\"https://synthetic:SECRET@example.invalid/post/101\"]}}", SidecarProfile.HydrusJsonTagsV1)]
    [InlineData("{\"id\":101,\"tags\":[],\"file_url\":\"https://example.invalid/file?token=SECRET&Signature=SECRET\"}", SidecarProfile.GalleryDlDanbooruIdTagsV1)]
    [InlineData("{\"metadata\":{\"tags\":[]},\"SECRET\":0}", SidecarProfile.HydrusJsonTagsV1)]
    public void UnknownSecretsAreNotReflected(string json, object profile)
    {
        var result = Parse(json, (SidecarProfile)profile);
        Failed(result, SidecarError.InvalidSchema);
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.DoesNotContain("https:", result.ToString());
    }

    [Fact]
    public void TagCountAndDecodedByteLimitsAreInclusive()
    {
        string[] tags = Enumerable.Repeat("", PassiveSidecarCodec.MaxTags).ToArray();
        Assert.True(Parse(Hydrus(tags)).Success);
        Assert.Equal(SidecarError.None, PassiveSidecarCodec.SerializeChosenTags(tags).Error);
        string[] tooMany = tags.Append("").ToArray();
        Failed(Parse(Hydrus(tooMany)), SidecarError.TooManyTags);
        Assert.Equal(SidecarError.TooManyTags, PassiveSidecarCodec.SerializeChosenTags(tooMany).Error);
        foreach (string tag in new[] { new string('a', 4096), new string('é', 2048) })
        {
            Assert.True(Parse(Hydrus(tag)).Success);
            Assert.Equal(SidecarError.None, PassiveSidecarCodec.SerializeChosenTags([tag]).Error);
            Failed(Parse(Hydrus(tag + "a")), SidecarError.TagTooLarge);
            Assert.Equal(SidecarError.TagTooLarge, PassiveSidecarCodec.SerializeChosenTags([tag + "a"]).Error);
        }
    }

    [Fact]
    public void PayloadBoundaryAndDepthAreBoundedAndExportChecksSameParser()
    {
        byte[] valid = Encoding.UTF8.GetBytes(Hydrus());
        byte[] exact = Enumerable.Repeat((byte)' ', PassiveSidecarCodec.MaxPayloadBytes).ToArray();
        valid.CopyTo(exact, 0);
        Assert.True(PassiveSidecarCodec.Parse(exact, SidecarProfile.HydrusJsonTagsV1).Success);
        Failed(PassiveSidecarCodec.Parse(exact.Append((byte)' ').ToArray(), SidecarProfile.HydrusJsonTagsV1), SidecarError.PayloadTooLarge);
        string deep = "{\"metadata\":{\"tags\":[]},\"unknown\":" + new string('[', 9) + "0" + new string(']', 9) + "}";
        Failed(Parse(deep), SidecarError.InvalidJson);
        Assert.Equal(SidecarError.PayloadTooLarge, PassiveSidecarCodec.SerializeChosenTags(Enumerable.Repeat(new string('a', 4096), 300).ToArray()).Error);
        string[] escapedExpansion = Enumerable.Repeat(new string('é', 2048), 100).ToArray();
        Assert.Equal(SidecarError.PayloadTooLarge, PassiveSidecarCodec.SerializeChosenTags(escapedExpansion).Error);
    }
}
