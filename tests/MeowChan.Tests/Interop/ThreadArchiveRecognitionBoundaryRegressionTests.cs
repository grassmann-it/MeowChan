using System.Text;
using System.Text.Json;
using MeowChan.Interop;

namespace MeowChan.Tests.Interop;

/// <summary>
/// Synthetic in-memory structural hints only: no importer, provenance check,
/// producer-compatibility check, media availability check or Apply authority.
/// </summary>
public sealed class ThreadArchiveRecognitionBoundaryRegressionTests
{
    private const string FourChanPosts = "\"posts\":[{\"no\":101,\"resto\":0,\"tim\":9001}]";
    private const string YChanHeader = "\"Id\":\"synthetic-id\",\"Board\":\"synthetic-board\"," +
        "\"Url\":\"https://synthetic.invalid/thread\"";
    private const string YChanPosts = "\"ThreadPosts\":[{\"post_id\":201,\"files\":[null,null]}," +
        "{\"post_id\":202}]";

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void PayloadLimitCountsUtf8BytesIncludingInertWhitespace(int byteDelta)
    {
        byte[] json = Encoding.UTF8.GetBytes("{" + FourChanPosts + ",\"ignored\":\"猫😀\"}");
        byte[] payload = new byte[ThreadArchiveRecognition.MaxPayloadBytes + byteDelta];
        json.CopyTo(payload, 0);
        Array.Fill(payload, (byte)' ', json.Length, payload.Length - json.Length);
        Assert.True(Encoding.UTF8.GetCharCount(payload) < payload.Length);

        ThreadArchiveRecognitionResult result = ThreadArchiveRecognition.Inspect(payload);

        if (byteDelta > 0)
            AssertRejected(result, ThreadArchiveRecognitionError.PayloadTooLarge);
        else
            AssertCandidate(result, ThreadArchiveRecognitionFormat.FourChanApiJson,
                new(1, 1, 1));
    }

