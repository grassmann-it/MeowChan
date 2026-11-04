using System.Text;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

// Pure parser tests: synthetic public-query text only, no requests or activation.
public sealed class DanbooruQueryAdversarialRegressionTests
{
    [Fact]
    public void RatingPositionAndTagPolarityRoundTripAcrossEveryAsciiSeparator()
    {
        foreach (string separator in new[] { " ", "\t", "\r", "\n", " \t\r\n " })
        foreach (string first in new[] { "Public_Tag", "-Public_Tag" })
        foreach (string second in new[] { "other.tag", "-other.tag" })
        foreach (string rating in new[] { "rating:general,s,q,e", "-rating:general,s,q,e" })
        foreach (int ratingPosition in new[] { 0, 1, 2 })
        {
            var tokens = new List<string> { first, second };
            tokens.Insert(ratingPosition, rating);
            string canonical = string.Join(" ", tokens);
            DanbooruTagQuery query = Parse(separator + string.Join(separator, tokens) + separator);
            Assert.Equal(canonical, query.ProviderQuery);
            Assert.Equal(2, query.TagTermCount);
            Assert.Equal(tokens, query.Terms.Select(term => term.Text));
            Assert.Equal(DanbooruTagTermKind.Rating, query.Terms[ratingPosition].Kind);
            Assert.Equal(tokens.Select(token => token.StartsWith('-')), query.Terms.Select(term => term.IsNegative));
            DanbooruTagQuery roundTrip = Parse(query.ProviderQuery);
            Assert.Equal(query.ProviderQuery, roundTrip.ProviderQuery);
            Assert.Equal(query.TagTermCount, roundTrip.TagTermCount);
            Assert.Equal(query.Terms, roundTrip.Terms);
        }
    }

    [Theory]
    [InlineData("cat!dog")]
    [InlineData("cat#dog")]
    [InlineData("cat$dog")]
    [InlineData("cat&dog")]
    [InlineData("cat;dog")]
    [InlineData("cat/dog")]
    [InlineData("cat?dog")]
    [InlineData("cat=dog")]
    [InlineData("cat'dog")]
    [InlineData("cat,dog")]
    [InlineData("%252Dcat")]
    [InlineData("cat%2520dog")]
    public void PunctuationAndDoubleEncodingAreNotRepairedIntoLiteralOrMultipleTags(string text) =>
        Reject(text, DanbooruTagQueryError.UnsupportedExpression);

    [Theory]
    [InlineData("rating%3Ag")]
    [InlineData("%72ating:g")]
    [InlineData("-Rating:g")]
    [InlineData("rating::g")]
    [InlineData("rating:g%2Ce")]
    [InlineData("id:>100")]
    [InlineData("page:2")]
    public void MetatagAndValueEncodingCannotBypassTheExactKeyAndValueGrammar(string text)
    {
        DanbooruTagQueryError expected = text switch
        {
            "rating%3Ag" => DanbooruTagQueryError.UnsupportedExpression,
            "rating::g" or "rating:g%2Ce" => DanbooruTagQueryError.InvalidRating,
            _ => DanbooruTagQueryError.UnsupportedMetaTag
        };
        Reject(text, expected);
    }

    [Theory]
    [InlineData("rating:g..e")]
    [InlineData("rating:>=g")]
    [InlineData("rating:!g")]
    [InlineData("rating:g+s")]
    [InlineData("rating:g/e")]
    [InlineData("rating:g;rating:e")]
    [InlineData("-rating:g,-e")]
    public void RatingRangesComparisonsAndEmbeddedOperatorsDoNotBecomeRatingLists(string text) =>
        Reject(text, DanbooruTagQueryError.InvalidRating);

    [Theory]
    [InlineData("ｒａｔｉｎｇ:g", nameof(DanbooruTagQueryError.UnsupportedMetaTag))]
    [InlineData("rating：g", nameof(DanbooruTagQueryError.UnsupportedExpression))]
    [InlineData("rating:ｇ", nameof(DanbooruTagQueryError.InvalidRating))]
    [InlineData("－cat", nameof(DanbooruTagQueryError.UnsupportedExpression))]
    [InlineData("cat\u200Ddog", nameof(DanbooruTagQueryError.InvalidCharacter))]
    [InlineData("cat\u2060dog", nameof(DanbooruTagQueryError.InvalidCharacter))]
    public void CompatibilityLookalikesAndInvisibleJoinersAreNotNormalizedOrRemoved(string text, string error) =>
        Reject(text, Enum.Parse<DanbooruTagQueryError>(error));

    [Fact]
    public void MaximumUtf16LengthCanStillExceedTheUtf8BudgetByOneByte()
    {
        // Whitespace avoids accidentally testing the independent term-size cap.
        string over = new string(' ', DanbooruTagQuery.MaximumInputUtf16CodeUnits - 1) + "é";
        Assert.Equal(DanbooruTagQuery.MaximumInputUtf16CodeUnits, over.Length);
        Assert.Equal(DanbooruTagQuery.MaximumQueryUtf8Bytes + 1, Encoding.UTF8.GetByteCount(over));
        Reject(over, DanbooruTagQueryError.InputTooLong);

        string exact = new string(' ', DanbooruTagQuery.MaximumQueryUtf8Bytes - 2) + "é";
        Assert.Equal(DanbooruTagQuery.MaximumQueryUtf8Bytes, Encoding.UTF8.GetByteCount(exact));
        Assert.Equal("é", Parse(exact).ProviderQuery);
    }

