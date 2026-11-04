using System.Text;
using System.Text.Json;
using MeowChan.Interop;

namespace MeowChan.Tests;

/// <summary>
/// Offline structural-recognition regressions, not foreign producer fixtures or import tests.
/// Inspect accepts bytes only: filename/extension claims below are inert input, not routing hints.
/// </summary>
public sealed class ForeignRecognitionAdversarialRegressionTests
{
    private const string FourChanFields = "\"posts\":[{\"no\":101,\"resto\":0,\"tim\":9001}]";
    private const string YChanFields = "\"Id\":\"synthetic\",\"Board\":\"g\"," +
        "\"Url\":\"https://synthetic.invalid/thread\",\"ThreadPosts\":[{\"post_id\":201}]";
    private const string KurobaFields = "\"postDescriptor\":{\"postNo\":301,\"postSubNo\":0," +
        "\"descriptor\":{\"threadNo\":301,\"boardDescriptor\":{\"boardCode\":\"g\"," +
        "\"siteDescriptor\":{\"site_name\":\"synthetic\"}}}},\"_postImages\":[]";

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "\n")]
    [InlineData(true, "")]
    [InlineData(true, "\n")]
    public void TwoCompleteCompetingDocumentsAreNeitherFirstMatchNorAmbiguity(bool reverse, string separator)
    {
        string first = "{" + (reverse ? YChanFields : FourChanFields) + "}";
        string second = "{" + (reverse ? FourChanFields : YChanFields) + "}";

        // Unlike two signatures in ONE object, concatenated/JSONL roots are invalid JSON.
        AssertRejected(Inspect(first + separator + second), ThreadArchiveRecognitionError.InvalidJson);
    }

    [Fact]
    public void NestedCompetingObjectsAreNotHoistedIntoRootCandidates()
    {
        AssertNoSignature(Inspect("[{" + FourChanFields + "},{" + YChanFields + "}]"));

        // All three field sets occur, but only the root-array Kuroba signature applies.
        ThreadArchiveRecognitionResult result = Inspect("[{" + KurobaFields + "," +
            FourChanFields + "," + YChanFields + "}]");

        AssertUnsupportedCandidate(result, ThreadArchiveRecognitionFormat.KurobaExJson);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Utf16AndUtf32AreNotTranscodedIntoMatchingUtf8Json(bool utf32, bool bigEndian)
    {
        Encoding encoding = utf32
            ? new UTF32Encoding(bigEndian, byteOrderMark: false, throwOnInvalidCharacters: true)
            : new UnicodeEncoding(bigEndian, byteOrderMark: false, throwOnInvalidBytes: true);
        byte[] payload = encoding.GetBytes("{" + FourChanFields + "}");

        // These ASCII-plus-NUL bytes are valid UTF-8, but not UTF-8 JSON. No BOM is involved.
        _ = new UTF8Encoding(false, true).GetCharCount(payload);
        AssertRejected(ThreadArchiveRecognition.Inspect(payload), ThreadArchiveRecognitionError.InvalidJson);
    }

    [Theory]
    [InlineData("\\x41")]
    [InlineData("\\u12G4")]
    [InlineData("\0")]
    [InlineData("\n")]
    public void InvalidJsonStringSyntaxAfterCompetingSignaturesCannotReturnPartialCandidates(string invalidText)
    {
        string json = "{" + FourChanFields + "," + YChanFields +
            ",\"ignored\":\"" + invalidText + "\"}";

        AssertRejected(Inspect(json), ThreadArchiveRecognitionError.InvalidJson);
    }

    [Theory]
    [InlineData("\u00a0")]
    [InlineData("\u200b")]
    [InlineData("\ufeff")]
    public void NonJsonUnicodeWhitespaceAfterACompleteMatchIsNotTrimmedAway(string suffix)
    {
        // The UTF-8 BOM check applies only at byte zero; a suffix is invalid JSON instead.
        AssertRejected(Inspect("{" + FourChanFields + "}" + suffix),
            ThreadArchiveRecognitionError.InvalidJson);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void OversizedEarlierCandidateCannotBeSkippedForALaterValidSignature(bool reverse, int excessPosts)
    {
        // Keep the array syntactically valid but structurally nonmatching. The post budget
        // must be checked before inspecting its first null, not bypassed by that null.
        string fourChan = "\"posts\":[" + string.Join(",", Enumerable.Repeat("null",
            ThreadArchiveRecognition.MaxPosts + excessPosts)) + "]";
        string json = "{" + (reverse ? YChanFields + "," + fourChan : fourChan + "," + YChanFields) + "}";
        byte[] payload = Encoding.UTF8.GetBytes(json);
        Assert.True(payload.Length < ThreadArchiveRecognition.MaxPayloadBytes);

        ThreadArchiveRecognitionResult result = ThreadArchiveRecognition.Inspect(payload);

        if (excessPosts == 0)
            AssertUnsupportedCandidate(result, ThreadArchiveRecognitionFormat.YChanExJson);
        else
            AssertRejected(result, ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void KurobaRootArrayBudgetIsCheckedEvenWhenItsFirstElementHasNoSignature(int excessPosts)
    {
        string json = "[" + string.Join(",", Enumerable.Repeat("null",
            ThreadArchiveRecognition.MaxPosts + excessPosts)) + "]";

        if (excessPosts == 0)
            AssertNoSignature(Inspect(json));
        else
            AssertRejected(Inspect(json), ThreadArchiveRecognitionError.BudgetExceeded);
    }

    [Theory]
    [InlineData("thread_data.json", "KurobaEx")]
    [InlineData("101.thread.json", "YChanEx")]
    [InlineData("101.json", "FourChanApiJson")]
    [InlineData("synthetic.chan.zip", "BASC")]
    public void FilenameExtensionAndSelfDeclaredConfidenceCannotSubstituteForAStructuralSignature(
        string filename, string format)
    {
        string claims = "\"filename\":" + JsonSerializer.Serialize(filename) +
            ",\"format\":" + JsonSerializer.Serialize(format) + ",\"confidence\":\"high\"";

        // Not a mock file API: Inspect receives only these JSON bytes, never filename.
        AssertNoSignature(Inspect("{" + claims + "}"));

        // A real known-but-unsupported signature cannot be promoted by the same claims.
        AssertUnsupportedCandidate(Inspect("{" + YChanFields + "," + claims + "}"),
            ThreadArchiveRecognitionFormat.YChanExJson);
    }

    private static void AssertUnsupportedCandidate(ThreadArchiveRecognitionResult result,
        ThreadArchiveRecognitionFormat format)
    {
        Assert.Equal(ThreadArchiveRecognitionStatus.Unsupported, result.Status);
        Assert.False(result.IsRecognized);
        Assert.Equal(ThreadArchiveRecognitionError.UnsupportedFormat, result.Error);
        Assert.Equal(format, result.Format);
        Assert.Equal(new[] { format }, result.Candidates.ToArray());
        Assert.Equal(new ThreadArchiveRecognitionCounts(1, 1, 0), result.Counts);
    }

    private static void AssertNoSignature(ThreadArchiveRecognitionResult result) =>
        AssertEmpty(result, ThreadArchiveRecognitionStatus.Unsupported, ThreadArchiveRecognitionError.NoKnownSignature);

    private static void AssertRejected(ThreadArchiveRecognitionResult result, ThreadArchiveRecognitionError error) =>
        AssertEmpty(result, ThreadArchiveRecognitionStatus.Rejected, error);

    private static void AssertEmpty(ThreadArchiveRecognitionResult result,
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
