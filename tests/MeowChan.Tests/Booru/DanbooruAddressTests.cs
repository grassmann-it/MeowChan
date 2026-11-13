using MeowChan.Booru;

namespace MeowChan.Tests.Booru;

public sealed class DanbooruAddressTests
{
    private static DanbooruAddress Parse(string text)
    {
        Assert.True(DanbooruAddress.TryParse(text, out DanbooruAddress? address), text);
        return address!;
    }

    [Theory]
    [InlineData("https://danbooru.donmai.us/posts/123", "123", false)]
    [InlineData("https://danbooru.donmai.us/posts/123/", "123", false)]
    [InlineData("https://danbooru.donmai.us/posts/123?q=cat+solo", "123", false)]
    [InlineData("https://danbooru.donmai.us/posts/123?q=rating:g,s#comments", "123", false)]
    [InlineData("  HTTPS://Danbooru.Donmai.us/posts/9007199254740993  ", "9007199254740993", false)]
    [InlineData("https://safebooru.donmai.us/posts/456", "456", true)]
    [InlineData("https://safebooru.donmai.us/posts/456?q=cat", "456", true)]
    public void PostAddressYieldsThePostIdAndDropsQueryAndFragment(string text, string id, bool safebooru)
    {
        DanbooruAddress address = Parse(text);
        Assert.Equal(DanbooruAddressKind.Post, address.Kind);
        Assert.Equal(id, address.PostId);
        Assert.Equal(safebooru, address.IsSafebooru);
        Assert.Equal(string.Empty, address.Tags);
    }

    [Theory]
    [InlineData("https://danbooru.donmai.us/posts?tags=cat_ears+solo", "cat_ears solo")]
    [InlineData("https://danbooru.donmai.us/posts?tags=cat_ears%20solo&page=3&utf8=%E2%9C%93", "cat_ears solo")]
    [InlineData("https://danbooru.donmai.us/posts?page=2&tags=blue_eyes", "blue_eyes")]
    [InlineData("https://danbooru.donmai.us/posts?tags=rating%3Ag%2Cs+cat", "rating:g,s cat")]
    // An unencoded comma list is part of the value, not a separator.
    [InlineData("https://danbooru.donmai.us/posts?tags=rating:g,s", "rating:g,s")]
    [InlineData("https://danbooru.donmai.us/posts?tags=a%2Bb", "a+b")]
    public void SearchAddressYieldsTheDecodedTagText(string text, string tags)
    {
        DanbooruAddress address = Parse(text);
        Assert.Equal(DanbooruAddressKind.Search, address.Kind);
        Assert.Equal(tags, address.Tags);
        Assert.Null(address.PostId);
    }

    [Theory]
    [InlineData("https://danbooru.donmai.us")]
    [InlineData("https://danbooru.donmai.us/")]
    [InlineData("https://danbooru.donmai.us/posts")]
    [InlineData("https://danbooru.donmai.us/posts?tags=")]
    [InlineData("https://danbooru.donmai.us/posts?page=2")]
    [InlineData("https://danbooru.donmai.us/?tags=")]
    public void BareSiteAddressIsTheSiteWithoutTags(string text)
    {
        DanbooruAddress address = Parse(text);
        Assert.Equal(DanbooruAddressKind.Site, address.Kind);
        Assert.Equal(string.Empty, address.Tags);
    }

    [Theory]
    [InlineData("http://danbooru.donmai.us/posts/123")]
    [InlineData("https://danbooru.donmai.us.evil.test/posts/123")]
    [InlineData("https://evil.test/posts/123")]
    [InlineData("https://testbooru.donmai.us/posts/123")]
    [InlineData("https://betabooru.donmai.us/posts/123")]
    [InlineData("https://user@danbooru.donmai.us/posts/123")]
    [InlineData("https://danbooru.donmai.us:8443/posts/123")]
    [InlineData("https://danbooru.donmai.us/posts/abc")]
    [InlineData("https://danbooru.donmai.us/posts/0123")]
    [InlineData("https://danbooru.donmai.us/posts/123/edit")]
    [InlineData("https://danbooru.donmai.us/wiki_pages/help:api")]
    [InlineData("https://danbooru.donmai.us/posts?tags=cat%0Asolo")]
    [InlineData("123")]
    [InlineData("")]
    public void ForeignOrUnsupportedAddressesAreNotRecognised(string text) =>
        Assert.False(DanbooruAddress.TryParse(text, out _));