    [Fact]
    public void CombiningSequencesUseOriginalBytesRatherThanAShorterNormalizedSpelling()
    {
        // e + acute is three bytes; NFC would shorten each pair to two bytes.
        string decomposed = string.Concat(Enumerable.Repeat("e\u0301", 85));
        string exact = decomposed + "a";
        Assert.Equal(DanbooruTagQuery.MaximumTermUtf8Bytes, Encoding.UTF8.GetByteCount(exact));
        Assert.Equal(exact, Parse(exact).ProviderQuery);
        Assert.NotEqual(exact, Parse(exact).ProviderQuery.Normalize(NormalizationForm.FormC));
        string over = decomposed + "aa";
        Assert.True(Encoding.UTF8.GetByteCount(over.Normalize(NormalizationForm.FormC)) < DanbooruTagQuery.MaximumTermUtf8Bytes);
        Reject(over, DanbooruTagQueryError.TermTooLong);
    }

    [Theory]
    [InlineData(0x0000, nameof(DanbooruTagQueryError.InvalidCharacter))]
    [InlineData(0xD800, nameof(DanbooruTagQueryError.InvalidUnicode))]
    public void WholeInputInspectionRejectsAnUnsafeSuffixBeforeReportingAnUtf8BudgetFailure(int unit, string error)
    {
        // Within the O(1) UTF-16 guard, but already over the UTF-8 limit. Inspect
        // must still validate the complete input instead of returning a prefix.
        string prefix = new string('é', DanbooruTagQuery.MaximumQueryUtf8Bytes / 2 - 1) + "\U00010400";
        Assert.Equal(DanbooruTagQuery.MaximumQueryUtf8Bytes + 2, Encoding.UTF8.GetByteCount(prefix));
        string text = prefix + (char)unit;
        Assert.True(text.Length <= DanbooruTagQuery.MaximumInputUtf16CodeUnits);
        Reject(text, Enum.Parse<DanbooruTagQueryError>(error));
    }

    [Theory]
    [InlineData("--cat")]
    [InlineData("order:id")]
    [InlineData("rating:unknown")]
    public void AFullThreeTermQueryRejectsAFourthTokenBeforeItsOwnGrammar(string fourth)
    {
        Reject("cat -dog rating:g " + fourth, DanbooruTagQueryError.TooManyTerms);
        // The prefix remains valid on its own, but no partial query is returned.
        Assert.Equal("cat -dog rating:g", Parse("cat -dog rating:g").ProviderQuery);
    }

    [Fact]
    public void TermSizeIsCheckedBeforeMetatagGrammarOrThirdTagOccurrence()
    {
        string oversizedMeta = "order:" + new string('a', DanbooruTagQuery.MaximumTermUtf8Bytes);
        Reject(oversizedMeta, DanbooruTagQueryError.TermTooLong);
        string oversizedTag = new('a', DanbooruTagQuery.MaximumTermUtf8Bytes + 1);
        Reject("cat dog " + oversizedTag, DanbooruTagQueryError.TermTooLong);
        Reject("cat dog rating:g " + oversizedTag, DanbooruTagQueryError.TooManyTerms);
    }

    [Fact]
    public void FailedParseDoesNotPoisonASubsequentSuccessfulParseUsingTheSameOutVariables()
    {
        Assert.True(DanbooruTagQuery.TryParse("cat -dog rating:g", out DanbooruTagQuery? query, out DanbooruTagQueryError error));
        DanbooruTagQuery first = Assert.IsType<DanbooruTagQuery>(query);
        Assert.False(DanbooruTagQuery.TryParse("rating:g..e", out query, out error));
        Assert.Null(query);
        Assert.Equal(DanbooruTagQueryError.InvalidRating, error);
        Assert.True(DanbooruTagQuery.TryParse("rating:e", out query, out error));
        Assert.Equal(DanbooruTagQueryError.None, error);
        DanbooruTagQuery recovered = Assert.IsType<DanbooruTagQuery>(query);
        Assert.NotSame(first, recovered);
        Assert.Equal("rating:e", recovered.ProviderQuery);
        Assert.Equal(0, recovered.TagTermCount);
        Assert.Single(recovered.Terms);
        Assert.Equal("cat -dog rating:g", first.ProviderQuery);
        Assert.Equal(3, first.Terms.Count);
    }

    private static DanbooruTagQuery Parse(string text)
    {
        Assert.True(DanbooruTagQuery.TryParse(text, out DanbooruTagQuery? query, out DanbooruTagQueryError error), error.ToString());
        Assert.Equal(DanbooruTagQueryError.None, error);
        return Assert.IsType<DanbooruTagQuery>(query);
    }

    private static void Reject(string? text, DanbooruTagQueryError expected)
    {
        Assert.False(DanbooruTagQuery.TryParse(text, out DanbooruTagQuery? query, out DanbooruTagQueryError error));
        Assert.Null(query);
        Assert.Equal(expected, error);
    }
}
