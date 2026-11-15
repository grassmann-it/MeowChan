using System.Runtime.CompilerServices;
using Avalonia;
using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// GL2 (PLAN-GALERIE-LAYOUT-BLOECKE §6): the docked dock hosts the video bar, so the video bar, the tools and the info are one surface and
// the gap between the picture and the bar is gone. The rules are pure; the viewer's wiring is pinned in its source (no window here).
public sealed class GalleryDockTransportTests
{
    [Theory]
    [InlineData(false, false, "Dock")]
    [InlineData(false, true, "Player")] // the Windows transparency fallback keeps the bar under the picture
    [InlineData(true, false, "Overlay")]
    [InlineData(true, true, "Overlay")] // overlay controls never coexist with the fallback; the footer wins anyway
    public void TheVideoBarLivesInTheDockTheFooterOrUnderThePicture(bool overlay, bool fallback, string expected) =>
        Assert.Equal(Enum.Parse<GalleryTransportHostKind>(expected), GalleryTransportPlacement.Resolve(overlay, fallback));

    [Theory]
    [InlineData(true, "Dock", true, true)]
    [InlineData(false, "Dock", true, false)] // an image: the dock has no video row, it is three rows
    [InlineData(true, "Dock", false, false)] // the player is hidden: the moved bar must not stay behind
    [InlineData(true, "Overlay", true, false)] // the footer's own host shows it
    [InlineData(true, "Player", true, false)]
    public void TheDockRowShowsOnlyForAShownVideoInTheDockedPlacement(bool transport, string host, bool videoShown, bool expected) =>
        Assert.Equal(expected, GalleryTransportPlacement.DockRowVisible(transport, Enum.Parse<GalleryTransportHostKind>(host), videoShown));

    [Theory]
    [InlineData(false, true, 8, 0, 8, 0)]
    [InlineData(false, false, 8, 0, 8, 8)]
    [InlineData(true, true, 8, 8, 8, 0)] // the fallback keeps the strip's 8 dip above the tools
    [InlineData(true, false, 8, 8, 8, 8)]
    public void TheDockTouchesThePictureSoNoGapRemains(bool fallback, bool infoFollows, double left, double top, double right, double bottom) =>
        Assert.Equal(new Thickness(left, top, right, bottom), GalleryTransportPlacement.DockMargin(fallback, infoFollows));

    // GL1+GL2 (integration): a bottom filmstrip shown in the dock is its first row and carries the dock's top margin; the tools surface
    // (video bar row, tools) follows it without a gap, in the fallback too.
    [Theory]
    [InlineData(false, true, 8, 0, 8, 0)]
    [InlineData(false, false, 8, 0, 8, 8)]
    [InlineData(true, true, 8, 0, 8, 0)]
    [InlineData(true, false, 8, 0, 8, 8)]
    public void UnderABottomFilmstripTheToolsSurfaceFollowsTheBandWithoutAGap(bool fallback, bool infoFollows, double left, double top, double right, double bottom) =>
        Assert.Equal(new Thickness(left, top, right, bottom), GalleryTransportPlacement.DockMargin(fallback, infoFollows, filmstripAbove: true));

    [Fact]
    public void TheDockOrderIsFilmstripVideoBarToolsInfoAndOnlyTheFirstRowHasATopMargin()
    {
        string chrome = Source("Desktop/GalleryViewer.Chrome.cs"), viewer = Source("Desktop/GalleryViewer.cs"), filmstrip = Source("Desktop/GalleryViewer.Filmstrip.cs");
        // The dock (GL1): the band is inserted first; the tools surface (_chrome, whose first row is the video bar, GL2) and the info follow.
        Assert.Contains("MoveOverlayControlAt(_filmstrip, _dockStack, 0)", filmstrip);
        int tools = viewer.IndexOf("_dockStack.Children.Add(_chrome);", StringComparison.Ordinal), info = viewer.IndexOf("_dockStack.Children.Add(_infoOverlay);", StringComparison.Ordinal);
        Assert.True(tools > 0 && info > tools);
        Assert.Contains("_chrome.Child = WithDockTransport(WithTaggingStrip(toolbar));", viewer);
        // The band's top margin is the dock's first-row margin; the tools surface yields its own while the band heads the dock.
        Assert.Contains("GalleryFilmstripPlan.Place(plan.FilmstripDock, overlay, show, tile, TopRowClearance, panelRight, _nativeOverlayFallback);", filmstrip);
        Assert.Contains("bool headsDock = show && place.Host == GalleryFilmstripPlan.Host.Dock;", filmstrip);
        Assert.Contains("_filmstripHeadsDock = headsDock; ApplyDockMargin();", filmstrip);
        Assert.Equal(1, chrome.Split("_chrome.Margin =").Length - 1); // one place sets it
        Assert.Contains("ApplyDockMargin();", chrome[chrome.IndexOf("private void ApplyChrome", StringComparison.Ordinal)..]);
    }

