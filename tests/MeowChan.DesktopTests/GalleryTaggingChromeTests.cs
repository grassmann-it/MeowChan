using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// PLAN-TAGGING-GALERIE-UX-1.1 §6, QW1 to QW5: the gallery says everything once while the tagging mode runs (one position, one status
// line, one rating), the slideshow line stays out of the mode, and the slideshow's seconds and mode live under More. The rules are pure
// functions (GalleryTaggingChrome, SlideshowStatusInput); what needs a window (the strip, the panel, focus) is covered by the
// tagging-mode smoke.
[Collection("Localization")]
public sealed class GalleryTaggingChromeTests : IDisposable
{
    public GalleryTaggingChromeTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- QW1: the position once ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TheTagPanelHeadShowsNoPositionInTheModeAndKeepsItOutsideIt(bool hasPosition, bool mode)
    {
        GalleryTagPanelPosition? position = hasPosition ? new(3, 27, false) : null;
        Assert.Equal(mode ? null : position, GalleryChromePlan.PanelCounter(position, mode));
    }

    [Theory]
    [InlineData("Queue 3 of 27", false, null)]            // the arrows are on the screen: they count, the strip does not repeat it
    [InlineData("Queue 3 of 27", true, "Queue 3 of 27")]  // the controls are hidden and the strip peeks in alone: it counts
    [InlineData(null, true, null)]
    public void TheFilmstripRepeatsThePositionOnlyWhileTheArrowsAreAway(string? position, bool controlsHidden, string? expected) =>
        Assert.Equal(expected, GalleryChromePlan.FilmstripPosition(position, controlsHidden));

    [Fact]
    public void ThePositionHooksOfTheViewerUseTheRules()
    {
        string panel = Source("Desktop/GalleryViewer.TagPanel.cs");
        Assert.Contains("GalleryChromePlan.PanelCounter(_tagOwner?.CounterPosition ?? _sidebarPosition, IsTagMode)",
            Source("Desktop/GalleryViewer.SidePanel.cs"));
        Assert.DoesNotContain("surface.CounterText =", panel);
        string filmstrip = Source("Desktop/GalleryViewer.Filmstrip.cs");
        Assert.Contains("GalleryChromePlan.FilmstripPosition(_compactPosition.Text, OverlayControlsEffective && _overlayHidden)", filmstrip);
        Assert.DoesNotContain("_filmstrip.SetPositionText(_compactPosition.Text)", filmstrip);
        string mode = Source("Desktop/GalleryViewer.TaggingMode.cs");
        Assert.Equal(2, CountOf(mode, "RefreshPanelCounter();")); // the mode starts and ends under an open panel
        // The strip's block disappears with its text, so no gap stays.
        Assert.Contains("_position.IsVisible = !string.IsNullOrEmpty(text);", Source("Desktop/Controls/GalleryFilmstrip.cs"));
    }

    // ---- QW2: the status line once ------------------------------------------------------------------------------------------

    [Fact]
    public void TheStripStatusLineYieldsToThePanelAndComesBackWithoutIt()
    {
        Assert.False(GalleryChromePlan.StripStatusVisible(panelCoupled: true));
        Assert.True(GalleryChromePlan.StripStatusVisible(panelCoupled: false));
        string mode = Source("Desktop/GalleryViewer.TaggingMode.cs");
        Assert.Contains("_taggingStatus.IsVisible = GalleryChromePlan.StripStatusVisible(panelCoupled: true);", mode);
        Assert.Contains("_taggingStatus.IsVisible = GalleryChromePlan.StripStatusVisible(panelCoupled: false);", mode);
        // The first sentence goes through the one line (the panel's while it is open); it is no longer written to the strip alone.
        Assert.Contains("SayTagging(TaggingModeText.Start(walk));", mode);
        Assert.DoesNotContain("_taggingStatus.Text = TaggingModeText.Start(walk);", mode);
        // Every sentence is remembered in the strip's line (shown when the panel closes) and announced by the panel while it is open.
        Assert.Contains("_taggingStatus.Text = text;\n        _taggingSurface?.Announce(text);", mode.Replace("\r\n", "\n"));
    }

    // ---- QW3: no slideshow line in the mode ---------------------------------------------------------------------------------

