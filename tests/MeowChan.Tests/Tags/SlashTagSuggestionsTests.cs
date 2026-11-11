using MeowChan.Tags;

namespace MeowChan.Tests.Tags;

public sealed class SlashTagSuggestionsTests
{
    private static string[] Detect(params string?[] texts) =>
        SlashTagSuggestions.Detect(texts).Select(name => name.Display).ToArray();

    [Theory]
    [InlineData("/rpt/ - Roleplay Thread", "/rpt/")]
    [InlineData("Welcome to /RPT/!", "/rpt/")]
    [InlineData("(/sdg/) Stable Diffusion General", "/sdg/")]
    [InlineData("/aicg/ and /lmg/ crossover", "/aicg/,/lmg/")]
    [InlineData("/k-pop/ /c_c/ /c++/", "/k-pop/,/c_c/,/c++/")]
    [InlineData("\u30B9\u30EC /\u65E5\u672C\u8A9E/ \u3067\u3059", "/\u65E5\u672C\u8A9E/")]
    [InlineData("ends with /vg/", "/vg/")]
    public void MarkersAreFoundLowercasedInOrderOfAppearance(string text, string expected) =>
        Assert.Equal(expected.Split(','), Detect(text));

    [Theory]
    [InlineData("https://boards.4chan.org/g/thread/123")]
    [InlineData("see /usr/bin/env and C:/path/to/file")]
    [InlineData("10/11/2026 and 1/2/ and /123/")]
    [InlineData("a/b/c x/rpt/ /rpt/x //rpt// /rpt")]
    [InlineData(">>>/g/123456")]
    [InlineData("/ / /-/ /_/ // /a b/")]
    [InlineData("/this-token-is-far-too-long-to-be-a-general-name/")]
    [InlineData("")]
    public void PathsUrlsDatesAndMalformedMarkersAreNotSuggested(string text) => Assert.Empty(Detect(text));

    [Fact]
    public void TitleComesFirstDuplicatesCollapseAndNullsAreIgnored()
    {
        Assert.Equal(new[] { "/rpt/", "/sdg/", "/new/" },
            Detect("/rpt/ thread", null, "", "old /RPT/ link, also /sdg/", "/new/ /Sdg/"));
    }

    [Fact]
    public void ResultsAndScannedTextAreBounded()
    {
        string many = string.Join(" ", Enumerable.Range(0, 50).Select(index => "/tag" + index + "/"));
        Assert.Equal(SlashTagSuggestions.MaximumSuggestions, Detect(many).Length);
        Assert.Equal("/tag0/", Detect(many)[0]);

        string late = new string(' ', SlashTagSuggestions.MaximumScannedCharacters) + "/late/";
        Assert.Empty(Detect(late));
        // A marker cut off by the scan limit is not completed from unscanned text.
        string straddling = new string(' ', SlashTagSuggestions.MaximumScannedCharacters - 3) + "/late/";
        Assert.Empty(Detect(straddling));
        Assert.Equal(new[] { "/ok/" }, Detect(late, "/ok/"));
    }

    [Fact]
    public void SuggestionsAreValidTagNamesThatCompareLikeTags()
    {
        PrivateTagName suggested = Assert.Single(SlashTagSuggestions.Detect("/RpT/"));
        Assert.True(PrivateTagName.TryCreate("/rpt/", out PrivateTagName? typed, out _));
        Assert.Equal(typed, suggested);
        Assert.Equal("/rpt/", suggested.Display);
    }
}
