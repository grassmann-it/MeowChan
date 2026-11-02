using System.Text;
using System.Text.Json;
using MeowChan.Interop;

namespace MeowChan.Tests.Interop;

public sealed class ThreadArchiveRecognitionTests
{
    [Fact]
    public void RecognizesSyntheticFourChanApiJsonAndOnlyReportsCounts()
    {
        const string json = "{\"posts\":[" +
            "{\"no\":101,\"resto\":0,\"filename\":\"synthetic.jpg\",\"tim\":9001}," +
            "{\"no\":102,\"resto\":101,\"com\":\"synthetic\"}" +
            "]}";

        ThreadArchiveRecognitionResult result = Inspect(json);

        Assert.Equal(ThreadArchiveRecognitionStatus.Recognized, result.Status);
        Assert.True(result.IsRecognized);
        Assert.Equal(ThreadArchiveRecognitionFormat.FourChanApiJson, result.Format);
        Assert.Equal(new[] { ThreadArchiveRecognitionFormat.FourChanApiJson }, result.Candidates.ToArray());
        Assert.Equal(ThreadArchiveRecognitionError.None, result.Error);
        Assert.Equal(new ThreadArchiveRecognitionCounts(1, 2, 1), result.Counts);
    }

    [Fact]
    public void KurobaSignatureIsKnownButExplicitlyUnsupported()
    {
        const string json = "[{" +
            "\"postDescriptor\":{" +
                "\"postNo\":102," +
                "\"postSubNo\":0," +
                "\"descriptor\":{" +
                    "\"threadNo\":101," +
                    "\"boardDescriptor\":{" +
                        "\"boardCode\":\"g\",\"siteDescriptor\":{\"site_name\":\"4chan\"}" +
                    "}" +
                "}" +
            "}," +
            "\"_postImages\":[{}],\"timestamp\":1700000000" +
        "}]";

        ThreadArchiveRecognitionResult result = Inspect(json);

        Assert.Equal(ThreadArchiveRecognitionStatus.Unsupported, result.Status);
        Assert.Equal(ThreadArchiveRecognitionFormat.KurobaExJson, result.Format);
        Assert.Equal(ThreadArchiveRecognitionError.UnsupportedFormat, result.Error);
        Assert.Equal(new[] { ThreadArchiveRecognitionFormat.KurobaExJson }, result.Candidates.ToArray());
        Assert.Equal(new ThreadArchiveRecognitionCounts(1, 1, 1), result.Counts);
    }

    [Fact]
    public void YChanSignatureIsKnownButExplicitlyUnsupported()
    {
        const string json = "{" +
            "\"Id\":\"101\",\"Board\":\"g\",\"Url\":\"https://synthetic.invalid/g/101\"," +
            "\"ThreadPosts\":[{" +
                "\"post_id\":101,\"first\":true,\"files\":[{\"file_id\":\"9001\"}]" +
            "}]" +
        "}";

        ThreadArchiveRecognitionResult result = Inspect(json);

        Assert.Equal(ThreadArchiveRecognitionStatus.Unsupported, result.Status);
        Assert.Equal(ThreadArchiveRecognitionFormat.YChanExJson, result.Format);
        Assert.Equal(ThreadArchiveRecognitionError.UnsupportedFormat, result.Error);
        Assert.Equal(new[] { ThreadArchiveRecognitionFormat.YChanExJson }, result.Candidates.ToArray());
        Assert.Equal(new ThreadArchiveRecognitionCounts(1, 1, 1), result.Counts);
    }