    private static SlideshowStatusInput PausedByContext(bool tagging) =>
        new(Selected: true, Running: false, Started: true, Terminal: false, HasMedia: true, Failed: false, Loading: false,
            Context: true, Help: false, Menu: false, Active: true, SeekPaused: false, Tagging: tagging);

    [Fact]
    public void TheSlideshowStatusIsNeverShownInTheTaggingModeAndKeepsItsRulesOutsideIt()
    {
        Assert.Equal(SlideshowStatus.Context, PausedByContext(tagging: false).Resolve());
        Assert.Equal(SlideshowStatus.None, PausedByContext(tagging: true).Resolve());
        // The other lines of the slideshow are as silent: a tag panel, help or menu is not a reason to talk about the slideshow.
        Assert.Equal(SlideshowStatus.None, (PausedByContext(true) with { Context = false, Help = true }).Resolve());
        Assert.Equal(SlideshowStatus.None, (PausedByContext(true) with { Context = false, Loading = true }).Resolve());
        Assert.Equal(SlideshowStatus.None, (PausedByContext(true) with { Context = false, Active = false }).Resolve());
        // A failed medium is an error and is never hidden.
        Assert.Equal(SlideshowStatus.Failed, (PausedByContext(true) with { Failed = true }).Resolve());
        // The default (no argument) is the old behaviour.
        Assert.Equal(SlideshowStatus.Context, new SlideshowStatusInput(true, false, true, false, true, false, false, true, false, false, true, false).Resolve());
    }

    [Fact]
    public void TheViewerFillsTheModeIntoTheInputAndRefreshesWhenTheModeStartsAndEnds()
    {
        Assert.Contains("Tagging: IsTagMode, TagHold: _taggingHold).Resolve()", Source("Desktop/GalleryViewer.Playback.cs"));
        string mode = Source("Desktop/GalleryViewer.TaggingMode.cs");
        Assert.Equal(2, CountOf(mode, "RefreshPlaybackControls();"));
    }

    // ---- QW4: seconds and mode under More (E2) ------------------------------------------------------------------------------

    [Fact]
    public void SecondsAndModeAreMenuRowsInEveryView()
    {
        foreach (string id in new[] { "gallery.toolbar.slideshow-seconds", "gallery.toolbar.playback-mode" })
            foreach (WorkView view in WorkViewSetting.All)
                Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve(id, view));
        // The slideshow toggle itself stays the one slideshow control on the bar.
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("gallery.toolbar.slideshow", WorkView.Advanced));
    }

    // ---- QW5: the rating once -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, true)]  // plain gallery: the info line shows the rating as before
    [InlineData(false, true, false)]  // the panel's row shows it: the info line does not repeat it
    [InlineData(true, false, false)]  // tools hidden: no info line
    [InlineData(true, true, false)]
    public void TheInfoLineRatingYieldsToThePanelsRow(bool mediaFocus, bool rowVisible, bool expected) =>
        Assert.Equal(expected, GalleryChromePlan.InfoRatingVisible(mediaFocus, rowVisible));

    [Fact]
    public void TheViewerRefreshesTheInfoLineWhenThePanelOpensAndCloses()
    {
        // G1: the rule is a case of the chrome plan, and the viewer applies the plan's answer in its one place (ApplyChrome).
        Assert.Contains("_ratingStatus.IsVisible = plan.InfoRating;", Source("Desktop/GalleryViewer.Chrome.cs"));
        Assert.Contains("InfoRating: info && InfoRatingVisible(mediaFocus: false, input.PanelDecisions)", Source("Desktop/GalleryChromePlan.cs"));
        Assert.Contains("PanelDecisions = DecisionRowVisible", Source("Desktop/GalleryViewer.Chrome.cs"));
        string panel = Source("Desktop/GalleryViewer.TagPanel.cs");
        // The post segment also owns the row now; this source contract keeps the info-line hand-off
        // in sync for both the tag and post panel paths.
        Assert.Contains("internal bool DecisionRowVisible => _tagPanelOpen ? _decisionRow != null : PostContextVisible && _entry != null;", panel);
        Assert.Equal(2, CountOf(panel, "ApplyChrome();"));
    }

    // ---- helpers ------------------------------------------------------------------------------------------------------------

    private static int CountOf(string text, string needle) => (text.Length - text.Replace(needle, "", StringComparison.Ordinal).Length) / needle.Length;

    private static string Source(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
