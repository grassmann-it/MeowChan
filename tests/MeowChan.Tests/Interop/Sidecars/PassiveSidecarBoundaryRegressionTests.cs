using System.Text;
using System.Text.Json;
using MeowChan.Interop.Sidecars;

namespace MeowChan.Tests.Interop.Sidecars;

public sealed class PassiveSidecarBoundaryRegressionTests
{
    private static readonly SidecarProfile[] ImportProfiles =
        [SidecarProfile.HydrusJsonTagsV1, SidecarProfile.GalleryDlDanbooruIdTagsV1];

    private const string UserInfoUrl = "https://synthetic-user:SYNTHETIC_PASSWORD@example.invalid/private";
    private const string SignedUrl = "https://example.invalid/file?token=SYNTHETIC_TOKEN&Signature=SYNTHETIC_SIGNATURE";
    private const string Bearer = "Bearer SYNTHETIC_BEARER";
    private const string PrivatePath = "/synthetic/private/SYNTHETIC_PATH";
    private const string ChosenTag = "SYNTHETIC_CHOSEN_TAG";

    // tagTokens are already JSON-encoded strings/elements; extraProperties are root members.
    private static string Document(SidecarProfile profile, string tagTokens, string extraProperties = "") =>
        profile == SidecarProfile.HydrusJsonTagsV1
            ? "{\"metadata\":{\"tags\":[" + tagTokens + "]}" + extraProperties + "}"
            : "{\"id\":101,\"tags\":[" + tagTokens + "]" + extraProperties + "}";

    private static SidecarParseResult Parse(string json, SidecarProfile profile) =>
        PassiveSidecarCodec.Parse(Encoding.UTF8.GetBytes(json), profile);