    [Fact]
    public void MatchingTwoSignaturesIsAmbiguousAndProducesNoCountsToApply()
    {
        const string json = "{" +
            "\"Id\":\"101\",\"Board\":\"g\",\"Url\":\"https://synthetic.invalid/g/101\"," +
            "\"ThreadPosts\":[{\"post_id\":101}]," +
            "\"posts\":[{\"no\":101,\"resto\":0}]" +
        "}";

        ThreadArchiveRecognitionResult result = Inspect(json);

        Assert.Equal(ThreadArchiveRecognitionStatus.Ambiguous, result.Status);
        Assert.Equal(ThreadArchiveRecognitionFormat.None, result.Format);
        Assert.Equal(ThreadArchiveRecognitionError.AmbiguousSignature, result.Error);
        Assert.Equal(new[]
        {
            ThreadArchiveRecognitionFormat.FourChanApiJson,
            ThreadArchiveRecognitionFormat.YChanExJson
        }, result.Candidates.ToArray());
        Assert.Equal(new ThreadArchiveRecognitionCounts(0, 0, 0), result.Counts);
    }

    [Fact]
    public void ValidUnknownJsonIsExplicitlyUnsupported()
    {
        ThreadArchiveRecognitionResult result = Inspect("{\"posts\":[]}");

        Assert.Equal(ThreadArchiveRecognitionStatus.Unsupported, result.Status);
        Assert.Equal(ThreadArchiveRecognitionFormat.None, result.Format);
        Assert.Equal(ThreadArchiveRecognitionError.NoKnownSignature, result.Error);
        Assert.Empty(result.Candidates.ToArray());
        Assert.Equal(new ThreadArchiveRecognitionCounts(0, 0, 0), result.Counts);
    }

    [Fact]
    public void AMalformedKnownLookingDocumentIsRejectedNotPartiallyCounted()
    {
        ThreadArchiveRecognitionResult result = Inspect("{\"posts\":[{\"no\":101,\"resto\":0},]}");

        Assert.Equal(ThreadArchiveRecognitionStatus.Rejected, result.Status);
        Assert.Equal(ThreadArchiveRecognitionFormat.None, result.Format);
        Assert.Equal(ThreadArchiveRecognitionError.InvalidJson, result.Error);
        Assert.Empty(result.Candidates.ToArray());
        Assert.Equal(new ThreadArchiveRecognitionCounts(0, 0, 0), result.Counts);
    }

    [Fact]
    public void DuplicatePropertiesAreRejectedWithoutEchoingInput()
    {
        ThreadArchiveRecognitionResult result = Inspect("{\"posts\":[{\"no\":101,\"no\":102,\"resto\":0}]}");

        Assert.Equal(ThreadArchiveRecognitionStatus.Rejected, result.Status);
        Assert.Equal(ThreadArchiveRecognitionError.DuplicateProperty, result.Error);
        Assert.DoesNotContain("101", result.ToString());
        Assert.DoesNotContain("102", result.ToString());
    }

    [Fact]
    public void PayloadBomUtf8AndDepthAreBounded()
    {
        byte[] tooLarge = Encoding.UTF8.GetBytes("{\"unknown\":\"" + new string('x', ThreadArchiveRecognition.MaxPayloadBytes) + "\"}");
        Assert.Equal(ThreadArchiveRecognitionError.PayloadTooLarge, ThreadArchiveRecognition.Inspect(tooLarge).Error);
        Assert.Equal(ThreadArchiveRecognitionError.BomNotAllowed,
            ThreadArchiveRecognition.Inspect(Encoding.UTF8.GetBytes("\uFEFF{\"posts\":[]}")).Error);
        Assert.Equal(ThreadArchiveRecognitionError.InvalidUtf8,
            ThreadArchiveRecognition.Inspect(new byte[] { 0xff, 0xfe }).Error);

        string nested = "{\"unknown\":" + new string('[', ThreadArchiveRecognition.MaxJsonDepth + 1) + "0" +
            new string(']', ThreadArchiveRecognition.MaxJsonDepth + 1) + "}";
        Assert.Equal(ThreadArchiveRecognitionError.InvalidJson, Inspect(nested).Error);
    }

