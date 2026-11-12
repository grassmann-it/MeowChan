using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Source tags of a downloaded Danbooru post as clipboard text and as a Danbooru search. A search never silently loses a tag:
// a selection either becomes exactly the query the host's own parser accepts, or is refused with one stated reason.
[Collection("Localization")]
public sealed class BooruSourceTagSearchTests : IDisposable
{
    public BooruSourceTagSearchTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void TagsAreWrittenWithSingleSpacesInTheProvidersSpelling()
    {
        Assert.Equal("artist_name 1girl blue_sky", BooruSourceTagSearch.Written(new[] { "artist_name", "1girl", "blue_sky" }));
        Assert.Equal("solo", BooruSourceTagSearch.Written(new[] { "solo" }));
        Assert.Equal(string.Empty, BooruSourceTagSearch.Written(Array.Empty<string>()));
    }

    [Fact]
    public void OneOrTwoPlainTagsBecomeExactlyThatQuery()
    {
        BooruTagSearchPlan one = BooruSourceTagSearch.Plan(new[] { "blue_sky" });
        Assert.True(one.IsValid);
        Assert.Equal("blue_sky", one.Query);
        BooruTagSearchPlan two = BooruSourceTagSearch.Plan(new[] { "artist_name", "1girl" });
        Assert.True(two.IsValid);
        Assert.Equal("artist_name 1girl", two.Query);
        Assert.Equal(2, BooruSourceTagSearch.MaximumTags);
    }

    [Fact]
    public void ANaturalSelectionOverTheLimitIsRefusedWithItsCountAndNothingIsDropped()
    {
        BooruTagSearchPlan three = BooruSourceTagSearch.Plan(new[] { "a", "b", "c" });
        Assert.False(three.IsValid);
        Assert.Equal(BooruTagSearchProblem.TooMany, three.Problem);
        Assert.Null(three.Query);
        Assert.Equal(3, three.Count);
    }

    [Fact]
    public void RepeatedNamesCountOnceAndNothingSelectedIsItsOwnProblem()
    {
        BooruTagSearchPlan same = BooruSourceTagSearch.Plan(new[] { "a", "a", "b" });
        Assert.True(same.IsValid);
        Assert.Equal("a b", same.Query);
        Assert.Equal(BooruTagSearchProblem.NothingSelected, BooruSourceTagSearch.Plan(Array.Empty<string>()).Problem);
    }

    [Theory]
    [InlineData("ganyu_(genshin_impact)")]
    [InlineData("re:zero_kara_hajimeru_isekai_seikatsu")]
    [InlineData("-negative")]
    [InlineData("or")]
    public void ATagTheQueryParserRejectsIsNamedWithTheParsersReason(string tag)
    {
        BooruTagSearchPlan plan = BooruSourceTagSearch.Plan(new[] { "blue_sky", tag });
        Assert.Equal(BooruTagSearchProblem.Unusable, plan.Problem);
        Assert.Equal(tag, plan.Tag);
        Assert.Null(plan.Query);
        Assert.NotEqual(BooruInputIssue.None, plan.Issue);
    }

    [Fact]
    public void ARatingTermIsNotASourceTagAndIsRefusedLikeAnyOtherUnusableTag()
    {
        // The parser would accept "rating:g" as a rating term; a source tag is never one, so it must not change a search's meaning.
        BooruTagSearchPlan plan = BooruSourceTagSearch.Plan(new[] { "rating:g" });
        Assert.Equal(BooruTagSearchProblem.Unusable, plan.Problem);
        Assert.Null(plan.Query);
    }
}