    [Fact]
    public void OverlongTagTextIsNotRecognisedInsteadOfTruncated()
    {
        Assert.False(DanbooruAddress.TryParse("https://danbooru.donmai.us/posts?tags=" + new string('a', 1025), out _));
        Assert.True(DanbooruAddress.TryParse("https://danbooru.donmai.us/posts?tags=" + new string('a', 1024), out _));
    }

    [Theory]
    [InlineData("https://safebooru.donmai.us/posts?tags=cat_ears+solo", "cat_ears solo rating:g", true)]
    [InlineData("https://safebooru.donmai.us/", "rating:g", true)]
    [InlineData("https://safebooru.donmai.us/posts", "rating:g", true)]
    // The user's own rating term is kept; nothing is added or replaced.
    [InlineData("https://safebooru.donmai.us/posts?tags=cat+rating:s", "cat rating:s", false)]
    [InlineData("https://safebooru.donmai.us/posts?tags=cat+-rating:e", "cat -rating:e", false)]
    [InlineData("https://danbooru.donmai.us/posts?tags=cat_ears+solo", "cat_ears solo", false)]
    [InlineData("https://danbooru.donmai.us/", "", false)]
    public void PrefilledQueryAddsTheVisibleSafeRatingOnlyForSafebooru(string text, string expected, bool added)
    {
        Assert.Equal(expected, Parse(text).PrefilledQuery(out bool addedSafeRating));
        Assert.Equal(added, addedSafeRating);
    }

    [Fact]
    public void SafebooruIsAnAliasOfDanbooruAndOnlyForUserInput()
    {
        const string link = "https://safebooru.donmai.us/posts/77";
        Assert.False(DanbooruPostReference.TryParse(link, out _, out DanbooruPostReferenceError strict));
        Assert.Equal(DanbooruPostReferenceError.InvalidUrl, strict);
        Assert.True(DanbooruPostReference.TryParseUserInput(link, out DanbooruPostReference? alias, out _));
        Assert.Equal("77", alias!.PostId);
        Assert.Equal("https://danbooru.donmai.us/posts/77", alias.CanonicalUrl.AbsoluteUri);
    }

    [Fact]
    public void SpansKeepCommasInsideAnAddressAndSplitBeforeTheNextUrl()
    {
        const string first = "https://danbooru.donmai.us/posts?tags=rating:g,s";
        const string second = "https://safebooru.donmai.us/posts/5";
        string line = $"x,{first},{second}, https://danbooru.donmai.us/posts/9,";
        (int start, int length)[] spans = DanbooruAddress.FindSpans(line).ToArray();
        Assert.Equal(new[] { first, second, "https://danbooru.donmai.us/posts/9" },
            spans.Select(span => line.Substring(span.start, span.length)));
    }

    [Theory]
    [InlineData("see https://evil.test/?u=https://danbooru.donmai.us/posts/1")]
    [InlineData("https://danbooru.donmai.us.evil.test/posts/1")]
    [InlineData("https://notdanbooru.donmai.us/posts/1")]
    public void SpansNeedARealAddressAtTheStartOfAToken(string line) =>
        Assert.Empty(DanbooruAddress.FindSpans(line));

    [Fact]
    public void LogRedactionHidesAnyDanbooruAddressAndKeepsOtherText()
    {
        Assert.DoesNotContain("secret", DanbooruAddress.RedactForLog("https://danbooru.donmai.us/posts?tags=secret"));
        Assert.DoesNotContain("secret", DanbooruAddress.RedactForLog("a,https://safebooru.donmai.us/posts?tags=secret"));
        Assert.Equal("https://boards.4chan.org/g/thread/1", DanbooruAddress.RedactForLog("https://boards.4chan.org/g/thread/1"));
        Assert.Equal(string.Empty, DanbooruAddress.RedactForLog(null));
    }

