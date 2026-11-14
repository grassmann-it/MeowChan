using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// Plan AP5 WV4: the work-view rows of the gallery viewer and the page contracts that keep them honest. The viewer needs a
/// platform (icons, popups), so the visual proof (what each view shows, ⋯ menu, focus, shortcuts) lives in the work-views-gallery
/// smoke; these tests pin the policy and the source contracts the smoke cannot see.
/// </summary>
public sealed class WorkViewGalleryTests
{
    private static IEnumerable<WorkViewEntry> Rows => WorkViewPolicy.Entries.Where(entry => entry.Page == WorkViewPage.Gallery);

    [Fact]
    public void WayBackFavouritePositionAndTransportAreRequiredInEveryView()
    {
        foreach (string id in new[] { "gallery.back", "gallery.nav.previous", "gallery.nav.next", "gallery.position", "gallery.favorite",
                     "gallery.favorite.indicator", "gallery.video.transport", "gallery.toolbar.tools-toggle", "gallery.toolbar.more",
                     "gallery.toolbar.read-posts" })
            foreach (WorkView view in WorkViewSetting.All)
                Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view));
    }

    [Fact]
    public void ErrorsIntegrityWarningsAndConsentAreSafetyRowsRequiredInEveryView()
    {
        string[] safety =
        [
            "gallery.info.provenance", "gallery.status.media-unavailable", "gallery.status.file-only", "gallery.status.favorite-error",
            "gallery.status.rating", "gallery.status.slideshow", "gallery.status.overlay-fallback", "gallery.favorite.confirm",
            "gallery.favorite.indicator"
        ];
        foreach (string id in safety)
        {
            Assert.True(WorkViewPolicy.Get(id).Safety, id);
            foreach (WorkView view in WorkViewSetting.All) Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view));
        }
    }

    [Fact]
    public void TheCuratingToolsMoveUnderMoreInBarebonesAndOntoTheBarFromSimpleOn()
    {
        Assert.Empty(WorkViewPolicy.GalleryBarToolIds(WorkView.Barebones));
        Assert.Equal(["gallery.toolbar.rating", "gallery.toolbar.tags", "gallery.toolbar.slideshow"],
            WorkViewPolicy.GalleryBarToolIds(WorkView.Simple));
        // Advanced adds the shortcut help. E2: the slideshow's seconds and playback mode are settings, not tools: under More in every view.
        Assert.Equal(["gallery.toolbar.rating", "gallery.toolbar.tags", "gallery.toolbar.slideshow", "gallery.toolbar.help"],
            WorkViewPolicy.GalleryBarToolIds(WorkView.Advanced));
    }

    [Fact]
    public void TheSlideshowSecondsAndModeLeaveTheBarInEveryViewEvenWhileASlideshowRuns()
    {
        string[] settings = ["gallery.toolbar.slideshow-seconds", "gallery.toolbar.playback-mode"];
        foreach (string id in settings)
            foreach (WorkView view in WorkViewSetting.All)
            {
                Assert.Equal(Presence.Overflow, WorkViewPolicy.Resolve(id, view));
                Assert.DoesNotContain(id, WorkViewPolicy.GalleryBarToolIds(view, slideshowActive: true));
                Assert.Contains(id, WorkViewPolicy.GalleryMovableToolIds); // still placed (menu rows with captions), never dropped
            }
    }

    [Fact]
    public void AChosenOrRunningSlideshowKeepsItsToggleOnTheBarInEveryView()
    {
        Assert.DoesNotContain("gallery.toolbar.slideshow", WorkViewPolicy.GalleryBarToolIds(WorkView.Barebones));
        foreach (WorkView view in WorkViewSetting.All)
            Assert.Contains("gallery.toolbar.slideshow", WorkViewPolicy.GalleryBarToolIds(view, slideshowActive: true));
        // The seconds number is not raised by the state: it stays in the menu in Barebones, as the menu always held it.
        Assert.DoesNotContain("gallery.toolbar.slideshow-seconds", WorkViewPolicy.GalleryBarToolIds(WorkView.Barebones, slideshowActive: true));
    }

    [Fact]
    public void MovableToolsKeepOneOrderForBarAndMenu()
    {
        string[] ids = WorkViewPolicy.GalleryMovableToolIds.ToArray();
        Assert.Equal(ids.OrderBy(id => WorkViewPolicy.Get(id).OverflowOrder), ids);
        Assert.Equal(ids.Length, ids.Select(id => WorkViewPolicy.Get(id).OverflowOrder).Distinct().Count());
        Assert.All(ids, id => Assert.Equal(WorkViewKind.Action, WorkViewPolicy.Get(id).Kind));
    }

    [Fact]
    public void OnlyTheWhereAmILineTheFilmstripAndTheModeSwitchAreLeftOutAndTheirAlternativesExist()
    {
        // The filmstrip (V3) is away in Barebones by default; the L key, the menu row and the setting bring it back in every view.
        // G1: Barebones leaves the mode switch away until the list can be walked or the mode runs (states, R6); Shift+T works anyway.
        Assert.Equal(["gallery.mode", "gallery.filmstrip", "gallery.info.scope"], Rows.Where(entry => WorkViewSetting.All.Any(view => entry.For(view) == Presence.Hidden))
            .Select(entry => entry.Id).ToArray());
        Assert.Equal("gallery.mode-toggle", WorkViewPolicy.Get("gallery.mode").Alternative);
        Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.mode", WorkView.Barebones, nonDefaultState: true)));
        WorkViewEntry scope = WorkViewPolicy.Get("gallery.info.scope");
        Assert.Equal(Presence.Hidden, scope.Barebones);
        Assert.Equal("media.details", scope.Alternative);
        Assert.False(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.info.scope", WorkView.Barebones)));
        Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.info.scope", WorkView.Simple)));
        // R6: a state raises it.
        Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.info.scope", WorkView.Barebones, nonDefaultState: true)));
    }

    [Fact]
    public void ThePostPanelAndPictureToolsStayOnTheBarInEveryView()
    {
        foreach (string id in new[] { "gallery.toolbar.post-panel", "gallery.post-panel", "gallery.toolbar.fit", "gallery.toolbar.actual" })
            foreach (WorkView view in WorkViewSetting.All)
                Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(id, view)), id);
    }

    [Fact]
    public void RowsTheNoViewMovesStayUnderMoreAndKeepTheirShortcutCommand()
    {
        foreach (WorkViewEntry entry in Rows.Where(entry => entry.Id.StartsWith("gallery.menu.", StringComparison.Ordinal)))
            foreach (WorkView view in WorkViewSetting.All)
                Assert.Equal(Presence.Overflow, entry.For(view));
    }

    [Fact]
    public void DialogsOfReducedCommandsReturnFocusToMore()
    {
        // Barebones keeps rating, tags and the ratings manager under More; Simple brings rating and tags onto the bar.
        Assert.True(WorkViewPolicy.GalleryCommandReduced("media.rating.edit", WorkView.Barebones));
        Assert.True(WorkViewPolicy.GalleryCommandReduced("media.tags.edit", WorkView.Barebones));
        Assert.False(WorkViewPolicy.GalleryCommandReduced("media.rating.edit", WorkView.Simple));
        Assert.False(WorkViewPolicy.GalleryCommandReduced("media.tags.edit", WorkView.Advanced));
        foreach (WorkView view in WorkViewSetting.All)
        {
            Assert.True(WorkViewPolicy.GalleryCommandReduced("media.rating.manage", view));
            Assert.False(WorkViewPolicy.GalleryCommandReduced("favorites.toggle", view));
            Assert.False(WorkViewPolicy.GalleryCommandReduced("gallery.close", view));
            Assert.False(WorkViewPolicy.GalleryCommandReduced("no.such.command", view));
        }
        string tags = ReadSource("Desktop/Shell/MainWindow.GalleryTags.cs");
        Assert.Contains("FocusReturnTarget(origin, \"media.tags.edit\")", tags);
        string ratings = ReadSource("Desktop/Shell/MainWindow.Ratings.cs");
        Assert.Contains("FocusReturnTarget(origin, \"media.rating.edit\")", ratings);
        Assert.Contains("FocusReturnTarget(origin, \"media.rating.manage\")", ratings);
    }

    [Fact]
    public void EveryRowIsRegisteredByTheGalleryFiles()
    {
        string files = string.Concat(Directory.EnumerateFiles(Root("Desktop"), "GalleryViewer*.cs").Select(File.ReadAllText));
        foreach (WorkViewEntry entry in Rows)
            Assert.True(files.Contains("\"" + entry.Id + "\"", StringComparison.Ordinal), entry.Id + " is not registered by a GalleryViewer file.");
    }

    // The shortcuts never depended on where a button sits: the viewer's key handler routes them by command. A moved tool
    // therefore keeps its shortcut in every view (R2); the handler must keep naming each of these commands.
    [Fact]
    public void TheKeyHandlerRoutesEveryMovableToolByItsCommand()
    {
        string viewer = ReadSource("Desktop/GalleryViewer.cs");
        foreach (string command in new[] { "media.tags.edit", "viewer.shortcut-help" })
            Assert.Contains("Matches(e, \"" + command + "\")", viewer);
        // Slideshow and the overlay switches go through PlaybackActions, the rating commands through the viewer's own router.
        Assert.Contains("foreach (var (_, id) in PlaybackActions())", viewer);
        Assert.Contains("(\"media.rating.edit\"", viewer);
        string playback = ReadSource("Desktop/GalleryViewer.Playback.cs");
        Assert.Contains("(_slideshowToggle, \"gallery.slideshow-toggle\")", playback);
    }

    // Tom's rule of 14.11. (replaces "back alone at the top right, the star left of it"): back and close come first at the LEFT, the star
    // directly right of it as a pair, the other actions apart on the right.
    [Fact]
    public void TheWayBackSitsAtTheTopLeftWithTheStarRightOfItAndPanelsBelowWhereTheyReachIt()
    {
        string viewer = ReadSource("Desktop/GalleryViewer.cs");
        Assert.Contains("_close.HorizontalAlignment = HorizontalAlignment.Left;", viewer);
        Assert.DoesNotContain("_close.HorizontalAlignment = HorizontalAlignment.Right;", viewer);
        // A change of its width (a longer label) moves the star and the panel start along.
        Assert.Contains("_close.SizeChanged += (_, _) => { UpdateFavoriteIndicators(); if (_contextOpen) LayoutContextPlacement(); };", viewer);
        string favorites = ReadSource("Desktop/GalleryViewer.Favorites.cs");
        Assert.Contains("StarLeftMargin", favorites);
        Assert.DoesNotContain("StarRightMargin", favorites);
        Assert.Contains("HorizontalAlignment = HorizontalAlignment.Left", favorites);
        Assert.Contains("_close.Bounds.Width", favorites);
        Assert.DoesNotContain("_contextPanel.Width", favorites); // the star no longer steps aside for the panel: the panel steps below it
        // The way back is the first child of the root (first stop in the tab order); the star is a passive badge, so the toolbar follows it.
        Assert.Contains("root.Children.Add(_close); // First child", viewer);
        // Both placements of the post panel (and with it the slot of a later tag panel) start below the top row where the panel reaches the
        // pair at the left (a narrow viewer) and at the top otherwise; the help starts below the top row.
        string overlay = ReadSource("Desktop/GalleryViewer.Overlay.cs");
        Assert.Equal(2, Regex.Matches(overlay, @"_contextPanel\.Margin = new Thickness\([^;]*PostPanelTopInset\(").Count);
        Assert.Contains("TopRowClearance, 12, 12", viewer);
        // The clearance holds the way back (12 margin + a 32 dip button + an 8 gap).
        Assert.True(MeowChan.Desktop.GalleryViewer.TopRowClearance >= 12 + 32 + 8);
    }

    [Fact]
    public void TheWorkViewReachesTheGalleryWhenItOpensAndOnEveryChange()
    {
        Assert.Contains("_gallery.ApplyWorkView(ConfiguredWorkView);", ReadSource("Desktop/Shell/MainWindow.Gallery.cs"));
        string window = ReadSource("Desktop/Shell/MainWindow.WorkViews.cs");
        Assert.Contains("_workViews.Changed += (_, current) => _gallery?.ApplyWorkView(current);", window);
    }

    [Fact]
    public void TheGalleryNeedsNoNewTextsAndKeepsTheStandaloneViewerInTheLayoutItAlwaysHad()
    {
        // Barebones is the layout every gallery smoke and test was written against; the window pushes its own view on open.
        string workView = ReadSource("Desktop/GalleryViewer.WorkView.cs");
        Assert.Contains("private WorkView _workView = WorkView.Barebones;", workView);
        Assert.DoesNotContain("Texts.T(\"workview.gallery", workView);
    }

    private static string ReadSource(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
