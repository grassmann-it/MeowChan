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

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(20)]
    public void ConfiguredCountPreservesOrderDedupeAndTextSafetyCeilings(int count)
    {
        string many = string.Join(" ", Enumerable.Range(0, 40).Select(index => "/tag" + index + "/"));
        string[] expected = Enumerable.Range(0, count).Select(index => "/tag" + index + "/").ToArray();
        Assert.Equal(expected, SlashTagSuggestions.Detect(count, "/TAG0/", null, many).Select(name => name.Display));
        string late = new string(' ', SlashTagSuggestions.MaximumScannedCharacters) + "/late/";
        string cut = new string(' ', SlashTagSuggestions.MaximumScannedCharacters - 3) + "/late/";
        string longToken = "/" + new string('a', SlashTagSuggestions.MaximumTokenCharacters + 1) + "/";
        Assert.Empty(SlashTagSuggestions.Detect(count, late, cut, longToken, "https://host.invalid/path/"));
        Assert.Equal(new[] { "/ok/" }, SlashTagSuggestions.Detect(count, late, "/ok/").Select(name => name.Display));
        Assert.Throws<ArgumentNullException>(() => SlashTagSuggestions.Detect(count, (string?[])null!));
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(2)]
    [InlineData(21)]
    [InlineData(int.MaxValue)]
    public void InvalidSuggestionCountsAreRefusedEvenWithoutInput(int count) =>
        Assert.Equal("suggestionCount", Assert.Throws<ArgumentOutOfRangeException>(() => SlashTagSuggestions.Detect(count)).ParamName);

    [Fact]
    public void SuggestionsAreValidTagNamesThatCompareLikeTags()
    {
        PrivateTagName suggested = Assert.Single(SlashTagSuggestions.Detect("/RpT/"));
        Assert.True(PrivateTagName.TryCreate("/rpt/", out PrivateTagName? typed, out _));
        Assert.Equal(typed, suggested);
        Assert.Equal("/rpt/", suggested.Display);
    }
}
