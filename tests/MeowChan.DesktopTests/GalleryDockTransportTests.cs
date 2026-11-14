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
        Assert.Contains("Margin = new Thickness(8, 0, 8, 0), HorizontalAlignment = HorizontalAlignment.Stretch }; // GL2", viewer);
        // The overlay footer keeps its host; the docked placement moves the bar into the dock; the fallback sends it back under the picture.
        Assert.Contains("_video?.SetExternalTransportHost(_overlayTransportHost);", overlay);
        Assert.Contains("if (DockHostsTransport) _video?.SetExternalTransportHost(_dockTransportHost);", overlay);
        Assert.Contains("if (!DockHostsTransport) _video?.SetExternalTransportHost(null);", overlay);
        Assert.Contains("GalleryTransportPlacement.DockMargin(_nativeOverlayFallback, _infoOverlay.IsVisible)", chrome);
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
        Assert.Contains("if (DockHostsTransport) transport.Padding = new Thickness(0); else transport.ClearValue(Border.PaddingProperty);", overlay);
        // The way back (_close) and the safety lines are not blocks of the plan, so no plan state can hide them.
        string chrome = Source("Desktop/GalleryViewer.Chrome.cs");
        Assert.DoesNotContain("_close.IsVisible", chrome);
        Assert.DoesNotContain("_favoriteError.IsVisible", chrome);
        Assert.DoesNotContain("_warnings.IsVisible", chrome);
    }

    private static string Source(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