    [Fact]
    public void InvalidPostShapeDoesNotBecomeARecognizedFourChanArchive()
    {
        ThreadArchiveRecognitionResult result = Inspect("{\"posts\":[{\"no\":101,\"resto\":\"0\"}]}");

        Assert.Equal(ThreadArchiveRecognitionStatus.Unsupported, result.Status);
        Assert.Equal(ThreadArchiveRecognitionError.NoKnownSignature, result.Error);
        Assert.Empty(result.Candidates.ToArray());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("101")]
    [InlineData("\"posts\"")]
    [InlineData("[]")]
    [InlineData("{\"threads\":[{\"posts\":[{\"no\":101,\"resto\":0}]}]}")]
    [InlineData("{\"posts\":null}")]
    [InlineData("{\"posts\":{}}")]
    [InlineData("{\"posts\":[null]}")]
    [InlineData("{\"posts\":[{}]}")]
    [InlineData("{\"posts\":[{\"no\":101}]}")]
    [InlineData("{\"posts\":[{\"no\":0,\"resto\":0}]}")]
    [InlineData("{\"posts\":[{\"no\":102,\"resto\":101}]}")]
    [InlineData("{\"posts\":[{\"no\":101,\"resto\":0},{\"no\":102,\"resto\":0}]}")]
    [InlineData("{\"posts\":[{\"no\":101,\"resto\":0},{\"no\":102,\"resto\":999}]}")]
    [InlineData("{\"posts\":[{\"no\":101,\"resto\":0},{\"no\":101,\"resto\":101}]}")]
    public void UnknownShapesCatalogsAndInconsistentThreadsDoNotMatch(string json)
    {
        var result = Inspect(json);
        AssertFailure(result, ThreadArchiveRecognitionStatus.Unsupported, ThreadArchiveRecognitionError.NoKnownSignature);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0")]
    [InlineData("101.0")]
    [InlineData("1.01e2")]
    [InlineData("18446744073709551616")]
    [InlineData("\"101\"")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("{}")]
    public void IdsMustBeExactBoundedPositiveIntegerTokens(string id)
    {
        AssertFailure(Inspect("{\"posts\":[{\"no\":" + id + ",\"resto\":0}]}"),
            ThreadArchiveRecognitionStatus.Unsupported, ThreadArchiveRecognitionError.NoKnownSignature);
    }

    [Fact]
    public void UInt64IdBoundDoesNotRoundOrExposeIds()
    {
        var result = Inspect("{\"posts\":[{\"no\":18446744073709551615,\"resto\":0}," +
            "{\"no\":9007199254740993,\"resto\":18446744073709551615}]}");
        Assert.True(result.IsRecognized);
        Assert.Equal(new ThreadArchiveRecognitionCounts(1, 2, 0), result.Counts);
        Assert.DoesNotContain("18446744073709551615", result.ToString());
    }

    [Theory]
    [InlineData("\"filedeleted\":0", 0)]
    [InlineData("\"filedeleted\":1", 1)]
    [InlineData("\"tim\":123", 1)]
    [InlineData("\"tim\":123,\"filedeleted\":1", 1)]
    [InlineData("\"filename\":\"synthetic\",\"ext\":\".jpg\"", 0)]
    public void CountsAttachmentClaimsNotLocalFiles(string fields, int expected)
    {
        var result = Inspect("{\"posts\":[{\"no\":101,\"resto\":0," + fields + "}]}");
        Assert.True(result.IsRecognized);
        Assert.Equal(expected, result.Counts.AttachmentClaims);
    }

    [Theory]
    [InlineData("\"tim\":0")]
    [InlineData("\"tim\":1.0")]
    [InlineData("\"tim\":\"123\"")]
    [InlineData("\"tim\":null")]
    [InlineData("\"filedeleted\":2")]
    [InlineData("\"filedeleted\":true")]
    public void WrongClaimTypesDoNotBecomeAnAttachmentCount(string fields)
    {
        AssertFailure(Inspect("{\"posts\":[{\"no\":101,\"resto\":0," + fields + "}]}"),
            ThreadArchiveRecognitionStatus.Unsupported, ThreadArchiveRecognitionError.NoKnownSignature);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"posts\":[]} {}")]
    [InlineData("/*synthetic*/{}")]
    [InlineData("{\"posts\":[],}")]
    [InlineData("PK\u0003\u0004synthetic-zip-not-json")]
    [InlineData("<html>synthetic</html>")]
    public void NonJsonAndContainerBytesAreNotExtractedOrPartiallyAccepted(string input)
    {
        AssertFailure(Inspect(input), ThreadArchiveRecognitionStatus.Rejected, ThreadArchiveRecognitionError.InvalidJson);
    }

