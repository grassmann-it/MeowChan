using System.Collections.Generic;
using System.Text;
using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

public sealed class DanbooruTagQueryTests
{
    [Fact]
    public void NormalizesOnlyAsciiSeparatorsAndPreservesSpellingPolarityAndOrder()
    {
        DanbooruTagQuery query = Parse(" \tCat\r\n-rating:sensitive   -dog  ");
        Assert.Equal("Cat -rating:sensitive -dog", query.ProviderQuery);
        Assert.Equal(2, query.TagTermCount);
        Assert.Equal(new[] { "Cat", "-rating:sensitive", "-dog" }, query.Terms.Select(term => term.Text));
        Assert.Equal(DanbooruTagTermKind.Rating, query.Terms[1].Kind);
        Assert.True(query.Terms[1].IsNegative);
        Assert.True(query.Terms[2].IsNegative);
        Assert.False(query.Terms[0].IsNegative);
    }

    [Theory]
    [InlineData("rating:g")]
    [InlineData("rating:general")]
    [InlineData("rating:sensitive")]
    [InlineData("rating:s")]
    [InlineData("rating:q")]
    [InlineData("rating:questionable")]
    [InlineData("rating:e")]
    [InlineData("rating:explicit")]
    [InlineData("rating:q,s")]
    [InlineData("-rating:g,s,q,e")]
    public void DoesNotRewriteDanbooruRatingAliasesListsOrExclusions(string text)
    {
        DanbooruTagQuery query = Parse(text);
        Assert.Equal(text, query.ProviderQuery);
        Assert.Equal(0, query.TagTermCount);
        Assert.Equal(DanbooruTagTermKind.Rating, Assert.Single(query.Terms).Kind);
    }

    [Theory]
    [InlineData("cat dog bird")]
    [InlineData("cat -dog -bird")]
    [InlineData("-cat -dog -bird")]
    [InlineData("cat cat cat")]
    public void ExclusionsAndDuplicateOccurrencesCannotBypassTwoTagBudget(string text) =>
        Reject(text, DanbooruTagQueryError.TooManyTagTerms);

    [Fact]
    public void TwoLiteralTagsPlusFreeRatingAreAcceptedWithoutInventingAnOrderMetatag()
    {
        DanbooruTagQuery query = Parse("cat -dog rating:g");
        Assert.Equal("cat -dog rating:g", query.ProviderQuery);
        Assert.Equal(2, query.TagTermCount);
        Assert.DoesNotContain("order:", query.ProviderQuery);
    }

    [Theory]
    [InlineData("cat or dog")]
    [InlineData("cat OR dog")]
    [InlineData("cat and dog")]
    [InlineData("not cat")]
    [InlineData("(cat dog)")]
    [InlineData("tag_(qualifier)")]
    [InlineData("~cat")]
    [InlineData("blue_*")]
    [InlineData("-*cat*")]
    [InlineData("source:\"two words\"")]
    [InlineData("cat\\ dog")]
    [InlineData("cat|dog")]
    [InlineData("cat%20dog")]
    [InlineData("+cat")]
    [InlineData("{cat}")]
    [InlineData("[cat]")]
    public void UnsupportedSyntaxIsRejectedRatherThanFlattenedEscapedOrDecoded(string text) =>
        Reject(text, DanbooruTagQueryError.UnsupportedExpression);

    [Theory]
    [InlineData("order:score")]
    [InlineData("order:id")]
    [InlineData("order:id_desc")]
    [InlineData("-order:id")]
    [InlineData("limit:9999")]
    [InlineData("random:20")]
    [InlineData("future_provider_key:value")]
    [InlineData("private:keep")]
    [InlineData("local:favorite")]
    [InlineData("RATING:g")]
    [InlineData(":g")]
    public void UnimplementedMetatagsAreNeverSilentlyDroppedOrAssumedFree(string text) =>
        Reject(text, DanbooruTagQueryError.UnsupportedMetaTag);

    [Theory]
    [InlineData("rating:")]
    [InlineData("rating:safe")]
    [InlineData("rating:unknown")]
    [InlineData("rating:G")]
    [InlineData("rating:g,")]
    [InlineData("rating:,g")]
    [InlineData("rating:g,,e")]
    [InlineData("rating:g:s")]
    [InlineData("rating:g,s,q,e,g")]
    public void InvalidRatingDoesNotBecomeAUniversalOrLocalTag(string text) =>
        Reject(text, DanbooruTagQueryError.InvalidRating);

    [Theory]
    [InlineData("rating:g rating:e")]
    [InlineData("rating:g rating:g")]
    [InlineData("rating:g -rating:g")]
    public void MultipleRatingsAreNotDeduplicatedOrRewrittenAsOneCommaList(string text) =>
        Reject(text, DanbooruTagQueryError.DuplicateRating);

    [Theory]
    [InlineData("-")]
    [InlineData("--cat")]
    public void InvalidNegationIsRejected(string text) => Reject(text, DanbooruTagQueryError.InvalidNegativeTerm);

    [Fact]
    public void RawInputBoundRunsBeforeScanningAndCountsWhitespaceToo()
    {
        Reject(new string(' ', DanbooruTagQuery.MaximumInputUtf16CodeUnits + 1), DanbooruTagQueryError.InputTooLong);
        Reject(new string('a', DanbooruTagQuery.MaximumInputUtf16CodeUnits + 1) + "\uD800", DanbooruTagQueryError.InputTooLong);
        string exact = "cat".PadRight(DanbooruTagQuery.MaximumQueryUtf8Bytes);
        Assert.Equal("cat", Parse(exact).ProviderQuery);
        Reject(exact + " ", DanbooruTagQueryError.InputTooLong);
    }

