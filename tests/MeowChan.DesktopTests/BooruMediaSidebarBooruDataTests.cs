using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using MeowChan.Tests.Shared;
using static MeowChan.DesktopTests.BooruMediaSidebarTests;

namespace MeowChan.DesktopTests;

/// <summary>
/// Tom, 25.11.2026 14:48/14:55: the Booru sidebar shows the provider's data instead of the local rating and tag editor, and
/// its actions read download, copy post link, then the small open-in-browser icon, filling the row without clipping.
/// </summary>
[Collection("Booru exact navigation")]
public sealed class BooruMediaSidebarBooruDataTests
{
    [Fact]
    public void HeaderNamesThePostAndProviderAndTheStatsShowWhatDanbooruDelivered() => InWindow((harness, model, view, window) =>
    {
        Texts.SetLanguage("en");
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("101", rating: BooruRating.Questionable, score: 12,
                tags: ["cat", "blue_eyes", "sky", "ice_bear"]))));
        Search(model, view);
        Settle(window);
        BooruMediaSidebar sidebar = Sidebar(view);

        Assert.Contains("101", ById<TextBlock>(sidebar, "BooruMediaTitle").Text);
        Assert.Contains("Danbooru", AutomationProperties.GetName(ById<TextBlock>(sidebar, "BooruMediaSummary")));
        Assert.True(ById<Control>(sidebar, "BooruMediaStats").IsEffectivelyVisible);
        TextBlock rating = ById<TextBlock>(sidebar, "BooruMediaStatRating");
        Assert.True(rating.IsEffectivelyVisible);
        Assert.Equal(BooruText.Rating(BooruRating.Questionable), rating.Text);
        Assert.Equal(Texts.T("booru.ui.post.rating", BooruText.Rating(BooruRating.Questionable)), AutomationProperties.GetName(rating));
        TextBlock score = ById<TextBlock>(sidebar, "BooruMediaStatScore");
        Assert.True(score.IsEffectivelyVisible);
        Assert.Equal(Texts.T("booru.ui.result.short.score", "12"), score.Text);
        Control tags = ById<Control>(sidebar, "BooruMediaStatTags");
        Assert.True(tags.IsEffectivelyVisible);
        Assert.Equal("4", ById<TextBlock>(sidebar, "BooruMediaStatTagCount").Text);
        Assert.Equal(Texts.Count("booru.media.gallery.tags", 4), AutomationProperties.GetName(tags));
        AssertNoLocalEditing(sidebar);

        // The stats sit between the actions and the source tags, inside the sidebar.
        Control stats = ById<Control>(sidebar, "BooruMediaStats");
        Assert.True(In(ById<Control>(sidebar, "BooruMediaActions"), sidebar).Bottom <= In(stats, sidebar).Top);
        Assert.True(In(stats, sidebar).Bottom <= In(ById<Control>(sidebar, "BooruMediaTags"), sidebar).Top);
        Assert.InRange(In(stats, sidebar).Right, 1, sidebar.Bounds.Width);
    });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    public void StatsTheProviderDidNotDeliverStayHidden(bool rated, bool scored, bool tagged) => InWindow((harness, model, view, window) =>
    {
        BooruPost post = BooruUiFixtures.Post("101", rating: rated ? BooruRating.General : BooruRating.Unknown, score: 0).Post;
        BooruFieldPresence fields = post.Fields;
        if (!scored) fields &= ~BooruFieldPresence.Score;
        if (!tagged) fields &= ~BooruFieldPresence.Tags;
        BooruPostView row = BooruPostView.From(post with { Fields = fields });
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, row)));
        Search(model, view);
        Settle(window);
        BooruMediaSidebar sidebar = Sidebar(view);

        Assert.Equal(rated, ById<Control>(sidebar, "BooruMediaStatRating").IsEffectivelyVisible);
        Assert.Equal(scored, ById<Control>(sidebar, "BooruMediaStatScore").IsEffectivelyVisible);
        Assert.Equal(tagged, ById<Control>(sidebar, "BooruMediaStatTags").IsEffectivelyVisible);
        // A score of zero is a delivered value, not a missing one.
        if (scored) Assert.Equal(Texts.T("booru.ui.result.short.score", "0"), ById<TextBlock>(sidebar, "BooruMediaStatScore").Text);
        Assert.Equal(rated || scored || tagged, ById<Control>(sidebar, "BooruMediaStats").IsEffectivelyVisible);
        AssertNoLocalEditing(sidebar);
    });

    [Theory]
    [InlineData("en", 280)]
    [InlineData("en", 360)]
    [InlineData("en", 480)]
    [InlineData("de", 280)]
    [InlineData("de", 360)]
    [InlineData("de", 480)]
    [InlineData("ru", 280)]
    [InlineData("ru", 360)]
    [InlineData("ru", 480)]
    public void ActionsReadDownloadCopyThenOpenAndFillTheRow(string language, int width) => InWindow((harness, model, view, window) =>
    {
        Texts.SetLanguage(language);
        harness.Serve(pages: 1, perPage: 1);
        Search(model, view);
        BooruMediaSidebar sidebar = Sidebar(view);
        sidebar.Width = width;
        Settle(window);

        Control actions = ById<Control>(sidebar, "BooruMediaActions");
        Button load = ById<Button>(sidebar, "BooruPanelBooruLoad:101");
        Button copy = ById<Button>(sidebar, "BooruPanelBooruCopyLink:101");
        Button open = ById<Button>(sidebar, "BooruPanelBooruOpenPost:101");
        Rect[] bounds = new[] { load, copy, open }.Select(button => In(button, actions)).ToArray();
        Assert.All(new[] { load, copy, open }, button =>
        {
            Assert.True(button.IsEffectivelyVisible);
            Assert.False(button.Focusable);
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)));
        });
        Assert.Single(bounds.Select(rect => Math.Round(rect.Y)).Distinct());
        Assert.True(bounds[0].Right <= bounds[1].Left && bounds[1].Right <= bounds[2].Left, "Download, copy and open are out of order.");
        // The row is filled: download starts at its left edge, the open icon ends at its right edge.
        Assert.Equal(0, bounds[0].Left, precision: 0);
        Assert.InRange(bounds[2].Right, actions.Bounds.Width - 1, actions.Bounds.Width + 0.5);
        Assert.InRange(In(actions, sidebar).Right, 1, width);
        Assert.Equal(bounds[0].Width, bounds[1].Width, precision: 0);
        Assert.Equal(open.Bounds.Height, open.Bounds.Width);
        Assert.False(ById<TextBlock>(open, "BooruPanelBooruOpenPost:101:Label").IsEffectivelyVisible);
        AssertActionLabelFits(load, requireLabel: width >= 480);
        AssertActionLabelFits(copy, requireLabel: width >= 480);
    });
}