    [Theory]
    [InlineData(WorkView.Barebones)]
    [InlineData(WorkView.Simple)]
    [InlineData(WorkView.Advanced)]
    public void TheVideoBarIsAPlanBlockThatNeitherTheLayoutChoiceNorTheHiddenControlsTakeAway(WorkView view)
    {
        foreach (GalleryChromeMode mode in new[] { GalleryChromeMode.View, GalleryChromeMode.Tag })
            foreach (GalleryLayoutChoice choice in new GalleryLayoutChoice[] { new(), new(Tools: false, Info: false, Hints: false, Filmstrip: false) })
                foreach (bool mediaFocus in new[] { false, true })
                    foreach (bool hidden in new[] { false, true })
                    {
                        GalleryChromePlan plan = GalleryChromePlan.Resolve(new(mode, view, choice, mediaFocus, Overlay: hidden, hidden, Video: true,
                            PanelOpen: false, PanelCoupled: false, 1920, 1030));
                        Assert.True(plan.Transport);
                    }
        Assert.False(GalleryChromePlan.Resolve(new(GalleryChromeMode.View, view, new(), false, false, false, Video: false, false, false, 1920, 1030)).Transport);
    }

    [Fact]
    public void TheRealHiddenStateReachesTheInputButNotThePlanTheChromeIsAppliedFrom()
    {
        string chrome = Source("Desktop/GalleryViewer.Chrome.cs");
        // The applied plan is the layout wish: the filmstrip's peek, the hints and the slideshow toggle ask it, and a hidden footer hands every block back.
        Assert.Contains("private GalleryChromePlan BuildChromePlan() => GalleryChromePlan.Resolve(BuildChromeInput());", chrome);
        Assert.Contains("ControlsHidden: false,", chrome);
        // The state itself: only the overlay placement can hide the controls.
        Assert.Contains("internal bool ControlsHiddenNow => OverlayControlsEffective && _overlayHidden;", chrome);
        Assert.Contains("BuildChromeInput() with { ControlsHidden = ControlsHiddenNow }", chrome);
        Assert.Contains("internal GalleryChromePlan ShownChromePlan => GalleryChromePlan.Resolve(ChromeInput);", chrome);
    }