    [Theory]
    [InlineData("{\"posts\":[],\"p\\u006fsts\":[]}")]
    [InlineData("{\"posts\":[{\"no\":101,\"resto\":0}],\"ignored\":{\"x\":1,\"x\":2}}")]
    public void DuplicateDecodedKeysAlsoFailInIgnoredFields(string json)
    {
        AssertFailure(Inspect(json), ThreadArchiveRecognitionStatus.Rejected, ThreadArchiveRecognitionError.DuplicateProperty);
    }

    [Theory]
    [InlineData("{\"posts\":[{\"no\":101,\"resto\":0,\"com\":\"\\uD800\"}]}")]
    [InlineData("{\"ignored\":[\"\\uDC00\"]}")]
    [InlineData("{\"ignored\\uD800\":0}")]
    [InlineData("{\"ignored\":\"\\uD800\\uD800\"}")]
    public void LoneSurrogatesAreRejectedInAllStringsAndPropertyNames(string json)
    {
        AssertFailure(Inspect(json), ThreadArchiveRecognitionStatus.Rejected, ThreadArchiveRecognitionError.InvalidUnicode);
    }

    [Fact]
    public void ValidUnicodeLiteralBackslashAndRawUtf8AreHandledWithoutEcho()
    {
        var result = Inspect("{\"posts\":[{\"no\":101,\"resto\":0,\"com\":\"猫\\uD83D\\uDE00\\\\uD800\"}]}");
        Assert.True(result.IsRecognized);
        foreach (byte[] invalid in new[] { new byte[] { 0xc0, 0xaf }, new byte[] { 0xed, 0xa0, 0x80 },
            new byte[] { 0xf4, 0x90, 0x80, 0x80 }, new byte[] { 0xe2, 0x82 } })
            AssertFailure(ThreadArchiveRecognition.Inspect(invalid), ThreadArchiveRecognitionStatus.Rejected,
                ThreadArchiveRecognitionError.InvalidUtf8);
    }

