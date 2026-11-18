using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan.DesktopTests;

// The Media page's media panel (1.1.0 split view): only the pure plans are pinned here: the body row's widths (MediaPanelColumns),
// and its key routing (MediaPanelKeys.Route). The page itself is driven in the media-page smoke.
public sealed class MediaPanelTests
{
    private const double GridMinimum = MediaPageLayoutPlan.MinimumGridWidth;
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
    [InlineData(999, false, true)]
    [InlineData(1000, true, false)]
    [InlineData(1001, true, false)]
    public void ThePanelHidesByItselfBelowTheMinimumPageWidthAndTheChoiceStays(double bodyWidth, bool shown, bool collapsed)
    {
        MediaPanelColumns columns = MediaPanelColumns.Of(MediaPageLayout.Standard, bodyWidth, MinimumPage);
        Assert.Equal(shown, columns.PanelShown);
        Assert.Equal(collapsed, columns.CollapsedByWidth);
        Assert.Equal(shown ? 360 : 0, columns.PanelWidth);
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
        // Below its range it counts as its minimum (600), above as its maximum (3000).
        Assert.False(MediaPanelColumns.Of(MediaPageLayout.Standard, 599, 100).PanelShown);
        Assert.True(MediaPanelColumns.Of(MediaPageLayout.Standard, 600, 100).PanelShown);
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
    public void WithoutRoomForTheToolbarAndBothSidebarsThePanelHidesAndTheTagListGetsItsWidthBack()
    {
        double narrowest = CombinedContentMinimum + Chrome + MeowChan.Config.MediaTagListWidthMinimum + MeowChan.Config.MediaPanelWidthMinimum;
        Assert.Equal(new MediaPanelColumns(false, true, 256, 0), MediaPanelColumns.Of(WithTagList(), narrowest - 1, 600));
    }

    [Fact]
    public void AShownPanelNeverLeavesTheGridUnderItsMinimum()
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
                Assert.True(width - Chrome - columns.TagListWidth - columns.PanelWidth >= GridMinimum, layout.Format() + " @" + width);
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
