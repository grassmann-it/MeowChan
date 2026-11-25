using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.VisualTree;
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

        // Plan review §2: “Je Wert eine sichtbare Darstellung … und die bestehende Kopierfunktion der Fakten erhalten.”
        ContextGestures.Click(ById<ToggleButton>(sidebar, "BooruMediaInfo"));
        Assert.Contains("101", ById<TextBlock>(sidebar, "BooruMediaTitle").Text);
        Assert.Contains("Danbooru", AutomationProperties.GetName(ById<TextBlock>(sidebar, "BooruMediaSummary")));
        Assert.True(ById<Control>(sidebar, "BooruMediaStats").IsEffectivelyVisible);
        TextBlock rating = ById<TextBlock>(sidebar, "BooruMediaStatRating");
        Assert.True(rating.IsEffectivelyVisible);
        Assert.Equal(BooruText.Rating(BooruRating.Questionable), rating.Text);
        AssertDescription(rating, Texts.T("media.breakdown.summary", Texts.T("booru.rating.label"), rating.Text));
        TextBlock score = ById<TextBlock>(sidebar, "BooruMediaStatScore");
        Assert.True(score.IsEffectivelyVisible);
        Assert.Equal(Texts.T("booru.ui.result.short.score", "12"), score.Text);
        AssertDescription(score, Texts.T("booru.media.provider.score", 12));
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
        BooruPost post = BooruUiFixtures.Post("101", rating: rated ? BooruRating.General : BooruRating.Unknown, score: 0, rawRating: "").Post;
        BooruFieldPresence fields = post.Fields;
        if (!scored) fields &= ~BooruFieldPresence.Score;
        if (!tagged) fields &= ~BooruFieldPresence.Tags;
        BooruPostView row = BooruPostView.From(post with { Fields = fields });
        harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
            BooruUiFixtures.Page(query, page, true, row)));
        Search(model, view);
        Settle(window);
        BooruMediaSidebar sidebar = Sidebar(view);

        ContextGestures.Click(ById<ToggleButton>(sidebar, "BooruMediaInfo"));
        Assert.Equal(rated, ById<Control>(sidebar, "BooruMediaStatRating").IsEffectivelyVisible);
        Assert.Equal(scored, ById<Control>(sidebar, "BooruMediaStatScore").IsEffectivelyVisible);
        Assert.Equal(tagged, ById<Control>(sidebar, "BooruMediaStatTags").IsEffectivelyVisible);
        // A score of zero is a delivered value, not a missing one.
        if (scored) Assert.Equal(Texts.T("booru.ui.result.short.score", "0"), ById<TextBlock>(sidebar, "BooruMediaStatScore").Text);
        Assert.Equal(rated || scored || tagged, ById<Control>(sidebar, "BooruMediaStats").IsEffectivelyVisible);
        AssertNoLocalEditing(sidebar);
    });

    [Theory]
    [InlineData(BooruRating.General)]
    [InlineData(BooruRating.Sensitive)]
    [InlineData(BooruRating.Questionable)]
    [InlineData(BooruRating.Explicit)]
    [InlineData(BooruRating.Unknown, "x")]
    public void FactsAndCompactStatsTakeTurnsShowingEachProviderValue(BooruRating rating, string? raw = null) =>
        InWindow((harness, model, view, window) =>
        {
            BooruPost post = BooruUiFixtures.Post("101", rating: rating, rawRating: raw, score: -7).Post;
            BooruPostView row = BooruPostView.From(post with { RawRating = raw, Fields = post.Fields & ~BooruFieldPresence.Tags });
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, row)));
            Search(model, view);
            Settle(window);
            BooruMediaSidebar sidebar = Sidebar(view);
            ToggleButton info = ById<ToggleButton>(sidebar, "BooruMediaInfo");
            SelectableTextBlock ratingFact = ById<SelectableTextBlock>(sidebar, "BooruMediaRating");
            SelectableTextBlock scoreFact = ById<SelectableTextBlock>(sidebar, "BooruMediaScore");
            TextBlock ratingStat = ById<TextBlock>(sidebar, "BooruMediaStatRating");
            TextBlock scoreStat = ById<TextBlock>(sidebar, "BooruMediaStatScore");
            string ratingText = BooruText.Rating(rating) + (raw is null ? "" : " (" + raw + ")");
            string scoreText = (-7).ToString(Texts.Culture);
            string ratingName = Texts.T("media.breakdown.summary", Texts.T("booru.rating.label"), ratingText);
            string scoreName = Texts.T("booru.media.provider.score", scoreText);

            AssertFactsOpen();
            if (raw is not null)
            {
                CopyFact(ratingFact, ratingText);
                CopyFact(scoreFact, scoreText);
            }
            ContextGestures.Click(info);
            Assert.False(ratingFact.IsEffectivelyVisible);
            Assert.False(scoreFact.IsEffectivelyVisible);
            Assert.True(ratingStat.IsEffectivelyVisible);
            Assert.True(scoreStat.IsEffectivelyVisible);
            Assert.Equal(ratingText, ratingStat.Text);
            Assert.Equal(Texts.T("booru.ui.result.short.score", scoreText), scoreStat.Text);
            AssertDescription(ratingStat, ratingName);
            AssertDescription(scoreStat, scoreName);
            ContextGestures.Click(info);
            AssertFactsOpen();

            void AssertFactsOpen()
            {
                Assert.True(ratingFact.IsEffectivelyVisible);
                Assert.True(scoreFact.IsEffectivelyVisible);
                Assert.Equal(ratingText, ratingFact.Text);
                Assert.Equal(scoreText, scoreFact.Text);
                AssertDescription(ratingFact, ratingName);
                AssertDescription(scoreFact, scoreName);
                Assert.False(ratingStat.IsEffectivelyVisible);
                Assert.False(scoreStat.IsEffectivelyVisible);
                Assert.False(ById<Control>(sidebar, "BooruMediaStats").IsEffectivelyVisible);
            }

            void CopyFact(SelectableTextBlock fact, string expected)
            {
                ContextGestures.ShiftF10(fact);
                ContextMenu menu = Assert.IsType<ContextMenu>(fact.ContextMenu);
                Assert.True(menu.IsOpen);
                Assert.Single(MenuProbe.Entries(menu), entry => entry.Id == "panel.copy-value").Activate();
                Assert.Equal(expected, BooruComponentDispatcher.Wait(window.Clipboard!.TryGetTextAsync()));
                menu.Close();
                Settle(window);
            }
        });

    [Fact]
    public void SwitchingHitsDistinguishesEmptyAndMissingTagsAndClearsPreviousValues() =>
        InWindow((harness, model, view, window) =>
        {
            BooruPost empty = BooruUiFixtures.Post("102", tags: []).Post;
            BooruPostView EmptyOrMissing(BooruPost post, BooruFieldPresence keep) =>
                BooruPostView.From(post with { Fields = BooruFieldPresence.Media | keep });
            harness.Search.OnSearch = (query, page, _) => Task.FromResult(BooruCallResult<BooruResultPage>.Ok(
                BooruUiFixtures.Page(query, page, true, BooruUiFixtures.Post("101"),
                    EmptyOrMissing(empty, BooruFieldPresence.Tags),
                    EmptyOrMissing(BooruUiFixtures.Post("103").Post, BooruFieldPresence.None))));
            Search(model, view);
            BooruMediaSidebar sidebar = Sidebar(view);
            ToggleButton info = ById<ToggleButton>(sidebar, "BooruMediaInfo");
            ContextGestures.Click(info);
            ScrollViewer tags = ById<ScrollViewer>(sidebar, "BooruMediaTags");
            Control tagStat = ById<Control>(sidebar, "BooruMediaStatTags");
            TextBlock count = ById<TextBlock>(sidebar, "BooruMediaStatTagCount");
            Assert.True(ById<Button>(tags, "BooruPanelTag:blue_eyes").IsEffectivelyVisible);
            Assert.True(ById<Control>(sidebar, "BooruMediaStatRating").IsEffectivelyVisible);
            Assert.True(ById<Control>(sidebar, "BooruMediaStatScore").IsEffectivelyVisible);

            Assert.True(Hit(view, "103").Focus(NavigationMethod.Directional));
            Settle(window);
            Assert.False(tags.IsEffectivelyVisible);
            Assert.Null(tags.Content);
            Assert.False(tagStat.IsEffectivelyVisible);
            Assert.False(ById<Control>(sidebar, "BooruMediaStats").IsEffectivelyVisible);
            ContextGestures.Click(info);
            Assert.DoesNotContain(sidebar.GetVisualDescendants().OfType<Control>(), control =>
                AutomationProperties.GetAutomationId(control) is "BooruMediaRating" or "BooruMediaScore");

            Assert.True(Hit(view, "102").Focus(NavigationMethod.Directional));
            Settle(window);
            Assert.True(tags.IsEffectivelyVisible);
            Assert.True(tagStat.IsEffectivelyVisible);
            Assert.Equal("0", count.Text);
            AssertDescription(tagStat, Texts.Count("booru.media.gallery.tags", 0));
            Assert.DoesNotContain(tags.GetVisualDescendants().OfType<Button>(), button =>
                AutomationProperties.GetAutomationId(button) == "BooruPanelTag:blue_eyes");

            Assert.True(Hit(view, "101").Focus(NavigationMethod.Directional));
            Settle(window);
            Assert.True(ById<Button>(tags, "BooruPanelTag:blue_eyes").IsEffectivelyVisible);
            Assert.Equal("3", count.Text);
        });

    private static void AssertDescription(Control control, string expected)
    {
        Assert.Equal(expected, AutomationProperties.GetName(control));
        Assert.Equal(expected, ToolTip.GetTip(control));
    }

    [Theory]
    [InlineData("en", 280)]
    [InlineData("en", 320)]
    [InlineData("en", 360)]
    [InlineData("en", 480)]
    [InlineData("de", 280)]
    [InlineData("de", 320)]
    [InlineData("de", 360)]
    [InlineData("de", 480)]
    [InlineData("ru", 280)]
    [InlineData("ru", 320)]
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
        VerifyBestFittingLabel(load, Texts.T("booru.ui.load"), Texts.T("booru.ui.result.short.load"));
        VerifyBestFittingLabel(copy, Texts.T("booru.media.copyPost"), Texts.T("booru.ui.result.short.copy"));

        static void VerifyBestFittingLabel(Button button, string full, string brief)
        {
            TextBlock label = ById<TextBlock>(button, AutomationProperties.GetAutomationId(button) + ":Label");
            // Allow for the 16 DIP icon, its 4 DIP gap and 8 DIP of padding on either side.
            double room = button.Bounds.Width - button.BorderThickness.Left - button.BorderThickness.Right - 36;
            string? expected = new[] { full, brief }.FirstOrDefault(candidate =>
            {
                FormattedText measured = new(candidate, Texts.Culture, label.FlowDirection,
                    new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, label.Foreground);
                return Math.Ceiling(measured.WidthIncludingTrailingWhitespace) <= room;
            });
            Assert.Equal(expected, label.IsEffectivelyVisible ? label.Text : null);
        }
    });
}