    [Fact]
    public void ExactPayloadAndTokenBoundsAreInclusive()
    {
        byte[] exact = Enumerable.Repeat((byte)' ', ThreadArchiveRecognition.MaxPayloadBytes).ToArray();
        Encoding.UTF8.GetBytes("{\"posts\":[{\"no\":101,\"resto\":0}]}").CopyTo(exact, 0);
        Assert.True(ThreadArchiveRecognition.Inspect(exact).IsRecognized);
        AssertFailure(ThreadArchiveRecognition.Inspect(exact.Append((byte)' ').ToArray()),
            ThreadArchiveRecognitionStatus.Rejected, ThreadArchiveRecognitionError.PayloadTooLarge);

        string prefix = "{\"posts\":[{\"no\":101,\"resto\":0,\"com\":\"";
        Assert.True(Inspect(prefix + new string('x', ThreadArchiveRecognition.MaxTokenBytes) + "\"}]}").IsRecognized);
        AssertFailure(Inspect(prefix + new string('x', ThreadArchiveRecognition.MaxTokenBytes + 1) + "\"}]}"),
            ThreadArchiveRecognitionStatus.Rejected, ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void TokenBudgetIsInclusiveIndependentlyOfPostBudgets(int excessTokens)
    {
        int expectedTokens = ThreadArchiveRecognition.MaxTokens + excessTokens;
        // No post signature: the root object bypasses all post-array budgets.
        // StartObject + PropertyName + StartArray + numbers + EndArray + EndObject = numbers + 5.
        byte[] payload = Encoding.UTF8.GetBytes("{\"unknown\":[" +
            string.Join(",", Enumerable.Repeat("0", expectedTokens - 5)) + "]}");
        Assert.True(payload.Length < ThreadArchiveRecognition.MaxPayloadBytes);

        var reader = new Utf8JsonReader(payload,
            new JsonReaderOptions { MaxDepth = ThreadArchiveRecognition.MaxJsonDepth });
        int actualTokens = 0;
        while (reader.Read()) actualTokens++;
        Assert.Equal(expectedTokens, actualTokens);

        ThreadArchiveRecognitionResult result = ThreadArchiveRecognition.Inspect(payload);
        if (excessTokens == 0)
            AssertFailure(result, ThreadArchiveRecognitionStatus.Unsupported,
                ThreadArchiveRecognitionError.NoKnownSignature);
        else
            AssertFailure(result, ThreadArchiveRecognitionStatus.Rejected,
                ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Fact]
    public void ExactPostAndAttachmentBudgetsAreInclusiveAndProduceNoPartialCountsOnOverflow()
    {
        string Post(int i) => "{\"no\":" + (i + 1) + ",\"resto\":" + (i == 0 ? "0" : "1") + "}";
        string exactPosts = "{\"posts\":[" + string.Join(",", Enumerable.Range(0, ThreadArchiveRecognition.MaxPosts).Select(Post)) + "]}";
        Assert.Equal(ThreadArchiveRecognition.MaxPosts, Inspect(exactPosts).Counts.PostClaims);
        string tooManyPosts = "{\"posts\":[" + string.Join(",", Enumerable.Range(0, ThreadArchiveRecognition.MaxPosts + 1).Select(Post)) + "]}";
        AssertFailure(Inspect(tooManyPosts), ThreadArchiveRecognitionStatus.Rejected, ThreadArchiveRecognitionError.BudgetExceeded);

        string YChanFiles(int count) => "{\"Id\":\"101\",\"Board\":\"g\",\"Url\":\"https://synthetic.invalid/\"," +
            "\"ThreadPosts\":[{\"post_id\":101,\"files\":[" + string.Join(",", Enumerable.Repeat("{}", count)) + "]}]}";
        Assert.Equal(ThreadArchiveRecognition.MaxAttachments, Inspect(YChanFiles(ThreadArchiveRecognition.MaxAttachments)).Counts.AttachmentClaims);
        AssertFailure(Inspect(YChanFiles(ThreadArchiveRecognition.MaxAttachments + 1)),
            ThreadArchiveRecognitionStatus.Rejected, ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Fact]
    public void RecognitionIsInertAndDoesNotReturnForeignContentOrClaimMediaAvailability()
    {
        const string json = "{\"posts\":[{\"no\":101,\"resto\":0,\"tim\":123," +
            "\"com\":\"<script>SECRET</script>\",\"url\":\"https://synthetic:SECRET@example.invalid/?token=SECRET\"," +
            "\"path\":\"../../SECRET\"}],\"unknown\":{\"SECRET\":true}}";
        var result = Inspect(json);
        Assert.True(result.IsRecognized); // Signature match is NOT a content/URL safety verdict.
        Assert.Equal(new ThreadArchiveRecognitionCounts(1, 1, 1), result.Counts);
        Assert.DoesNotContain("SECRET", result.ToString());
        Assert.DoesNotContain("script", result.ToString());
        Assert.DoesNotContain("https:", result.ToString());
        Assert.DoesNotContain("../", result.ToString());
    }

    private static void AssertFailure(ThreadArchiveRecognitionResult result,
        ThreadArchiveRecognitionStatus status, ThreadArchiveRecognitionError error)
    {
        Assert.Equal(status, result.Status);
        Assert.Equal(error, result.Error);
        Assert.False(result.IsRecognized);
        Assert.Equal(ThreadArchiveRecognitionFormat.None, result.Format);
        Assert.Empty(result.Candidates);
        Assert.Equal(new ThreadArchiveRecognitionCounts(0, 0, 0), result.Counts);
    }

    private static ThreadArchiveRecognitionResult Inspect(string json) =>
        ThreadArchiveRecognition.Inspect(Encoding.UTF8.GetBytes(json));
}
