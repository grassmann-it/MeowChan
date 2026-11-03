using System.Text;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

public sealed class DanbooruTagQueryBoundaryRegressionTests
{
    [Theory]
    [InlineData("\u754C", false)]
    [InlineData("\u754C", true)]
    [InlineData("\U00010400", false)]
    [InlineData("\U00010400", true)]
    public void ThreeAndFourByteTermBoundariesIncludeTheExclusionOperator(string scalar, bool negative)
    {
        int prefixBytes = negative ? 1 : 0;
        int scalarBytes = Encoding.UTF8.GetByteCount(scalar);
        int available = DanbooruTagQuery.MaximumTermUtf8Bytes - prefixBytes;
        string body = string.Concat(Enumerable.Repeat(scalar, available / scalarBytes))
            + new string('a', available % scalarBytes);
        string exact = (negative ? "-" : string.Empty) + body;
        Assert.Equal(DanbooruTagQuery.MaximumTermUtf8Bytes, Encoding.UTF8.GetByteCount(exact));
        Assert.True(exact.Length < DanbooruTagQuery.MaximumTermUtf8Bytes);

        DanbooruTagQuery query = Parse(exact);
        DanbooruTagTerm term = Assert.Single(query.Terms);
        Assert.Equal(exact, query.ProviderQuery);
        Assert.Equal(exact, term.Text);
        Assert.Equal(negative, term.IsNegative);
        Assert.Equal(DanbooruTagTermKind.Tag, term.Kind);
        Assert.Equal(1, query.TagTermCount);
        Reject(exact + "a", DanbooruTagQueryError.TermTooLong);
    }

    [Fact]
    public void AddingExclusionToAnOtherwiseFullAsciiTagExceedsTheTermBudget()
    {
        string body = new('a', DanbooruTagQuery.MaximumTermUtf8Bytes - 1);
        Assert.Equal("-" + body, Parse("-" + body).ProviderQuery);
        Assert.Equal(body + "a", Parse(body + "a").ProviderQuery);
        Reject("-" + body + "a", DanbooruTagQueryError.TermTooLong);
    }

    [Theory]
    [InlineData("\u0915\u093E")] // SpacingCombiningMark
    [InlineData("a\u20DD")] // EnclosingMark
    [InlineData("\U0001D7CE")] // Supplementary decimal digit, not a letter or symbol
    public void OtherAllowedUnicodeCategoriesRemainLiteralTags(string text)
    {
        DanbooruTagQuery query = Parse(text);
        Assert.Equal(text, query.ProviderQuery);
        DanbooruTagTerm term = Assert.Single(query.Terms);
        Assert.Equal(text, term.Text);
        Assert.Equal(DanbooruTagTermKind.Tag, term.Kind);
        Assert.False(term.IsNegative);
        Assert.Equal(1, query.TagTermCount);
    }

    [Theory]
    [InlineData("cat\U000E0001")]
    [InlineData("rating:g\U000E0020")]
    [InlineData("cat\u0085dog")]
    [InlineData("cat\u000Cdog")]
    public void SupplementaryFormatsAndRemainingWhitespaceControlsAreNotSeparators(string text) =>
        Reject(text, DanbooruTagQueryError.InvalidCharacter);

    [Theory]
    [InlineData("cat\U0001F600")]
    [InlineData("\u2212cat")]
    public void ValidUnicodeSymbolsAreNotRepairedIntoLiteralTagsOrAsciiNegation(string text) =>
        Reject(text, DanbooruTagQueryError.UnsupportedExpression);

    [Theory]
    [InlineData(0xD800, false)]
    [InlineData(0xDC00, false)]
    [InlineData(0xD800, true)]
    [InlineData(0xDC00, true)]
    public void MalformedUnicodeAfterAFullValidPrefixRejectsTheWholeInput(int unit, bool appendValidPair)
    {
        // Keep invalid UTF-16 out of attribute/discovery serialization. In the true
        // case a valid supplementary letter follows, but cannot repair the lone unit.
        string suffix = ((char)unit).ToString() + (appendValidPair ? "\U00010400" : string.Empty);
        Reject("cat -dog rating:g " + suffix, DanbooruTagQueryError.InvalidUnicode);
    }

    [Theory]
    [InlineData("cat--dog", false)]
    [InlineData("-cat--dog", true)]
    public void InteriorDoubleHyphensDoNotCreateAnotherExclusion(string text, bool negative)
    {
        DanbooruTagQuery query = Parse(text);
        DanbooruTagTerm term = Assert.Single(query.Terms);
        Assert.Equal(text, query.ProviderQuery);
        Assert.Equal(text, term.Text);
        Assert.Equal(negative, term.IsNegative);
        Assert.Equal(1, query.TagTermCount);
    }

    [Theory]
    [InlineData("--rating:g", nameof(DanbooruTagQueryError.InvalidNegativeTerm))]
    [InlineData("-oR", nameof(DanbooruTagQueryError.UnsupportedExpression))]
    [InlineData("-AnD", nameof(DanbooruTagQueryError.UnsupportedExpression))]
    [InlineData("-NoT", nameof(DanbooruTagQueryError.UnsupportedExpression))]
    public void ExclusionCannotHideDoubleNegationOrBooleanKeywords(string text, string error) =>
        Reject(text, Enum.Parse<DanbooruTagQueryError>(error));