    [Theory]
    [InlineData("https://danbooru.donmai.us/posts?tags=blue_sky cat", "https://danbooru.donmai.us/posts?tags=blue_sky cat")]
    [InlineData("a, https://danbooru.donmai.us/posts?tags=blue_sky  cat solo , b", "https://danbooru.donmai.us/posts?tags=blue_sky  cat solo")]
    [InlineData("https://danbooru.donmai.us/posts?tags=rating:g,s cat", "https://danbooru.donmai.us/posts?tags=rating:g,s cat")]
    [InlineData("https://danbooru.donmai.us/posts?tags=blue_sky cat, dog", "https://danbooru.donmai.us/posts?tags=blue_sky cat")]
    [InlineData("https://danbooru.donmai.us/posts?tags=blue_sky , dog", "https://danbooru.donmai.us/posts?tags=blue_sky")]
    public void ASpaceDoesNotEndAnAddressUnlessACommaOrAnotherUrlFollows(string line, string expected)
    {
        (int start, int length) span = Assert.Single(DanbooruAddress.FindSpans(line));
        Assert.Equal(expected, line.Substring(span.start, span.length));
    }

    [Fact]
    public void ASpaceBeforeTheNextUrlStillSplitsTwoAddresses()
    {
        const string line = "https://danbooru.donmai.us/posts?tags=a b https://safebooru.donmai.us/posts/5 https://boards.4chan.org/g/";
        string[] spans = DanbooruAddress.FindSpans(line).Select(span => line.Substring(span.Start, span.Length)).ToArray();
        Assert.Equal(new[] { "https://danbooru.donmai.us/posts?tags=a b", "https://safebooru.donmai.us/posts/5" }, spans);
    }

    [Theory]
    [InlineData("<https://danbooru.donmai.us/posts?tags=secret>")]
    [InlineData("(https://danbooru.donmai.us/posts?tags=secret)")]
    [InlineData("\"https://safebooru.donmai.us/posts?tags=secret\"")]
    [InlineData("'https://safebooru.donmai.us/posts?tags=secret'")]
    [InlineData("x;https://danbooru.donmai.us/posts?tags=secret")]
    [InlineData("see:https://danbooru.donmai.us/posts?tags=secret.")]
    [InlineData("https://danbooru.donmai.us./posts?tags=secret")]
    [InlineData("https://user@danbooru.donmai.us/posts?tags=secret")]
    [InlineData("HTTPS://DANBOORU.DONMAI.US/posts?tags=secret")]
    [InlineData("danbooru.donmai.us/posts?tags=secret")]
    [InlineData("https://example.test/?u=https://danbooru.donmai.us/posts?tags=secret")]
    public void LogRedactionFindsAnAddressInsideBracketsQuotesAndAfterPunctuation(string text)
    {
        Assert.True(DanbooruAddress.MentionsKnownHost(text));
        string logged = DanbooruAddress.RedactForLog(text);
        Assert.DoesNotContain("secret", logged);
        Assert.DoesNotContain("donmai", logged);
    }

    [Fact]
    public void CanonicalTextNeverCarriesTheQuery()
    {
        Assert.Equal("https://danbooru.donmai.us/posts", Parse("https://danbooru.donmai.us/posts?tags=secret").CanonicalText);
        Assert.Equal("https://safebooru.donmai.us/posts", Parse("https://safebooru.donmai.us/posts?tags=secret&page=2").CanonicalText);
        Assert.Equal("https://danbooru.donmai.us/posts", Parse("https://danbooru.donmai.us/").CanonicalText);
        Assert.Equal("https://safebooru.donmai.us/posts/77", Parse("https://safebooru.donmai.us/posts/77?q=secret").CanonicalText);
    }
}
