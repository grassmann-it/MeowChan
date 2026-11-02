using System.Text;
using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

public sealed class PrivateTagNameTests
{
    [Fact]
    public void TrimsEdgesAndNormalizesDisplayToNfc()
    {
        PrivateTagName decomposed = Create("  A\u030A b  ");
        PrivateTagName precomposed = Create("\u00C5 b");

        Assert.Equal("\u00C5 b", decomposed.Display);
        Assert.Equal(precomposed.Display, decomposed.Display);
        Assert.Equal(precomposed, decomposed);
        Assert.Equal(precomposed.GetHashCode(), decomposed.GetHashCode());
    }

    [Fact]
    public void CaseComparisonUsesInvariantKeyWhilePreservingEachDisplay()
    {
        PrivateTagName lower = Create("cat");
        PrivateTagName upper = Create("CAT");
        PrivateTagName mixed = Create("Cat");

        Assert.Equal("cat", lower.Display);
        Assert.Equal("CAT", upper.Display);
        Assert.Equal("Cat", mixed.Display);
        Assert.Equal("CAT", lower.Key);
        Assert.Equal(lower, upper);
        Assert.Equal(lower, mixed);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
    }

    [Fact]
    public void TurkishCaseComparisonRemainsInvariantRatherThanTurkishLocaleSensitive()
    {
        PrivateTagName asciiLower = Create("i");
        PrivateTagName asciiUpper = Create("I");
        PrivateTagName dotlessLower = Create("\u0131");
        PrivateTagName dottedUpper = Create("\u0130");

        // Fixed expectations for simple invariant casing, not Turkish locale casing.
        Assert.Equal("I", asciiLower.Key);
        Assert.Equal("I", asciiUpper.Key);
        Assert.Equal("\u0131", dotlessLower.Key);
        Assert.Equal("\u0130", dottedUpper.Key);
        Assert.Equal(asciiLower, asciiUpper);
        Assert.NotEqual(asciiLower, dotlessLower);
        Assert.NotEqual(asciiUpper, dotlessLower);
        Assert.NotEqual(asciiLower, dottedUpper);
        Assert.NotEqual(dotlessLower, dottedUpper);
    }

    [Fact]
    public void GermanSharpSPreservesDisplayWithoutOverclaimingCaseFolding()
    {
        PrivateTagName lower = Create("stra\u00DFe");
        PrivateTagName upper = Create("STRASSE");

        // Only the display contract is asserted here. The implementation stays at
        // ToUpperInvariant + NFC; it adds no locale or custom multi-character folding promise.
        Assert.Equal("stra\u00DFe", lower.Display);
        Assert.Equal("STRASSE", upper.Display);
        Assert.NotEqual(lower.Display, upper.Display);
    }

    [Fact]
    public void InteriorWhitespaceIsPreservedAndDistinctWithoutCollapsing()
    {
        PrivateTagName single = Create(" cat toy ");
        PrivateTagName multiple = Create("cat  toy");

        Assert.Equal("cat toy", single.Display);
        Assert.Equal("cat  toy", multiple.Display);
        Assert.NotEqual(single, multiple);
    }

    [Fact]
    public void ScalarLimitCountsSupplementaryScalarsAndUtf8BudgetBoundaryPasses()
    {
        PrivateTagName maximumAscii = Create(new string('a', PrivateTagName.MaximumScalars));
        PrivateTagName maximumSupplementary = Create(string.Concat(Enumerable.Repeat("\U0001F600", PrivateTagName.MaximumScalars)));

        Assert.Equal(PrivateTagName.MaximumScalars, maximumAscii.Display.Length);
        Assert.Equal(PrivateTagName.MaximumUtf8Bytes, Encoding.UTF8.GetByteCount(maximumSupplementary.Display));
        Rejects(new string('a', PrivateTagName.MaximumScalars + 1), TagNameError.TooManyScalars);
        Rejects(string.Concat(Enumerable.Repeat("\U0001F600", PrivateTagName.MaximumScalars + 1)), TagNameError.TooManyScalars);
    }

    [Fact]
    public void RawUtf16InputCapFailsBeforeScanningAndNormalization()
    {
        Rejects(new string('a', PrivateTagName.MaximumUtf16CodeUnits + 1), TagNameError.InputTooLong);
        // Even valid prefix material cannot buy another scan when the raw input is oversized.
        Rejects(new string('a', 16_384), TagNameError.InputTooLong);
    }

    [Fact]
    public void ControlAndFormatScalarsAreRejectedEvenWhenTrimWouldRemoveThem()
    {
        Rejects(null, TagNameError.Null);
        Rejects(string.Empty, TagNameError.Blank);
        Rejects(" \u00A0 ", TagNameError.Blank);
        Rejects("\ta", TagNameError.InvalidCharacter);
        Rejects("a\n", TagNameError.InvalidCharacter);
        Rejects("\ra", TagNameError.InvalidCharacter);
        Rejects("a\u2028", TagNameError.InvalidCharacter);
        Rejects("a\u2029b", TagNameError.InvalidCharacter);
        Rejects("a\u007Fb", TagNameError.InvalidCharacter);
        Rejects("\u200Ea", TagNameError.InvalidCharacter);
        Rejects("a\u202Eb", TagNameError.InvalidCharacter);
        Rejects("a\u2066b", TagNameError.InvalidCharacter);
        Rejects("a\u061Cb", TagNameError.InvalidCharacter);
    }

    [Fact]
    public void UnpairedSurrogatesAreRejectedButValidSupplementaryScalarsAreAccepted()
    {
        Rejects("\uD800a", TagNameError.InvalidUnicode);
        Rejects("a\uDFFF", TagNameError.InvalidUnicode);
        Rejects("\uD800\uD800", TagNameError.InvalidUnicode);

        PrivateTagName supplementary = Create("\uD83D\uDE00");
        Assert.Equal("\U0001F600", supplementary.Display);
    }

    [Fact]
    public void RejectedNamesDoNotEscapeThroughExceptions()
    {
        string[] untrusted =
        [
            null!,
            " ",
            "\tSECRET\n",
            "SECRET\u200E",
            "\uD800SECRET",
            new string('S', PrivateTagName.MaximumUtf16CodeUnits + 1)
        ];

        foreach (string candidate in untrusted)
        {
            Exception? failure = Record.Exception(() => PrivateTagName.TryCreate(candidate, out _, out _));
            Assert.Null(failure);
        }
    }

    private static PrivateTagName Create(string candidate)
    {
        bool created = PrivateTagName.TryCreate(candidate, out PrivateTagName? tagName, out TagNameError error);

        Assert.True(created);
        Assert.Equal(TagNameError.None, error);
        return tagName!;
    }

    private static void Rejects(string? candidate, TagNameError expected)
    {
        Exception? failure = Record.Exception(() => PrivateTagName.TryCreate(candidate, out _, out _));

        Assert.Null(failure);
        bool created = PrivateTagName.TryCreate(candidate, out PrivateTagName? tagName, out TagNameError error);
        Assert.False(created);
        Assert.Null(tagName);
        Assert.Equal(expected, error);
    }
}
