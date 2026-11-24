using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// The Media page's media panel (1.1.0 split view): only the pure plans are pinned here: the body row's widths (MediaPanelColumns),
// and its key routing (MediaPanelKeys.Route). The page itself is driven in the media-page smoke.
public sealed class MediaPanelTests
{
    private const double CombinedContentMinimum = MediaPanelColumns.MinimumCombinedContentWidth;
    private const double Chrome = MediaPageLayoutPlan.BodyChrome + MediaPanelColumns.PanelChrome;
    private const int MinimumPage = MeowChan.Config.MediaPanelMinimumPageWidthDefault;

    private static MediaPageLayout WithTagList(int tagListWidth = MeowChan.Config.MediaTagListWidthDefault, int panelWidth = MeowChan.Config.MediaPanelWidthDefault) =>
        MediaPageLayout.Standard with { TagList = true, TagListWidth = tagListWidth, PanelWidth = panelWidth };

    // ---- MediaPanelColumns ----

    [Fact]
    public void ThePanelIsOnByDefaultAndKeepsItsSavedWidthOnAWidePage()
    {
        Assert.True(MediaPageLayout.Standard.Panel);
        Assert.Equal(MeowChan.Config.MediaPanelWidthDefault, MediaPageLayout.Standard.PanelWidth);
        Assert.Equal(new MediaPanelColumns(true, false, 0, MeowChan.Config.MediaPanelWidthDefault),
            MediaPanelColumns.Of(MediaPageLayout.Standard, 1400, MinimumPage));
        Assert.Equal(new MediaPanelColumns(true, false, 256, 360), MediaPanelColumns.Of(WithTagList(), 1400, MinimumPage));
    }

    [Theory]
    [InlineData(999, false, 0)]
    [InlineData(1000, true, 342)]
    [InlineData(1001, true, 343)]
    public void ThePanelHidesBelowTheSettingAndLeavesRoomForTheToolbarWhenItReturns(double bodyWidth, bool shown, double panelWidth)
    {
        MediaPanelColumns columns = MediaPanelColumns.Of(MediaPageLayout.Standard, bodyWidth, MinimumPage);
        Assert.Equal(shown, columns.PanelShown);
        Assert.Equal(!shown, columns.CollapsedByWidth);
        Assert.Equal(panelWidth, columns.PanelWidth);
        // Hiding by width never turns the layout's choice off: wide again, the panel is back.
        Assert.True(MediaPanelColumns.Of(MediaPageLayout.Standard, 1400, MinimumPage).PanelShown);
    }