    private static void Failed(SidecarParseResult result, SidecarError error)
    {
        Assert.Equal(error, result.Error);
        Assert.False(result.Success);
        Assert.Empty(result.Tags);
        Assert.Null(result.ClaimId);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void HydrusOutputBudgetIncludesQuotesCommasAndAllThreeClosingBytes(int delta)
    {
        const int oneMiB = 1024 * 1024;
        const int tagCount = 256;
        const int fullTagBytes = 4096;
        const string prefix = "{\"metadata\":{\"tags\":[";
        const string suffix = "]}}";
        Assert.Equal(oneMiB, PassiveSidecarCodec.MaxPayloadBytes);

        // Only unescaped ASCII: count envelope, two quotes per tag and n-1 commas
        // independently of the product writer (no padding or oversized decoded tag).
        int punctuationBytes = prefix.Length + suffix.Length + 2 * tagCount + tagCount - 1;
        int tailLength = oneMiB + delta - punctuationBytes - (tagCount - 1) * fullTagBytes;
        Assert.InRange(tailLength, 1, PassiveSidecarCodec.MaxTagBytes);
        string[] tags = Enumerable.Repeat(new string('a', fullTagBytes), tagCount - 1)
            .Append(new string('b', tailLength)).ToArray();
        string expectedJson = prefix + string.Join(",", tags.Select(tag => "\"" + tag + "\"")) + suffix;
        byte[] expectedBytes = Encoding.UTF8.GetBytes(expectedJson);
        Assert.Equal(oneMiB + delta, expectedBytes.Length);
        Assert.Equal(3, Encoding.UTF8.GetByteCount(suffix));
        if (delta is >= 1 and <= 3)
            Assert.True(expectedBytes.Length - suffix.Length <= oneMiB);

        var result = PassiveSidecarCodec.SerializeChosenTags(tags);
        if (delta > 0)
        {
            // +1..+3 fit at the last per-tag flush; only closing punctuation
            // crosses the cap. +4 also crosses it before closing the container.
            Assert.Equal(SidecarError.PayloadTooLarge, result.Error);
            Assert.Empty(result.Utf8);
            return;
        }

        Assert.Equal(SidecarError.None, result.Error);
        Assert.Equal(expectedBytes, result.Utf8.ToArray());
        var parsed = PassiveSidecarCodec.Parse(result.Utf8.ToArray(), SidecarProfile.HydrusJsonTagsV1);
        Assert.True(parsed.Success);
        Assert.Equal(tags, parsed.Tags.ToArray());
        Assert.Null(parsed.ClaimId);
    }

    [Theory]
    [InlineData("\"id\":101,\"\\u0069d\":101")]
    [InlineData("\"i\\u0064\":101,\"id\":102")]
    [InlineData("\"\\u0069\\u0064\":101,\"i\\u0064\":101")]
    [InlineData("\"\\u0069d\":102,\"id\":101")]
    public void GalleryRejectsDecodedDuplicateIdRegardlessOfSpellingOrderOrValue(string idMembers)
    {
        string json = "{\"tags\":[\"synthetic_tag\"]," + idMembers + "}";
        Failed(Parse(json, SidecarProfile.GalleryDlDanbooruIdTagsV1), SidecarError.DuplicateProperty);
    }

    [Theory]
    [InlineData("\\uD7FF", "\uD7FF")]
    [InlineData("\\uE000", "\uE000")]
    [InlineData("\\uD800\\uDC00", "\U00010000")]
    [InlineData("\\uDbFf\\uDfFf", "\U0010FFFF")]
    [InlineData("\\\\uDC00", "\\uDC00")]
    [InlineData("\\u005CuD800", "\\uD800")]
    [InlineData("\\\\\\uD800\\uDC00", "\\\U00010000")]
    [InlineData("\\uD800\\uDC00\\\\", "\U00010000\\")]
    [InlineData("\\\\uD800\\uD800\\uDC00", "\\uD800\U00010000")]
    public void BothProfilesDecodeScalarEndpointsAndEscapedBackslashBoundariesExactly(string encoded, string decoded)
    {
        foreach (var profile in ImportProfiles)
        {
            var result = Parse(Document(profile, "\"" + encoded + "\""), profile);
            Assert.True(result.Success, $"{profile}: {result.Error}");
            Assert.Equal(decoded, Assert.Single(result.Tags));
            Assert.Equal(profile == SidecarProfile.GalleryDlDanbooruIdTagsV1 ? (long?)101 : null, result.ClaimId);
        }
        var exported = PassiveSidecarCodec.SerializeChosenTags([decoded]);
        Assert.Equal(SidecarError.None, exported.Error);
        var reparsed = PassiveSidecarCodec.Parse(exported.Utf8.ToArray(), SidecarProfile.HydrusJsonTagsV1);
        Assert.True(reparsed.Success);
        Assert.Equal(decoded, Assert.Single(reparsed.Tags));
    }

    [Theory]
    [InlineData("\\uDBFF\\uDBFF")]
    [InlineData("\\uDC00\\uD800")]
    [InlineData("\\uD800\\\\uDC00")]
    [InlineData("\\uD800\\u005CuDC00")]
    [InlineData("\\uD800x\\uDC00")]
    [InlineData("\\\\\\uDC00")]
    [InlineData("\\uDBFF\\uE000")]
    [InlineData("\\uD7FF\\uDC00")]
    [InlineData("\\uD800\\uDC00\\uDFFF")]
    [InlineData("\\uD800\\uDC00\\uDBFF")]
    public void BothProfilesRejectUnpairedSurrogatesAcrossEscapeBoundaries(string encoded)
    {
        foreach (var profile in ImportProfiles)
            Failed(Parse(Document(profile, "\"synthetic_tag\",\"" + encoded + "\""), profile), SidecarError.InvalidUnicode);
    }

    [Theory]
    [InlineData("\\uD80")]
    [InlineData("\\uZZZZ")]
    [InlineData("\\x41")]
    [InlineData("\\")]
    public void BothProfilesRejectMalformedOrIncompleteJsonEscapes(string encoded)
    {
        foreach (var profile in ImportProfiles)
            Failed(Parse(Document(profile, "\"synthetic_tag\",\"" + encoded + "\""), profile), SidecarError.InvalidJson);
    }

    [Theory]
    [InlineData("\\uD800\\uDC00", "\U00010000")]
    [InlineData("\\uDBFF\\uDFFF", "\U0010FFFF")]
    public void BothProfilesCountDecodedUtf8AtSupplementaryScalarTagBoundary(string encodedScalar, string scalar)
    {
        string encoded = string.Concat(Enumerable.Repeat(encodedScalar, 1024));
        string decoded = string.Concat(Enumerable.Repeat(scalar, 1024));
        Assert.Equal(PassiveSidecarCodec.MaxTagBytes, Encoding.UTF8.GetByteCount(decoded));
        foreach (var profile in ImportProfiles)
        {
            var result = Parse(Document(profile, "\"" + encoded + "\""), profile);
            Assert.True(result.Success, $"{profile}: {result.Error}");
            Assert.Equal(decoded, Assert.Single(result.Tags));
            Failed(Parse(Document(profile, "\"" + encoded + "a\""), profile), SidecarError.TagTooLarge);
        }

        var exported = PassiveSidecarCodec.SerializeChosenTags([decoded]);
        Assert.Equal(SidecarError.None, exported.Error);
        Assert.Equal(decoded, Assert.Single(PassiveSidecarCodec.Parse(exported.Utf8.ToArray(), SidecarProfile.HydrusJsonTagsV1).Tags));
        var tooLarge = PassiveSidecarCodec.SerializeChosenTags([decoded + "a"]);
        Assert.Equal(SidecarError.TagTooLarge, tooLarge.Error);
        Assert.Empty(tooLarge.Utf8);
    }

    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDBFF)]
    [InlineData(0xDC00)]
    [InlineData(0xDFFF)]
    public void HydrusExportRejectsLateUnpairedUtf16WithoutReturningEarlierChosenTags(int unit)
    {
        string invalid = new string('a', 4095) + (char)unit;
        var result = PassiveSidecarCodec.SerializeChosenTags([ChosenTag, invalid]);
        Assert.Equal(SidecarError.InvalidUnicode, result.Error);
        Assert.Empty(result.Utf8);
        Assert.DoesNotContain(ChosenTag, result.ToString());
    }

    [Theory]
    [InlineData(SidecarProfile.HydrusJsonTagsV1)]
    [InlineData(SidecarProfile.GalleryDlDanbooruIdTagsV1)]
    public void RejectedResultsDoNotRetainTagsClaimsForeignKeysValuesOrExceptionText(object selectedProfile)
    {
        var profile = (SidecarProfile)selectedProfile;
        string tag = JsonSerializer.Serialize(ChosenTag);
        (string Json, SidecarError Error)[] cases =
        [
            (Document(profile, tag, "," + JsonSerializer.Serialize(UserInfoUrl) + ":0"), SidecarError.InvalidSchema),
            (Document(profile, tag, ",\"file_url\":" + JsonSerializer.Serialize(SignedUrl)), SidecarError.InvalidSchema),
            (Document(profile, tag, ",\"auth\":{\"Authorization\":" + JsonSerializer.Serialize(Bearer) +
                ",\"path\":" + JsonSerializer.Serialize(PrivatePath) + "}"), SidecarError.InvalidSchema),
            (Document(profile, tag, ",\"SYNTHETIC_DUPLICATE_KEY\":0,\"\\u0053YNTHETIC_DUPLICATE_KEY\":1"), SidecarError.DuplicateProperty),
            (Document(profile, tag, ",\"SYNTHETIC_UNICODE_KEY\":\"SYNTHETIC_PRIVATE_VALUE\\uDBFF\""), SidecarError.InvalidUnicode),
            (Document(profile, tag, ",\"SYNTHETIC_UNICODE_KEY\\uDFFF\":\"SYNTHETIC_PRIVATE_VALUE\""), SidecarError.InvalidUnicode),
            (Document(profile, tag, ",\"SYNTHETIC_JSON_KEY\":\"SYNTHETIC_PRIVATE_VALUE\","), SidecarError.InvalidJson),
            (Document(profile, tag + ",false"), SidecarError.InvalidSchema),
            (Document(profile, tag + "," + JsonSerializer.Serialize(ChosenTag + new string('a', 4096))), SidecarError.TagTooLarge),
            (Document(profile, string.Join(",", Enumerable.Repeat(tag, PassiveSidecarCodec.MaxTags + 1))), SidecarError.TooManyTags)
        ];
        foreach (var item in cases)
            RedactedFailure(Parse(item.Json, profile), item.Error);

        byte[] valid = Encoding.UTF8.GetBytes(Document(profile, tag));
        RedactedFailure(PassiveSidecarCodec.Parse(valid.Append((byte)0xff).ToArray(), profile), SidecarError.InvalidUtf8);
        byte[] oversized = Enumerable.Repeat((byte)' ', PassiveSidecarCodec.MaxPayloadBytes + 1).ToArray();
        valid.CopyTo(oversized, 0);
        RedactedFailure(PassiveSidecarCodec.Parse(oversized, profile), SidecarError.PayloadTooLarge);
    }

    private static void RedactedFailure(SidecarParseResult result, SidecarError error)
    {
        Failed(result, error);
        string diagnostic = result.ToString() + JsonSerializer.Serialize(result);
        foreach (string forbidden in new[] { "SYNTHETIC_", "synthetic-user", "https:", "example.invalid", "Bearer", "/synthetic/", "Authorization", "file_url", "BytePositionInLine" })
            Assert.DoesNotContain(forbidden, diagnostic);
    }

    [Fact]
    public void ExplicitTagStringsArePreservedRatherThanSilentlyUrlFilteredOrRedacted()
    {
        // The contract rejects unknown sensitive fields, not arbitrary chosen tag text.
        // This control does not authorize discovering/exporting private metadata.
        string[] tags = [UserInfoUrl, SignedUrl, Bearer, PrivatePath];
        string tokens = string.Join(",", tags.Select(tag => JsonSerializer.Serialize(tag)));
        foreach (var profile in ImportProfiles)
        {
            var result = Parse(Document(profile, tokens), profile);
            Assert.True(result.Success);
            Assert.Equal(tags, result.Tags.ToArray());
        }
        var exported = PassiveSidecarCodec.SerializeChosenTags(tags);
        Assert.Equal(SidecarError.None, exported.Error);
        var reparsed = PassiveSidecarCodec.Parse(exported.Utf8.ToArray(), SidecarProfile.HydrusJsonTagsV1);
        Assert.True(reparsed.Success);
        Assert.Equal(tags, reparsed.Tags.ToArray());
    }
}