    [Theory]
    [InlineData("cat cat rating:g", false, false)]
    [InlineData("-cat -cat rating:g", true, true)]
    [InlineData("cat -cat rating:g", false, true)]
    [InlineData("-cat cat rating:g", true, false)]
    public void TwoOccurrencesArePreservedRatherThanDeduplicatedOrCancelled(
        string text, bool firstNegative, bool secondNegative)
    {
        DanbooruTagQuery query = Parse(text);
        Assert.Equal(text, query.ProviderQuery);
        Assert.Equal(2, query.TagTermCount);
        Assert.Equal(text.Split(' '), query.Terms.Select(term => term.Text));
        Assert.Equal(new[] { DanbooruTagTermKind.Tag, DanbooruTagTermKind.Tag, DanbooruTagTermKind.Rating },
            query.Terms.Select(term => term.Kind));
        Assert.Equal(new[] { firstNegative, secondNegative, false }, query.Terms.Select(term => term.IsNegative));
    }

    [Theory]
    [InlineData("rating:g,g,g,g", false)]
    [InlineData("rating:general,g,sensitive,s", false)]
    [InlineData("-rating:explicit,e,questionable,q", true)]
    public void FourRatingEntriesMayRepeatOrUseAliasesWithoutCanonicalization(string text, bool negative)
    {
        DanbooruTagQuery query = Parse(text);
        DanbooruTagTerm term = Assert.Single(query.Terms);
        Assert.Equal(text, query.ProviderQuery);
        Assert.Equal(text, term.Text);
        Assert.Equal(DanbooruTagTermKind.Rating, term.Kind);
        Assert.Equal(negative, term.IsNegative);
        Assert.Equal(0, query.TagTermCount);
        // The local cap counts entries, not distinct ratings, even for aliases.
        Reject(text + ",g", DanbooruTagQueryError.InvalidRating);
    }

    [Theory]
    [InlineData("-rating:g rating:general")]
    [InlineData("-rating:q -rating:questionable")]
    [InlineData("rating:g,s -cat -rating:e")]
    public void ASecondRatingIsRejectedAcrossAliasesPolarityAndInterveningTags(string text) =>
        Reject(text, DanbooruTagQueryError.DuplicateRating);

    [Theory]
    [InlineData("-rating:G")]
    [InlineData("-rating:g,,e")]
    [InlineData("rating:g,\t")]
    public void NegativeRatingsAndWhitespaceDoNotRelaxTheValueGrammar(string text) =>
        Reject(text, DanbooruTagQueryError.InvalidRating);

    [Theory]
    [InlineData("rating:g cat -cat cat", nameof(DanbooruTagQueryError.TooManyTerms))]
    [InlineData("cat -cat cat rating:g", nameof(DanbooruTagQueryError.TooManyTagTerms))]
    public void OccurrenceBudgetErrorsFollowTheExistingTokenOrder(string text, string error) =>
        Reject(text, Enum.Parse<DanbooruTagQueryError>(error));

    [Fact]
    public void EveryFailureClearsAPreviousQueryAndExposesOnlyAConstantDiagnostic()
    {
        const string marker = "synthetic_private_marker_09";
        // Check the actual out-parameter seam, not transport/log redaction or
        // whether successfully parsed text is public. All data here is synthetic.
        (string? Text, DanbooruTagQueryError Error)[] failures =
        [
            (null, DanbooruTagQueryError.NullInput),
            (" \t\r\n", DanbooruTagQueryError.Empty),
            (marker.PadRight(DanbooruTagQuery.MaximumInputUtf16CodeUnits + 1), DanbooruTagQueryError.InputTooLong),
            (marker + (char)0xD800, DanbooruTagQueryError.InvalidUnicode),
            (marker + "\0", DanbooruTagQueryError.InvalidCharacter),
            (marker + " -dog rating:g extra", DanbooruTagQueryError.TooManyTerms),
            (marker + " " + new string('a', DanbooruTagQuery.MaximumTermUtf8Bytes + 1), DanbooruTagQueryError.TermTooLong),
            (marker + " --dog", DanbooruTagQueryError.InvalidNegativeTerm),
            (marker + " +dog", DanbooruTagQueryError.UnsupportedExpression),
            (marker + " private:synthetic_secret", DanbooruTagQueryError.UnsupportedMetaTag),
            (marker + " -rating:synthetic_secret", DanbooruTagQueryError.InvalidRating),
            (marker + " rating:g -rating:e", DanbooruTagQueryError.DuplicateRating),
            (marker + " -dog third", DanbooruTagQueryError.TooManyTagTerms)
        ];

        foreach (var failure in failures)
        {
            Assert.True(DanbooruTagQuery.TryParse(marker, out DanbooruTagQuery? query,
                out DanbooruTagQueryError error));
            Assert.NotNull(query);
            Assert.Equal(marker, query.ProviderQuery);
            Assert.Equal(DanbooruTagQueryError.None, error);

            Assert.False(DanbooruTagQuery.TryParse(failure.Text, out query, out error));
            Assert.Null(query);
            Assert.Equal(failure.Error, error);
            Assert.DoesNotContain(marker, error.ToString());
            Assert.DoesNotContain("synthetic_secret", error.ToString());
        }
    }

    private static DanbooruTagQuery Parse(string text)
    {
        Assert.True(DanbooruTagQuery.TryParse(text, out DanbooruTagQuery? query,
            out DanbooruTagQueryError error), error.ToString());
        Assert.Equal(DanbooruTagQueryError.None, error);
        return Assert.IsType<DanbooruTagQuery>(query);
    }

    private static void Reject(string text, DanbooruTagQueryError expected)
    {
        Assert.False(DanbooruTagQuery.TryParse(text, out DanbooruTagQuery? query,
            out DanbooruTagQueryError error));
        Assert.Null(query);
        Assert.Equal(expected, error);
    }
}