    [Fact]
    public void APanelSwitchedOffIsNotCollapsedByWidth()
    {
        MediaPageLayout off = MediaPageLayout.Standard with { Panel = false };
        Assert.Equal(new MediaPanelColumns(false, false, 0, 0), MediaPanelColumns.Of(off, 1400, MinimumPage));
        Assert.Equal(new MediaPanelColumns(false, false, 0, 0), MediaPanelColumns.Of(off, 700, MinimumPage));
        Assert.Equal(new MediaPanelColumns(false, false, 256, 0), MediaPanelColumns.Of(off with { TagList = true }, 1400, MinimumPage));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnknownWidthCountsAsWideEnoughAndKeepsTheSavedWidths(double bodyWidth)
    {
        Assert.Equal(new MediaPanelColumns(true, false, 470, 900), MediaPanelColumns.Of(WithTagList(470, 900), bodyWidth, MinimumPage));
        Assert.Equal(new MediaPanelColumns(true, false, 0, 360), MediaPanelColumns.Of(MediaPageLayout.Standard, bodyWidth, MinimumPage));
    }

    [Fact]
    public void TheMinimumPageWidthIsBroughtIntoItsRange()
    {
        // A setting below the range still has to leave space for the toolbar beside the panel.
        Assert.Equal(898, MediaPanelColumns.MinimumBodyWidth(MediaPageLayout.Standard, 100));
        Assert.False(MediaPanelColumns.Of(MediaPageLayout.Standard, 897, 100).PanelShown);
        Assert.True(MediaPanelColumns.Of(MediaPageLayout.Standard, 898, 100).PanelShown);
        Assert.True(MediaPanelColumns.Of(MediaPageLayout.Standard, 2999, 100_000).CollapsedByWidth);
        Assert.True(MediaPanelColumns.Of(MediaPageLayout.Standard, 3000, 100_000).PanelShown);
    }

    [Fact]
    public void ThePanelAndTagListYieldBeforeTheToolbarCanBeClipped()
    {
        Assert.Equal(new MediaPanelColumns(true, false, 1200 - CombinedContentMinimum - Chrome - 240, 240),
            MediaPanelColumns.Of(WithTagList(480, 900), 1200, MinimumPage));
        Assert.Equal(900, MediaPanelColumns.Of(MediaPageLayout.Standard with { PanelWidth = 5000 }, 3000, MinimumPage).PanelWidth);
        Assert.Equal(240, MediaPanelColumns.Of(MediaPageLayout.Standard with { PanelWidth = 10 }, 3000, MinimumPage).PanelWidth);
        double narrowest = CombinedContentMinimum + Chrome + MeowChan.Config.MediaTagListWidthMinimum + MeowChan.Config.MediaPanelWidthMinimum;
        Assert.Equal(new MediaPanelColumns(true, false, 180, 240), MediaPanelColumns.Of(WithTagList(), narrowest, 600));
    }

    [Fact]
    public void TheTagListTemporarilyYieldsWhenItWouldPushTheToolbarBelowItsMinimum()
    {
        MediaPageLayout layout = WithTagList() with { Panel = false };
        double narrowest = CombinedContentMinimum + MediaPageLayoutPlan.BodyChrome + MeowChan.Config.MediaTagListWidthMinimum;
        MediaPanelColumns below = MediaPanelColumns.Of(layout, narrowest - 1, 600);
        MediaPanelColumns exact = MediaPanelColumns.Of(layout, narrowest, 600);

        Assert.False(MediaPanelColumns.TagListShown(layout, narrowest - 1, below));
        Assert.True(MediaPanelColumns.TagListShown(layout, narrowest, exact));
        Assert.True(MediaPanelColumns.TagListShown(layout, double.NaN, exact));
        Assert.False(MediaPanelColumns.TagListShown(layout with { TagList = false }, narrowest, exact));
        Assert.True(layout.TagList);
    }

    // Narrowing with both sidebars wanted: the viewer folds first (it needs 616 + its own minimum beside the tag list), then the tag list yields
    // below 838 DIP (616 toolbar + 180 list + 42 chrome), and both come back in the reverse order on widening.
    [Theory]
    [InlineData(1400, true, true)]
    [InlineData(1096, true, true)]   // 616 + 42 + 18 + 180 + 240: both sidebars at their minimum
    [InlineData(1095, false, true)]  // the viewer folds first
    [InlineData(839, false, true)]
    [InlineData(838, false, true)]   // the tag list and a 616 DIP toolbar fit exactly
    [InlineData(837, false, false)]  // the tag list yields too
    [InlineData(760, false, false)]  // the window minimum of Simple/Advanced
    [InlineData(640, false, false)]  // the window minimum of Barebones
    public void TheViewerCollapsesBeforeTheTagListAndBothReturnOnWidening(double bodyWidth, bool viewer, bool tagList)
    {
        MediaPageLayout layout = WithTagList();
        Assert.Equal(838, CombinedContentMinimum + MediaPageLayoutPlan.BodyChrome + MeowChan.Config.MediaTagListWidthMinimum);
        MediaPanelColumns columns = MediaPanelColumns.Of(layout, bodyWidth, 600);
        Assert.Equal(viewer, columns.PanelShown);
        Assert.Equal(!viewer, columns.CollapsedByWidth);
        Assert.Equal(tagList, MediaPanelColumns.TagListShown(layout, bodyWidth, columns));
        // What stays for the center: never under 616 DIP while any sidebar is shown.
        double center = bodyWidth - (tagList ? MediaPageLayoutPlan.BodyChrome + columns.TagListWidth : 0)
            - (viewer ? MediaPanelColumns.PanelChrome + columns.PanelWidth : 0);
        if (tagList || viewer) Assert.True(center + 0.5 >= CombinedContentMinimum, $"center {center} at {bodyWidth}");
        Assert.True(layout.TagList && layout.Panel);
    }

    // With the viewer folded or switched off, the tag list still has to leave 616 DIP inside the toolbar's margins.
    [Theory]
    [InlineData(true, 1100)]
    [InlineData(true, 600)]
    [InlineData(false, 600)]
    public void BesideTheTagListTheCenterKeeps616WithOrWithoutTheViewer(bool panel, int minimumPageWidth)
    {
        foreach (int saved in new[] { 180, 256, 480 })
        {
            MediaPageLayout layout = WithTagList(saved) with { Panel = panel };
            for (double width = 838; width <= 1600; width += 3)
            {
                MediaPanelColumns columns = MediaPanelColumns.Of(layout, width, minimumPageWidth);
                Assert.True(MediaPanelColumns.TagListShown(layout, width, columns));
                double center = width - MediaPageLayoutPlan.BodyChrome - columns.TagListWidth - (columns.PanelShown ? MediaPanelColumns.PanelChrome + columns.PanelWidth : 0);
                Assert.True(center + 0.5 >= CombinedContentMinimum, $"{layout.Format()} @{width}: center {center}");
                Assert.InRange(columns.TagListWidth, MeowChan.Config.MediaTagListWidthMinimum, saved);
                // The drag handle cannot take the center under 616 either.
                double dragLimit = MediaPanelColumns.MaximumTagListWidth(width, columns.PanelShown, columns.PanelWidth);
                Assert.True(width - MediaPageLayoutPlan.BodyChrome - dragLimit - (columns.PanelShown ? MediaPanelColumns.PanelChrome + columns.PanelWidth : 0) + 0.5 >= CombinedContentMinimum,
                    $"{layout.Format()} @{width}: drag limit {dragLimit}");
            }
        }
        Assert.Equal(186, MediaPanelColumns.Of(WithTagList() with { Panel = panel }, 844, minimumPageWidth).TagListWidth);
    }

    [Fact]
    public void WithoutRoomForTheToolbarAndBothSidebarsThePanelHidesAndTheTagListGetsItsWidthBack()
    {
        double narrowest = CombinedContentMinimum + Chrome + MeowChan.Config.MediaTagListWidthMinimum + MeowChan.Config.MediaPanelWidthMinimum;
        Assert.Equal(new MediaPanelColumns(false, true, 256, 0), MediaPanelColumns.Of(WithTagList(), narrowest - 1, 600));
    }

    [Fact]
    public void AShownPanelNeverLeavesTheToolbarUnderItsMinimum()
    {
        MediaPageLayout[] layouts =
        {
            MediaPageLayout.Standard, MediaPageLayout.Standard with { PanelWidth = 900 }, WithTagList(), WithTagList(480, 900), WithTagList(180, 240),
            WithTagList(256, 600) with { TagListSide = MediaTagListSide.Right, PanelSide = MediaPanelSide.Left }
        };
        foreach (MediaPageLayout layout in layouts)
            for (double width = 500; width <= 3200; width += 7)
            {
                MediaPanelColumns columns = MediaPanelColumns.Of(layout, width, 600);
                if (!columns.PanelShown) { Assert.Equal(0, columns.PanelWidth); continue; }
                Assert.InRange(columns.PanelWidth, MeowChan.Config.MediaPanelWidthMinimum, MeowChan.Config.MediaPanelWidthMaximum);
                double chrome = layout.TagList ? Chrome : 2 * MediaToolbarLayout.HorizontalMargin + MediaPanelColumns.PanelChrome;
                Assert.True(width - chrome - columns.TagListWidth - columns.PanelWidth >= CombinedContentMinimum, layout.Format() + " @" + width);
                if (layout.TagList)
                    Assert.InRange(columns.TagListWidth, MeowChan.Config.MediaTagListWidthMinimum, MeowChan.Config.MediaTagListWidthMaximum);
            }
    }

    // ---- MediaPanelKeys.Route ----

    private static MediaPanelKeyRoute Route(string? matched, Key key = Key.None, KeyModifiers modifiers = KeyModifiers.None, bool panelShown = true,
        bool videoOffered = true, bool nativeInput = false, bool fromPanel = false, string? alsoMatched = null) =>
        MediaPanelKeys.Route(key, modifiers, id => id == matched || id == alsoMatched, panelShown, videoOffered, nativeInput, fromPanel);

    [Fact]
    public void ThePanelHasNoArrowCommandsAndTheGridsArrowKeysAreNoPanelKeys()
    {
        // The panel shows exactly one medium: no previous/next command, no route that steps. The grid's own arrow keys are not panel keys.
        Assert.DoesNotContain(CommandCatalog.All, command => command.Id is "media.panel.previous" or "media.panel.next");
        Assert.Equal(["None", "Video", "ReturnToGrid"], Enum.GetNames<MediaPanelKeyKind>());
        Assert.Equal(MediaPanelKeyRoute.None, Route(null, Key.Right));
        Assert.Equal(MediaPanelKeyRoute.None, Route(null, Key.Left));
    }

    [Fact]
    public void EveryVideoCommandActsOnThePanelVideoOnlyWhileOneIsOffered()
    {
        foreach ((string id, ReaderVideoAction action) in ReaderVideoKeys.Commands)
        {
            Assert.Equal(new MediaPanelKeyRoute(MediaPanelKeyKind.Video, action), Route(id, fromPanel: false));
            Assert.Equal(new MediaPanelKeyRoute(MediaPanelKeyKind.Video, action), Route(id, fromPanel: true));
            Assert.Equal(MediaPanelKeyRoute.None, Route(id, videoOffered: false));
        }
        // Space without a video stays the grid's selection key.
        Assert.Equal(MediaPanelKeyRoute.None, Route("video.play-pause", Key.Space, videoOffered: false));
        Assert.Equal(MediaPanelKeyRoute.None, Route("gallery.open", Key.Enter));
    }

    [Fact]
    public void AHiddenPanelOrATextFieldTakesNoKey()
    {
        foreach (string id in new[] { "video.play-pause", "video.mute" })
        {
            Assert.Equal(MediaPanelKeyRoute.None, Route(id, panelShown: false));
            Assert.Equal(MediaPanelKeyRoute.None, Route(id, nativeInput: true));
            Assert.Equal(MediaPanelKeyRoute.None, Route(id, nativeInput: true, fromPanel: true));
        }
        Assert.Equal(MediaPanelKeyRoute.None, Route(null, Key.Escape, panelShown: false, fromPanel: true));
        Assert.Equal(MediaPanelKeyRoute.None, Route(null, Key.Escape, nativeInput: true, fromPanel: true));
    }

    [Fact]
    public void EscapeIsNoPanelKeyButALevelOfThePage()
    {
        // S-5: the page's ladder (MediaFocusMode.Escape, MediaPanelKeysTests) ends the focus mode before the panel hands the keyboard back.
        Assert.Equal(MediaPanelKeyRoute.None, Route(null, Key.Escape, fromPanel: true));
        Assert.Equal(MediaPanelKeyRoute.None, Route(null, Key.Escape, fromPanel: true, videoOffered: false));
        Assert.Equal(MediaPanelKeyRoute.None, Route(null, Key.Escape, fromPanel: false));
    }

    [Fact]
    public void AVideoKeyIsGivenWayByAPageCommandThatHasItToo()
    {
        Assert.Equal(new MediaPanelKeyRoute(MediaPanelKeyKind.Video, ReaderVideoAction.Mute), Route("video.mute"));
        Assert.Equal(MediaPanelKeyRoute.None, Route("video.mute", alsoMatched: "media.focus-mode"));
        Assert.Equal(new MediaPanelKeyRoute(MediaPanelKeyKind.Video, ReaderVideoAction.PlayPause), Route("video.play-pause", Key.Escape, fromPanel: true));
    }
}