    [Fact]
    public void TheViewerHostsTheVideoBarInTheDockAboveTheTools()
    {
        string chrome = Source("Desktop/GalleryViewer.Chrome.cs"), overlay = Source("Desktop/GalleryViewer.Overlay.cs"), viewer = Source("Desktop/GalleryViewer.cs");
        Assert.Contains("Name = \"GalleryDockTransportHost\"", chrome);
        // The host is the dock's first row: the tab and reading order follow the picture, the bar, then the tools.
        int host = chrome.IndexOf("dock.Children.Add(_dockTransportHost);", StringComparison.Ordinal), tools = chrome.IndexOf("dock.Children.Add(tools);", StringComparison.Ordinal);
        Assert.True(host > 0 && tools > host);
        Assert.Contains("Spacing = 4", chrome[(chrome.IndexOf("private Control WithDockTransport", StringComparison.Ordinal))..]);
        Assert.Contains("_chrome.Child = WithDockTransport(WithTaggingStrip(toolbar));", viewer);
        Assert.Contains("Margin = new Thickness(8, 0, 8, 0), Padding = new Thickness(8, 4), HorizontalAlignment = HorizontalAlignment.Stretch }; // GL2", viewer);
        // The overlay footer keeps its host; the docked placement moves the bar into the dock; the fallback sends it back under the picture.
        Assert.Contains("_video?.SetExternalTransportHost(_overlayTransportHost);", overlay);
        Assert.Contains("if (DockHostsTransport) _video?.SetExternalTransportHost(_dockTransportHost);", overlay);
        Assert.Contains("if (!DockHostsTransport) _video?.SetExternalTransportHost(null);", overlay);
        // (integration: the third argument is GL1's band heading the dock; see UnderABottomFilmstripTheToolsSurfaceFollowsTheBandWithoutAGap)
        Assert.Contains("GalleryTransportPlacement.DockMargin(_nativeOverlayFallback, _infoOverlay.IsVisible, _filmstripHeadsDock)", chrome);
        // The dock row's visibility is a plan block decided in ApplyChrome only.
        Assert.Equal(1, chrome.Split("_dockTransportHost.IsVisible =").Length - 1);
        Assert.DoesNotContain("_dockTransportHost.IsVisible", overlay + viewer);
        // The player is hosted, not changed: the transport keeps its own seek, volume, rotate and zoom.
        Assert.DoesNotContain("_dockTransportHost", Source("Desktop/GalleryVideoPlayer.cs"));
    }

    [Fact]
    public void ADockedBarSharesTheToolsInsetAndErrorsAndTheWayBackStayOutOfThePlan()
    {
        string overlay = Source("Desktop/GalleryViewer.Overlay.cs");
        // GL5b: the padding comes from the ".in-dock" class of the dock's host (the player sets it on its bar), not from a local value, so the
        // overlay footer and the Windows fallback keep the bar's own padding and the dock gets 0 at the sides and 4 above and below.
        Assert.Contains("transport.ClearValue(Border.PaddingProperty);", overlay);
        Assert.DoesNotContain("transport.Padding =", overlay);
        Assert.Contains("Classes = { \"in-dock\" }", Source("Desktop/GalleryViewer.Chrome.cs"));
        Assert.Contains("_transportPanel.Classes.Set(\"in-dock\", host != null && host.Classes.Contains(\"in-dock\"));", Source("Desktop/GalleryVideoPlayer.cs"));
        Assert.Contains("Border.workspace-media-transport.in-dock\"><Setter Property=\"Padding\" Value=\"0,4\" />", Source("Desktop/WorkspaceStyles.axaml"));
        // The way back (_close) and the safety lines are not blocks of the plan, so no plan state can hide them.
        string chrome = Source("Desktop/GalleryViewer.Chrome.cs");
        Assert.DoesNotContain("_close.IsVisible", chrome);
        Assert.DoesNotContain("_favoriteError.IsVisible", chrome);
        Assert.DoesNotContain("_warnings.IsVisible", chrome);
    }

    // GL5b (UX spec S1/S3): the dock's rows are 40 / 40 / 20 dip: the video bar 32 + 4 + 4, the tools 32 with the dock's 8/4 inside, the
    // info one line with 8/2; the play button is 32 like every control of the bar.
    [Fact]
    public void TheDockRowsAreFortyFortyAndTwentyDipWithAThirtyTwoPlayButton()
    {
        string viewer = Source("Desktop/GalleryViewer.cs"), player = Source("Desktop/GalleryVideoPlayer.cs");
        Assert.Contains("Padding = new Thickness(8, 4), HorizontalAlignment = HorizontalAlignment.Stretch }; // GL2", viewer);
        Assert.Contains("Padding = new Thickness(8, 2), HorizontalAlignment = HorizontalAlignment.Stretch }; // GL5b", viewer);
        Assert.Contains("Name = \"GalleryVideoPlayPause\", Classes = { \"media-control\", \"media-play\" }, Width = 32, Height = 32, CornerRadius = new CornerRadius(16)", player);
        Assert.Contains("StackPanel dock = new() { Spacing = 4 };", Source("Desktop/GalleryViewer.Chrome.cs")); // the 4 dip between the rows
    }

    private static string Source(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