    [Fact]
    public void OnlySuppliedMemorySliceCountsNotOversizedInvalidBackingBytes()
    {
        byte[] json = Encoding.UTF8.GetBytes("{" + FourChanPosts + "}");
        byte[] backing = new byte[ThreadArchiveRecognition.MaxPayloadBytes + 1];
        Array.Fill(backing, (byte)0xff);
        json.CopyTo(backing, 3);

        ThreadArchiveRecognitionResult result = ThreadArchiveRecognition.Inspect(
            backing.AsMemory(3, json.Length));

        AssertCandidate(result, ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 1, 1));
    }

    [Fact]
    public void InvalidUtf8InsideIgnoredTextRejectsAnOtherwiseMatchingPrefixWithoutEcho()
    {
        const string marker = "SYNTHETIC-UTF8-MARKER";
        byte[] prefix = Encoding.UTF8.GetBytes("{" + FourChanPosts + ",\"ignored\":\"" + marker);
        byte[] suffix = Encoding.UTF8.GetBytes("\"}");
        // Truncated three-byte sequence, followed by the closing quote.
        byte[] payload = prefix.Concat(new byte[] { 0xe2, 0x82 }).Concat(suffix).ToArray();

        ThreadArchiveRecognitionResult result = ThreadArchiveRecognition.Inspect(payload);

        AssertRejected(result, ThreadArchiveRecognitionError.InvalidUtf8);
        Assert.DoesNotContain(marker, result.ToString());
        Assert.DoesNotContain(marker, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void InertObjectAndArrayDepthsIncludeTheRootContainer(bool objects, int excessDepth)
    {
        // One root object plus seven nested containers reaches depth eight.
        int nestedContainers = ThreadArchiveRecognition.MaxJsonDepth - 1 + excessDepth;
        string nested = "0";
        for (int i = 0; i < nestedContainers; i++)
            nested = objects ? "{\"child\":" + nested + "}" : "[" + nested + "]";
        string json = "{" + FourChanPosts + ",\"ignored\":" + nested + "}";

        ThreadArchiveRecognitionResult result = Inspect(json);

        if (excessDepth == 0)
            AssertCandidate(result, ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 1, 1));
        else
            AssertRejected(result, ThreadArchiveRecognitionError.InvalidJson);
    }

    [Theory]
    [InlineData(false, "a")]
    [InlineData(false, "猫")]
    [InlineData(false, "\\u0061")]
    [InlineData(true, "a")]
    [InlineData(true, "猫")]
    [InlineData(true, "\\u0061")]
    public void RawTokenByteLimitIsInclusiveForValuesAndPropertyNames(bool propertyName, string unit)
    {
        string exactToken = RawTokenWithByteLength(unit, ThreadArchiveRecognition.MaxTokenBytes);
        string Json(string token) => propertyName
            ? "{" + FourChanPosts + ",\"" + token + "\":0}"
            : "{" + FourChanPosts + ",\"ignored\":\"" + token + "\"}";
        string exact = Json(exactToken);
        Assert.True(Encoding.UTF8.GetByteCount(exact) < ThreadArchiveRecognition.MaxPayloadBytes);
        // Escapes count as their six raw bytes, not one decoded character.
        Assert.Equal(ThreadArchiveRecognition.MaxTokenBytes, Encoding.UTF8.GetByteCount(exactToken));

        AssertCandidate(Inspect(exact), ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 1, 1));
        AssertRejected(Inspect(Json(exactToken + "x")), ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Fact]
    public void InertNumberTokensHaveTheSameRawByteBudgetWithoutNumericConversion()
    {
        string Json(int length) => "{" + FourChanPosts + ",\"ignored\":" + new string('7', length) + "}";

        AssertCandidate(Inspect(Json(ThreadArchiveRecognition.MaxTokenBytes)),
            ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 1, 1));
        AssertRejected(Inspect(Json(ThreadArchiveRecognition.MaxTokenBytes + 1)),
            ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void GlobalTokenBudgetAlsoAppliesAfterAKnownSignature(int tokenDelta)
    {
        // Root/known post/ignored array contribute 16 tokens, including tim.
        int expectedTokens = ThreadArchiveRecognition.MaxTokens + tokenDelta;
        string json = "{" + FourChanPosts + ",\"ignored\":[" +
            string.Join(",", Enumerable.Repeat("0", expectedTokens - 16)) + "]}";
        byte[] payload = Encoding.UTF8.GetBytes(json);
        Assert.True(payload.Length < ThreadArchiveRecognition.MaxPayloadBytes);
        var reader = new Utf8JsonReader(payload,
            new JsonReaderOptions { MaxDepth = ThreadArchiveRecognition.MaxJsonDepth });
        int actualTokens = 0;
        while (reader.Read()) actualTokens++;
        Assert.Equal(expectedTokens, actualTokens);

        ThreadArchiveRecognitionResult result = ThreadArchiveRecognition.Inspect(payload);

        if (tokenDelta > 0)
            AssertRejected(result, ThreadArchiveRecognitionError.BudgetExceeded);
        else
            AssertCandidate(result, ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 1, 1));
    }

    [Fact]
    public void EscapedRequiredKeysAreDecodedBeforeMatching()
    {
        const string json = "{\"po\\u0073ts\":[{\"n\\u006f\":101,\"re\\u0073to\":0}]}";

        AssertCandidate(Inspect(json), ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 1, 0));
    }

    [Theory]
    [InlineData("{\"no\":101,\"n\\u006f\":102,\"resto\":0}")]
    [InlineData("{\"n\\u006f\":101,\"no\":102,\"resto\":0}")]
    [InlineData("{\"no\":101,\"resto\":0,\"tim\":1,\"t\\u0069m\":2}")]
    public void EscapedDuplicatePostKeysRejectRegardlessOfOrder(string post)
    {
        AssertRejected(Inspect("{\"posts\":[" + post + "]}"),
            ThreadArchiveRecognitionError.DuplicateProperty);
    }

    [Theory]
    [InlineData("{\"na\\u006de\":1,\"name\":2}")]
    [InlineData("[{\"😀\":1,\"\\uD83D\\uDE00\":2}]")]
    [InlineData("{\"child\":{\"\\u0061\":1,\"a\":2}}")]
    public void DuplicateDecodedKeysInInertNestedScopesStillReject(string ignored)
    {
        AssertRejected(Inspect("{" + FourChanPosts + ",\"ignored\":" + ignored + "}"),
            ThreadArchiveRecognitionError.DuplicateProperty);
    }

    [Fact]
    public void DuplicateTrackingIsObjectLocalOrdinalAndDoesNotNormalizeUnicode()
    {
        const string json = "{\"posts\":[{\"no\":101,\"resto\":0},{\"no\":102,\"resto\":101}]," +
            "\"ignored\":[{\"a\":1},{\"\\u0061\":2}," +
            "{\"a\":1,\"A\":2,\"é\":3,\"e\\u0301\":4}]}";

        AssertCandidate(Inspect(json), ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 2, 0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AmbiguitySuppressesDifferentCandidateCountsRegardlessOfPropertyOrder(bool reverse)
    {
        string yChan = YChanHeader + "," + YChanPosts;
        AssertCandidate(Inspect("{" + FourChanPosts + "}"),
            ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 1, 1));
        AssertCandidate(Inspect("{" + yChan + "}"),
            ThreadArchiveRecognitionFormat.YChanExJson, new(1, 2, 2));
        string json = reverse ? "{" + yChan + "," + FourChanPosts + "}"
            : "{" + FourChanPosts + "," + yChan + "}";

        AssertAmbiguous(Inspect(json));
    }

    [Fact]
    public void IncompleteSecondSignatureDoesNotCreateAnAmbiguousCandidate()
    {
        AssertCandidate(Inspect("{" + FourChanPosts + "," + YChanHeader +
            ",\"ThreadPosts\":[{\"post_id\":0}]}"),
            ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 1, 1));
        AssertCandidate(Inspect("{" + YChanHeader + "," + YChanPosts +
            ",\"posts\":[{\"no\":101,\"resto\":\"0\"}]}"),
            ThreadArchiveRecognitionFormat.YChanExJson, new(1, 2, 2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GlobalFailuresDoNotReturnAnOtherwiseAmbiguousPartialResult(bool invalidUnicode)
    {
        string ignored = invalidUnicode ? "\"ignored\":\"\\uD800\""
            : "\"ignored\":{\"x\":1,\"\\u0078\":2}";
        ThreadArchiveRecognitionError error = invalidUnicode
            ? ThreadArchiveRecognitionError.InvalidUnicode : ThreadArchiveRecognitionError.DuplicateProperty;

        AssertRejected(Inspect("{" + FourChanPosts + "," + YChanHeader + "," + YChanPosts +
            "," + ignored + "}"), error);
    }

    [Fact]
    public void LaterCandidateAttachmentOverflowRejectsRatherThanKeepingEarlierMatch()
    {
        string files = string.Join(",", Enumerable.Repeat("null", ThreadArchiveRecognition.MaxAttachments + 1));
        string json = "{" + FourChanPosts + "," + YChanHeader +
            ",\"ThreadPosts\":[{\"post_id\":201,\"files\":[" + files + "]}]}";

        AssertRejected(Inspect(json), ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Fact]
    public void InvalidLaterPostShapeDiscardsEarlierClaimsInsteadOfReturningPartialCounts()
    {
        AssertNoSignature(Inspect("{\"posts\":[{\"no\":101,\"resto\":0,\"tim\":9001}," +
            "{\"no\":102,\"resto\":999}]}"));
        AssertNoSignature(Inspect("{" + YChanHeader +
            ",\"ThreadPosts\":[{\"post_id\":101,\"files\":[null]},{\"post_id\":102,\"files\":null}]}"));
        AssertNoSignature(Inspect("[" + KurobaPost("site", "board", 101, 101, "null") +
            ",{\"postDescriptor\":null,\"_postImages\":[]}]"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void YChanPostBudgetIsInclusiveAndNeverReturnsPartialClaims(int excessPosts)
    {
        int postCount = ThreadArchiveRecognition.MaxPosts + excessPosts;
        string posts = string.Join(",", Enumerable.Range(1, postCount)
            .Select(i => "{\"post_id\":" + i + "}"));
        string json = "{" + YChanHeader + ",\"ThreadPosts\":[" + posts + "]}";

        ThreadArchiveRecognitionResult result = Inspect(json);

        if (excessPosts == 0)
            AssertCandidate(result, ThreadArchiveRecognitionFormat.YChanExJson, new(1, postCount, 0));
        else
            AssertRejected(result, ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    [InlineData(true, 1)]
    public void UnsupportedCandidateAttachmentBudgetIsCumulativeAcrossPosts(bool kuroba, int excess)
    {
        int firstCount = ThreadArchiveRecognition.MaxAttachments / 2;
        string firstFiles = string.Join(",", Enumerable.Repeat("null", firstCount));
        string secondFiles = string.Join(",", Enumerable.Repeat("null",
            ThreadArchiveRecognition.MaxAttachments - firstCount + excess));
        string json = kuroba
            ? "[" + KurobaPost("site", "board", 101, 101, firstFiles) + "," +
                KurobaPost("site", "board", 101, 102, secondFiles) + "]"
            : "{" + YChanHeader + ",\"ThreadPosts\":[{\"post_id\":101,\"files\":[" + firstFiles +
                "]},{\"post_id\":102,\"files\":[" + secondFiles + "]}]}";
        Assert.True(Encoding.UTF8.GetByteCount(json) < ThreadArchiveRecognition.MaxPayloadBytes);

        ThreadArchiveRecognitionResult result = Inspect(json);

        if (excess == 0)
            AssertCandidate(result, kuroba ? ThreadArchiveRecognitionFormat.KurobaExJson
                : ThreadArchiveRecognitionFormat.YChanExJson,
                new(1, 2, ThreadArchiveRecognition.MaxAttachments));
        else
            AssertRejected(result, ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Fact]
    public void KurobaThreadClaimsUseTheWholeObservedSiteBoardThreadTuple()
    {
        string json = "[" + string.Join(",", new[]
        {
            KurobaPost("site", "board", 101, 101, "null"),
            KurobaPost("site", "board", 101, 102, ""),
            KurobaPost("other-site", "board", 101, 103, "null,null"),
            KurobaPost("site", "other-board", 101, 104, ""),
            KurobaPost("site", "board", 201, 201, "null")
        }) + "]";

        AssertCandidate(Inspect(json), ThreadArchiveRecognitionFormat.KurobaExJson, new(4, 5, 4));
    }

    [Fact]
    public void YChanAttachmentCountsAreArrayClaimsNotValidatedFileObjectsOrAvailability()
    {
        string json = "{" + YChanHeader + ",\"ThreadPosts\":[{\"post_id\":101," +
            "\"files\":[null,false,\"synthetic-placeholder\",{}]},{\"post_id\":102}]}";

        AssertCandidate(Inspect(json), ThreadArchiveRecognitionFormat.YChanExJson, new(1, 2, 4));
    }

    [Fact]
    public void FourChanAttachmentClaimsAtPostLimitCountBothIndicatorsOnlyOnce()
    {
        string posts = string.Join(",", Enumerable.Range(1, ThreadArchiveRecognition.MaxPosts)
            .Select(i => "{\"no\":" + i + ",\"resto\":" + (i == 1 ? 0 : 1) +
                ",\"tim\":9001,\"filedeleted\":1}"));

        AssertCandidate(Inspect("{\"posts\":[" + posts + "]}"),
            ThreadArchiveRecognitionFormat.FourChanApiJson,
            new(1, ThreadArchiveRecognition.MaxPosts, ThreadArchiveRecognition.MaxPosts));
    }

    [Fact]
    public void AllStatusPathsReturnOnlyRedactedClassificationAndClaimCounts()
    {
        const string sentinel = "SYNTHETIC-REDACTION-MARKER";
        const string ignored = "\"ignored\":{\"SYNTHETIC-REDACTION-MARKER\":" +
            "\"<script>https://synthetic.invalid/?secret=SYNTHETIC-REDACTION-MARKER</script>" +
            " ../../SYNTHETIC-REDACTION-MARKER\"}";
        ThreadArchiveRecognitionResult recognized = Inspect("{" + FourChanPosts + "," + ignored + "}");
        ThreadArchiveRecognitionResult unsupported = Inspect("{" + YChanHeader + "," + YChanPosts +
            "," + ignored + "}");
        ThreadArchiveRecognitionResult unknown = Inspect("{" + ignored + "}");
        ThreadArchiveRecognitionResult ambiguous = Inspect("{" + FourChanPosts + "," +
            YChanHeader + "," + YChanPosts + "," + ignored + "}");
        ThreadArchiveRecognitionResult duplicate = Inspect("{" + FourChanPosts + "," + ignored +
            ",\"duplicate\":{\"x\":1,\"x\":2}}");
        ThreadArchiveRecognitionResult malformed = Inspect("{" + FourChanPosts + "," + ignored);
        ThreadArchiveRecognitionResult unicode = Inspect("{" + FourChanPosts + "," + ignored +
            ",\"invalid\":\"\\uD800\"}");
        ThreadArchiveRecognitionResult budget = Inspect("{" + FourChanPosts + "," + ignored +
            ",\"large\":\"" + new string('x', ThreadArchiveRecognition.MaxTokenBytes + 1) + "\"}");
        byte[] oversized = Encoding.UTF8.GetBytes("{" + ignored + "}" +
            new string(' ', ThreadArchiveRecognition.MaxPayloadBytes));
        ThreadArchiveRecognitionResult size = ThreadArchiveRecognition.Inspect(oversized);

        AssertCandidate(recognized, ThreadArchiveRecognitionFormat.FourChanApiJson, new(1, 1, 1));
        AssertCandidate(unsupported, ThreadArchiveRecognitionFormat.YChanExJson, new(1, 2, 2));
        AssertNoSignature(unknown);
        AssertAmbiguous(ambiguous);
        AssertRejected(duplicate, ThreadArchiveRecognitionError.DuplicateProperty);
        AssertRejected(malformed, ThreadArchiveRecognitionError.InvalidJson);
        AssertRejected(unicode, ThreadArchiveRecognitionError.InvalidUnicode);
        AssertRejected(budget, ThreadArchiveRecognitionError.BudgetExceeded);
        AssertRejected(size, ThreadArchiveRecognitionError.PayloadTooLarge);

        foreach (ThreadArchiveRecognitionResult result in new[]
            { recognized, unsupported, unknown, ambiguous, duplicate, malformed, unicode, budget, size })
        {
            // Inspect both record diagnostics and the serializable result fields.
            // This is not a public result-wire contract or a provenance assertion.
            foreach (string rendered in new[] { result.ToString(), JsonSerializer.Serialize(result) })
            {
                Assert.DoesNotContain(sentinel, rendered);
                Assert.DoesNotContain("synthetic.invalid", rendered);
                Assert.DoesNotContain("script", rendered);
                Assert.DoesNotContain("../", rendered);
                Assert.DoesNotContain("9001", rendered);
            }
        }
    }

    private static string RawTokenWithByteLength(string unit, int bytes)
    {
        int unitBytes = Encoding.UTF8.GetByteCount(unit);
        return string.Concat(Enumerable.Repeat(unit, bytes / unitBytes)) + new string('x', bytes % unitBytes);
    }

    private static string KurobaPost(string site, string board, int thread, int post, string images) =>
        "{\"postDescriptor\":{\"postNo\":" + post + ",\"postSubNo\":0,\"descriptor\":{\"threadNo\":" +
        thread + ",\"boardDescriptor\":{\"boardCode\":\"" + board +
        "\",\"siteDescriptor\":{\"site_name\":\"" + site + "\"}}}},\"_postImages\":[" + images + "]}";

    private static void AssertCandidate(ThreadArchiveRecognitionResult result,
        ThreadArchiveRecognitionFormat format, ThreadArchiveRecognitionCounts counts)
    {
        bool fourChan = format == ThreadArchiveRecognitionFormat.FourChanApiJson;
        Assert.Equal(fourChan ? ThreadArchiveRecognitionStatus.Recognized
            : ThreadArchiveRecognitionStatus.Unsupported, result.Status);
        Assert.Equal(fourChan, result.IsRecognized);
        Assert.Equal(format, result.Format);
        Assert.Equal(new[] { format }, result.Candidates.ToArray());
        Assert.Equal(fourChan ? ThreadArchiveRecognitionError.None
            : ThreadArchiveRecognitionError.UnsupportedFormat, result.Error);
        Assert.Equal(counts, result.Counts);
    }

    private static void AssertAmbiguous(ThreadArchiveRecognitionResult result)
    {
        Assert.Equal(ThreadArchiveRecognitionStatus.Ambiguous, result.Status);
        Assert.Equal(ThreadArchiveRecognitionError.AmbiguousSignature, result.Error);
        Assert.False(result.IsRecognized);
        Assert.Equal(ThreadArchiveRecognitionFormat.None, result.Format);
        Assert.Equal(new[] { ThreadArchiveRecognitionFormat.FourChanApiJson,
            ThreadArchiveRecognitionFormat.YChanExJson }, result.Candidates.ToArray());
        Assert.Equal(new ThreadArchiveRecognitionCounts(0, 0, 0), result.Counts);
    }

    private static void AssertNoSignature(ThreadArchiveRecognitionResult result) =>
        AssertEmptyResult(result, ThreadArchiveRecognitionStatus.Unsupported,
            ThreadArchiveRecognitionError.NoKnownSignature);

    private static void AssertRejected(ThreadArchiveRecognitionResult result,
        ThreadArchiveRecognitionError error) =>
        AssertEmptyResult(result, ThreadArchiveRecognitionStatus.Rejected, error);

    private static void AssertEmptyResult(ThreadArchiveRecognitionResult result,
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
