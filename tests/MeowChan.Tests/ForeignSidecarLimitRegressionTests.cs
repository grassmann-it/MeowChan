using System.Text;
using MeowChan.Interop.Sidecars;

namespace MeowChan.Tests;

public sealed class ForeignSidecarLimitRegressionTests
{
    // Exercise both actual import schemas; no recognizer, file discovery or client execution.
    private static SidecarProfile Profile(bool gallery) => gallery
        ? SidecarProfile.GalleryDlDanbooruIdTagsV1 : SidecarProfile.HydrusJsonTagsV1;

    private static string Document(bool gallery, string tokens) => gallery
        ? "{\"id\":101,\"tags\":[" + tokens + "]}"
        : "{\"metadata\":{\"tags\":[" + tokens + "]}}";

    private static SidecarParseResult Parse(bool gallery, string json) =>
        PassiveSidecarCodec.Parse(Encoding.UTF8.GetBytes(json), Profile(gallery));

    private static void Failed(SidecarParseResult result, SidecarError error)
    {
        Assert.False(result.Success);
        Assert.Equal(error, result.Error);
        Assert.Empty(result.Tags);
        Assert.Null(result.ClaimId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FullTagCountCanContainAMaximumDecodedStringButNeitherBudgetCanBeExceeded(bool gallery)
    {
        // Three-byte BMP scalars plus one ASCII byte: 1366 UTF-16 units, 4096 UTF-8 bytes.
        // Literal UTF-8 (not serializer escapes) keeps this well below the payload cap.
        string maximum = new string('猫', 1365) + "a";
        Assert.Equal(4096, Encoding.UTF8.GetByteCount(maximum));
        string prefix = string.Join(",", Enumerable.Repeat("\"\"", 4095));
        string tokens = prefix + ",\"" + maximum + "\"";
        var accepted = Parse(gallery, Document(gallery, tokens));
        Assert.True(accepted.Success);
        Assert.Equal(4096, accepted.Tags.Length);
        Assert.All(accepted.Tags.Take(4095), tag => Assert.Equal("", tag));
        Assert.Equal(maximum, accepted.Tags[4095]);
        Assert.Equal(gallery ? (long?)101 : null, accepted.ClaimId);

        // Late string failure must not expose the already materialized 4095-tag prefix or id.
        Failed(Parse(gallery, Document(gallery, prefix + ",\"" + maximum + "a\"")), SidecarError.TagTooLarge);
        Failed(Parse(gallery, Document(gallery, tokens + ",\"\"")), SidecarError.TooManyTags);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ContentFilledPayloadBoundaryIsIndependentOfIndividualTagAndCountBudgets(bool gallery)
    {
        const int limit = 1024 * 1024;
        const int count = 256;
        // Account independently for schema envelope, quotes and commas. No whitespace padding.
        int overhead = Encoding.UTF8.GetByteCount(Document(gallery, "")) + 2 * count + count - 1;
        int tailLength = limit - overhead - 255 * 4096;
        Assert.InRange(tailLength, 1, 4095);
        string prefix = string.Join(",", Enumerable.Repeat("\"" + new string('x', 4096) + "\"", 255));
        foreach (int delta in new[] { -1, 0, 1 })
        {
            string tail = new string('y', tailLength + delta);
            string json = Document(gallery, prefix + ",\"" + tail + "\"");
            Assert.Equal(limit + delta, Encoding.UTF8.GetByteCount(json));
            var result = Parse(gallery, json);
            if (delta > 0)
            {
                Failed(result, SidecarError.PayloadTooLarge);
                continue;
            }
            Assert.True(result.Success);
            Assert.Equal(count, result.Tags.Length);
            Assert.All(result.Tags.Take(255), tag => Assert.Equal(new string('x', 4096), tag));
            Assert.Equal(tail, result.Tags[255]);
            Assert.Equal(gallery ? (long?)101 : null, result.ClaimId);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DepthEightReachesSchemaRejectionButDepthNineFailsJsonEvenInLateUnknownMembers(
        bool gallery, bool objectContainers)
    {
        Assert.Equal(8, PassiveSidecarCodec.MaxDepth);
        foreach (int depth in new[] { 7, 8, 9 })
        {
            // The root occupies depth one. Neither unknown fields nor wrong tag types
            // may bypass full-document token validation. All JSON is otherwise well formed.
            string nested = "0";
            for (int i = 1; i < depth; i++)
                nested = objectContainers ? "{\"child\":" + nested + "}" : "[" + nested + "]";
            string valid = Document(gallery, "\"synthetic_prefix\"");
            string json = valid[..^1] + ",\"unknown\":" + nested + "}";
            Failed(Parse(gallery, json), depth <= 8 ? SidecarError.InvalidSchema : SidecarError.InvalidJson);
        }
    }
}