    [Fact]
    public void QueryUtf8BoundaryIsIndependentOfUtf16Length()
    {
        string term = new('é', DanbooruTagQuery.MaximumTermUtf8Bytes / 2);
        string exact = (term + " " + term + " rating:g").PadRight(768);
        Assert.Equal(DanbooruTagQuery.MaximumQueryUtf8Bytes, Encoding.UTF8.GetByteCount(exact));
        Assert.Equal(2, Parse(exact).TagTermCount);
        Reject(exact + " ", DanbooruTagQueryError.InputTooLong);
    }

    [Fact]
    public void TermBoundAcceptsExactBytesAndRejectsTheNextByte()
    {
        string ascii = new('a', DanbooruTagQuery.MaximumTermUtf8Bytes);
        Assert.Equal(ascii, Parse(ascii).ProviderQuery);
        Reject(ascii + "a", DanbooruTagQueryError.TermTooLong);
        string multibyte = new('é', DanbooruTagQuery.MaximumTermUtf8Bytes / 2);
        Assert.Equal(multibyte, Parse(multibyte).ProviderQuery);
        Reject(multibyte + "a", DanbooruTagQueryError.TermTooLong);
    }

    [Fact]
    public void DuplicateFreeRatingsAreRejectedBeforeAnyFourthToken() =>
        Reject("rating:g rating:s rating:q rating:e", DanbooruTagQueryError.DuplicateRating);

    [Fact]
    public void FourthTokenFailsWithoutReturningAPartialQuery() =>
        Reject("cat dog rating:g extra", DanbooruTagQueryError.TooManyTerms);

    [Theory]
    [InlineData("", 0xD800, -1)]
    [InlineData("cat", 0xDC00, -1)]
    [InlineData("", 0xD800, 0x78)]
    [InlineData("", 0xD800, 0xD800)]
    public void InvalidUnicodeIsRejectedWithoutReplacement(string prefix, int unit, int suffix)
    {
        // Construct lone surrogates at execution time: attribute/test-discovery
        // serialization can replace malformed UTF-16 before it reaches the parser.
        string text = prefix + (char)unit + (suffix < 0 ? string.Empty : ((char)suffix).ToString());
        Assert.Contains(text, char.IsSurrogate);
        Reject(text, DanbooruTagQueryError.InvalidUnicode);
    }

    [Theory]
    [InlineData("cat\0")]
    [InlineData("\u0001cat")]
    [InlineData("cat\u007F")]
    [InlineData("cat\u200B")]
    [InlineData("cat\u202E")]
    [InlineData("cat\uFEFF")]
    [InlineData("cat\u00A0dog")]
    [InlineData("cat\u2028dog")]
    [InlineData("cat\u000Bdog")]
    public void NonSeparatorControlsFormatsAndUnicodeWhitespaceDoNotVanish(string text) =>
        Reject(text, DanbooruTagQueryError.InvalidCharacter);

    [Fact]
    public void UnicodeSpellingIsNotCaseFoldedOrNormalizedLikePrivateTagNames()
    {
        string decomposed = "A\u030A";
        Assert.Equal(decomposed, Parse(decomposed).ProviderQuery);
        Assert.NotEqual(Parse("Å").ProviderQuery, Parse(decomposed).ProviderQuery);
        Assert.Equal("\U00010400", Parse("\U00010400").ProviderQuery); // supplementary Unicode letter
        Assert.Equal("I_İ_ı", Parse("I_İ_ı").ProviderQuery);
    }

    [Fact]
    public void NullAndBlankDoNotBecomeUnboundedAllPostsQueries()
    {
        Reject(null, DanbooruTagQueryError.NullInput);
        Reject("", DanbooruTagQueryError.Empty);
        Reject(" \t\r\n ", DanbooruTagQueryError.Empty);
    }

    [Fact]
    public void SuccessfulOutputIsIdempotentAndTermsCannotBeMutated()
    {
        DanbooruTagQuery first = Parse(" cat\t-dog rating:g ");
        DanbooruTagQuery second = Parse(first.ProviderQuery);
        Assert.Equal(first.ProviderQuery, second.ProviderQuery);
        Assert.Equal(first.Terms, second.Terms);
        IList<DanbooruTagTerm> exposed = Assert.IsAssignableFrom<IList<DanbooruTagTerm>>(first.Terms);
        Assert.Throws<NotSupportedException>(() => exposed[0] = new("changed", DanbooruTagTermKind.Tag, false));
        Assert.Equal("cat -dog rating:g", first.ProviderQuery);
    }

    [Fact]
    public void ParsingOnlyUsesTheExplicitStringAndHasNoPrivateTagInputs()
    {
        // This is not a privacy classifier: explicit text may itself be sensitive.
        // What the pure seam guarantees is no automatic lookup/merge of local tags.
        Assert.Equal("synthetic_public_tag", Parse("synthetic_public_tag").ProviderQuery);
        Assert.Equal("synthetic_private_word", Parse("synthetic_private_word").ProviderQuery);
        Assert.DoesNotContain(typeof(DanbooruTagQuery).Assembly.GetType("MeowChan.Tags.PrivateTagSet")!,
            typeof(DanbooruTagQuery).GetMethods(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                .SelectMany(method => method.GetParameters()).Select(parameter => parameter.ParameterType));
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
