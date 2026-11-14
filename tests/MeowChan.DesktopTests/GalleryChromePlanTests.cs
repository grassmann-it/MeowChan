using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// G1 + GL0 (PLAN-TAGGING-GALERIE-UX-1.1 §3/§6, PLAN-GALERIE-LAYOUT-BLOECKE §4): the gallery chrome is one pure plan. These tests cover the
// whole matrix without a window: mode × work view × layout choice × state (GL0), mode × view × width × panel × video (G1), the overflow
// priority, E1's favourite placement, the slideshow hold of the tagging mode, and the source hooks that make the viewer apply the plan.
public sealed class GalleryChromePlanTests
{
    private static readonly WorkView[] Views = [WorkView.Barebones, WorkView.Simple, WorkView.Advanced];

    private static GalleryChromeInput Input(GalleryChromeMode mode = GalleryChromeMode.View, WorkView view = WorkView.Simple,
        GalleryLayoutChoice? choice = null, bool mediaFocus = false, bool overlay = false, bool controlsHidden = false, bool video = false,
        bool panelOpen = false, bool panelCoupled = false, double width = 1920, double height = 1030) =>
        new(mode, view, choice ?? GalleryLayoutChoice.Default, mediaFocus, overlay, controlsHidden, video, panelOpen, panelCoupled, width, height);

    private static GalleryChromePlan Plan(GalleryChromeInput input) => GalleryChromePlan.Resolve(input);

    // ---- GL0: mode × view × choice/state (36 cases) ---------------------------------------------------------------------------

    public static IEnumerable<object[]> Gl0Matrix()
    {
        string[] variants = ["default", "filmstrip-off", "dock-top", "tools-off", "hints-off", "media-focus"];
        foreach (bool tag in new[] { false, true })
            foreach (WorkView view in Views)
                foreach (string variant in variants)
                    yield return [tag, view, variant];
    }

    [Theory, MemberData(nameof(Gl0Matrix))]
    public void TheBlocksFollowViewDefaultThenChoiceThenState(bool tag, WorkView view, string variant)
    {
        GalleryChromeMode mode = tag ? GalleryChromeMode.Tag : GalleryChromeMode.View;
        GalleryLayoutChoice choice = variant switch
        {
            "filmstrip-off" => new(Filmstrip: false),
            "dock-top" => new(FilmstripDock: GalleryFilmstripDock.Top),
            "tools-off" => new(Tools: false),
            "hints-off" => new(Hints: false),
            _ => GalleryLayoutChoice.Default
        };
        bool focus = variant == "media-focus";
        GalleryChromePlan plan = Plan(Input(mode, view, choice, mediaFocus: focus, overlay: true) with { Walk = tag });
        bool filmstripByView = view != WorkView.Barebones; // the policy row "gallery.filmstrip": Hidden in Barebones
        Assert.Equal(!focus && variant != "filmstrip-off" && filmstripByView, plan.Filmstrip);
        Assert.Equal(variant == "dock-top" ? GalleryFilmstripDock.Top : GalleryFilmstripDock.Bottom, plan.FilmstripDock);
        Assert.Equal(!focus && !tag && variant != "tools-off", plan.Tools);
        Assert.Equal(!focus, plan.Info);
        Assert.Equal(!focus && !tag && variant != "hints-off", plan.Hints); // Tag: hints off by the view's default
        Assert.Equal(tag, plan.DecisionRow);
        Assert.Equal(plan.Filmstrip && variant == "dock-top", plan.FilmstripPeek); // overlay on: a top strip peeks, a bottom one never
        Assert.Equal(!focus && !tag && view != WorkView.Barebones, plan.InfoScope);
        Assert.Same(GalleryChromePlan.Priority, plan.BarPriority);
    }

    [Fact]
    public void TheViewDefaultReadsThePolicyAndPutsTheFilmstripAtTheBottom()
    {
        foreach (WorkView view in Views)
        {
            GalleryLayoutChoice viewMode = GalleryChromePlan.ViewDefault(GalleryChromeMode.View, view);
            Assert.Equal(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("gallery.filmstrip", view)), viewMode.Filmstrip);
            Assert.Equal(GalleryFilmstripDock.Bottom, viewMode.FilmstripDock); // Tom, GL E1
            Assert.True(viewMode.Tools == true && viewMode.Info == true && viewMode.Hints == true);
            Assert.False(GalleryChromePlan.ViewDefault(GalleryChromeMode.Tag, view).Hints); // the tag panel speaks
        }
        Assert.Equal(new GalleryLayoutChoice(), GalleryLayoutChoice.Default);
    }

    [Fact]
    public void TheChoiceBeatsTheViewDefaultBothWays()
    {
        Assert.True(Plan(Input(view: WorkView.Barebones, choice: new(Filmstrip: true))).Filmstrip);  // R6: on in a view that leaves it away
        Assert.False(Plan(Input(view: WorkView.Advanced, choice: new(Filmstrip: false))).Filmstrip);
        Assert.True(Plan(Input(GalleryChromeMode.Tag, choice: new(Hints: true))).Hints);
        Assert.False(Plan(Input(GalleryChromeMode.Tag, choice: new(Hints: true), panelCoupled: true)).Hints); // ... the coupled panel speaks
        GalleryLayoutChoice choice = GalleryLayoutChoice.Default.With(GalleryBlock.Info, false);
        Assert.False(choice.Of(GalleryBlock.Info));
        Assert.Null(choice.With(GalleryBlock.Info, null).Of(GalleryBlock.Info));
        GalleryChromePlan noInfo = Plan(Input(choice: choice));
        Assert.False(noInfo.Info || noInfo.InfoQuality || noInfo.InfoScope || noInfo.InfoRating);
    }

    [Fact]
    public void TheInvariantsHold()
    {
        foreach (GalleryChromeMode mode in Enum.GetValues<GalleryChromeMode>())
            foreach (WorkView view in Views)
                foreach (bool focus in new[] { false, true })
                {
                    // The video bar is always there for a video, whatever the state.
                    Assert.True(Plan(Input(mode, view, video: true, mediaFocus: focus, controlsHidden: true)).Transport);
                    Assert.False(Plan(Input(mode, view, video: false)).Transport);
                    // Auto-hidden controls: no block and no tool, only the video bar.
                    GalleryChromePlan hidden = Plan(Input(mode, view, new(Tools: true, Info: true, Hints: true, Filmstrip: true), mediaFocus: focus,
                        controlsHidden: true, video: true));
                    Assert.False(hidden.Filmstrip || hidden.Tools || hidden.Info || hidden.Hints || hidden.DecisionRow || hidden.ToolsToggle
                        || hidden.Nav || hidden.More || hidden.Favorite || hidden.Slideshow || hidden.ModeSwitch);
                    // Tag: the tools go into the decision strip.
                    if (mode == GalleryChromeMode.Tag)
                    {
                        GalleryChromePlan tag = Plan(Input(mode, view, new(Tools: true), mediaFocus: focus) with { Walk = true });
                        Assert.False(tag.Tools || tag.Zoom || tag.PanelToggle || tag.Rating || tag.Tags || tag.Help);
                        Assert.True(tag.DecisionRow);
                        // Tag without a walk: the same Tag layout, but no decisions (they only exist for the tagging queue).
                        GalleryChromePlan free = Plan(Input(mode, view, new(Tools: true), mediaFocus: focus));
                        Assert.False(free.Tools || free.DecisionRow || free.StripTwoRows);
                    }
                }
        // A low viewer leaves the filmstrip away; a not yet laid out one (0) does not.
        Assert.False(Plan(Input(height: 400)).Filmstrip);
        Assert.True(Plan(Input(height: 0)).Filmstrip);
        // An equal input gives an equal plan (no second layout pass).
        Assert.Equal(Plan(Input()), Plan(Input()));
    }

    [Fact]
    public void SafetyRowsAreNotPartOfThePlan()
    {
        string source = Source("Desktop/GalleryChromePlan.cs");
        foreach (string row in new[] { "gallery.status.favorite-error", "gallery.status.media-unavailable", "gallery.info.provenance", "gallery.favorite.confirm", "gallery.back" })
            Assert.DoesNotContain(row, source);
        Assert.Equal(["Filmstrip", "Tools", "Info", "Hints"], Enum.GetNames<GalleryBlock>());
        Assert.Equal(["Top", "Bottom"], Enum.GetNames<GalleryFilmstripDock>());
        Assert.Equal(["View", "Tag"], Enum.GetNames<GalleryChromeMode>());
    }

    // ---- G1: mode × view × width × panel × video ----------------------------------------------------------------------------

    public static IEnumerable<object[]> G1Matrix()
    {
        // Tag runs with a walk of the tagging queue or without one (any gallery); View never has a walk.
        foreach ((bool tag, bool walk) in new[] { (false, false), (true, false), (true, true) })
            foreach (WorkView view in Views)
                foreach (double width in new[] { 1920d, 1200d, 1024d, 640d })
                    foreach (bool panel in new[] { false, true })
                        foreach (bool video in new[] { false, true })
                            yield return [tag, walk, view, width, panel, video];
    }

    [Theory, MemberData(nameof(G1Matrix))]
    public void TheBarFollowsModeWalkViewWidthPanelAndVideo(bool tag, bool walk, WorkView view, double width, bool panel, bool video)
    {
        GalleryChromeMode mode = tag ? GalleryChromeMode.Tag : GalleryChromeMode.View;
        GalleryChromePlan plan = Plan(Input(mode, view, width: width, panelOpen: panel, panelCoupled: walk && panel, video: video)
            with { PanelDecisions = panel, TagAvailable = walk || !tag, Walk = walk });
        // Never reduced: the arrows with the position, "More", the video bar of a video.
        Assert.True(plan.Nav && plan.More);
        Assert.Equal(video, plan.Transport);
        Assert.True(plan.ModeSwitch); // a walkable list raises it in Barebones too
        Assert.Equal(!tag, plan.ToolsToggle);
        Assert.Equal(!tag, plan.Zoom && plan.PanelToggle);
        Assert.Equal(walk, plan.DecisionRow); // Back, Skip, Done & next and the progress only for a walk of the tagging queue
        // E1: in the mode (with or without a walk) the panel's decision row is the one favourite surface; without the panel the button comes back.
        Assert.Equal(tag && panel ? GalleryFavoriteSurface.Panel : GalleryFavoriteSurface.Bar, plan.FavoriteSurface);
        Assert.Equal(!(tag && panel), plan.Favorite);
        // The movable tools: the view's bar, narrowed by the width (help first, then rating, then everything); none in the mode.
        string[] byView = WorkViewPolicy.GalleryBarToolIds(view).ToArray();
        int overflow = width >= 1280 ? 0 : width >= 1100 ? 1 : width >= 900 ? 2 : 4;
        string[] expected = tag ? [] : byView.Where(id => GalleryChromePlan.OverflowOrder.ToList().IndexOf(id) >= overflow).ToArray();
        Assert.Equal(expected, plan.BarToolIds);
        // The info line: quality always; the scope only outside the mode and as the view says; the rating yields to the panel's row.
        Assert.True(plan.InfoQuality);
        Assert.Equal(!tag && view != WorkView.Barebones && width >= GalleryChromePlan.NarrowWidth, plan.InfoScope); // S2: the scope leaves below 900
        Assert.Equal(!panel && !plan.Rating, plan.InfoRating); // GL5c: and the rating button of the bar shows the value too
        Assert.Equal(walk && width < 1280, plan.StripTwoRows);
    }

    [Fact]
    public void TheOverflowPriorityLetsTheLowestToolGoFirstAndNeverTheCore()
    {
        Assert.Equal(["gallery.toolbar.help", "gallery.toolbar.rating", "gallery.toolbar.slideshow", "gallery.toolbar.tags"], GalleryChromePlan.OverflowOrder);
        // The keep-visible order: navigation, More and the tools toggle first; every movable tool of the policy is in it.
        Assert.Equal(["gallery.position", "gallery.toolbar.more", "gallery.toolbar.tools-toggle"], GalleryChromePlan.Priority.Take(3));
        Assert.All(GalleryChromePlan.OverflowOrder, id => Assert.Contains(id, WorkViewPolicy.GalleryMovableToolIds));
        Assert.All(GalleryChromePlan.Priority, id => WorkViewPolicy.Get(id));
        // Advanced on a shrinking viewer: help leaves first, then rating, then the rest; navigation and More stay.
        string[] Bar(double width) => Plan(Input(view: WorkView.Advanced, width: width)).BarToolIds.ToArray();
        Assert.Equal(["gallery.toolbar.rating", "gallery.toolbar.tags", "gallery.toolbar.slideshow", "gallery.toolbar.help"], Bar(1380));
        Assert.Equal(["gallery.toolbar.rating", "gallery.toolbar.tags", "gallery.toolbar.slideshow"], Bar(1279));
        Assert.Equal(["gallery.toolbar.tags", "gallery.toolbar.slideshow"], Bar(1099));
        Assert.Empty(Bar(899));
        // R6: an active slideshow keeps its toggle at every width and with hidden tools.
        Assert.True(Plan(Input(view: WorkView.Barebones, width: 640) with { SlideshowActive = true }).Slideshow);
        Assert.True(Plan(Input(mediaFocus: true) with { SlideshowActive = true }).Slideshow);
        Assert.False(Plan(Input(mediaFocus: true)).Slideshow);
    }

    [Fact]
    public void ThePlanNeverPutsMoreOnTheBarThanTheWorkViewOffers()
    {
        foreach (WorkView view in Views)
            foreach (double width in new[] { 1920d, 1024d, 640d })
                Assert.Empty(Plan(Input(view: view, width: width)).BarToolIds.Except(WorkViewPolicy.GalleryBarToolIds(view)));
    }

    [Fact]
    public void TheSlideshowCoexistsWithTheTaggingModeAndShowsItsToggleOnlyWhileRunning()
    {
        GalleryChromeInput tag = Input(GalleryChromeMode.Tag, WorkView.Advanced, panelOpen: true, panelCoupled: true) with { PanelDecisions = true };
        Assert.False(Plan(tag with { SlideshowActive = true }).Slideshow); // chosen, not running: a row of "More"
        Assert.True(Plan(tag with { SlideshowActive = true, SlideshowRunning = true }).Slideshow); // running: pause is one click away
    }

    [Theory]
    [InlineData(WorkView.Barebones, false, false, false)] // nothing to tag here: Barebones leaves the switch away
    [InlineData(WorkView.Barebones, true, false, true)]   // a walkable list raises it
    [InlineData(WorkView.Barebones, false, true, true)]   // in the mode it is the way out
    [InlineData(WorkView.Simple, false, false, true)]
    [InlineData(WorkView.Advanced, false, false, true)]
    public void TheModeSwitchFollowsTheViewAndTheState(WorkView view, bool available, bool tagging, bool shown) =>
        Assert.Equal(shown, Plan(Input(tagging ? GalleryChromeMode.Tag : GalleryChromeMode.View, view) with { TagAvailable = available }).ModeSwitch);

    [Fact]
    public void TagWithoutAWalkKeepsTheFavouriteInThePanelAndHasNoDecisions()
    {
        GalleryChromeInput free = Input(GalleryChromeMode.Tag, WorkView.Simple, panelOpen: true) with { PanelDecisions = true };
        GalleryChromePlan plan = Plan(free);
        Assert.Equal(GalleryFavoriteSurface.Panel, plan.FavoriteSurface); // E1: the panel's row, not a decision strip
        Assert.False(plan.DecisionRow || plan.Favorite || plan.Rating || plan.Tags || plan.Tools || plan.InfoRating);
        Assert.True(plan.ModeSwitch && plan.Nav && plan.More && plan.InfoQuality);
        // The panel closed: the favourite button is back on the bar, so the favourite stays reachable (and the star shows it anyway).
        GalleryChromePlan closed = Plan(free with { PanelOpen = false, PanelDecisions = false });
        Assert.Equal(GalleryFavoriteSurface.Bar, closed.FavoriteSurface);
        Assert.True(closed.Favorite && closed.InfoRating);
        // The same with a walk adds the decision strip and nothing else of the layout changes.
        Assert.Equal(plan with { DecisionRow = true }, Plan(free with { Walk = true }));
    }

    [Fact]
    public void TheModeSwitchStaysTheWayOutWithHiddenTools()
    {
        Assert.True(Plan(Input(GalleryChromeMode.Tag, mediaFocus: true)).ModeSwitch);
        Assert.True(Plan(Input(GalleryChromeMode.Tag, mediaFocus: true)).ToolsToggle); // and the hidden tools can come back
        Assert.False(Plan(Input(mediaFocus: true)).ModeSwitch);
        Assert.Equal(GalleryFavoriteSurface.KeysOnly, Plan(Input(mediaFocus: true)).FavoriteSurface);
        Assert.Equal(GalleryChromeMode.Tag, GalleryChromePlan.ResolveMode(tagging: true));
        Assert.Equal(GalleryChromeMode.View, GalleryChromePlan.ResolveMode(tagging: false));
    }

    [Fact]
    public void TheFavouriteIsRequiredAndThePanelRowTakesItOverInTheMode()
    {
        foreach (WorkView view in Views) Assert.Equal(Presence.Required, WorkViewPolicy.Resolve("gallery.favorite", view));
        GalleryChromePlan tag = Plan(Input(GalleryChromeMode.Tag, panelOpen: true, panelCoupled: true) with { PanelDecisions = true });
        Assert.Equal(GalleryFavoriteSurface.Panel, tag.FavoriteSurface);
        Assert.False(tag.Favorite || tag.Rating || tag.Tags || tag.InfoRating); // E1: favourite, rating and "Tag…" leave the bar
        // A panel without the decision row (its owner offers none) leaves the bar's button in place.
        Assert.Equal(GalleryFavoriteSurface.Bar, Plan(Input(GalleryChromeMode.Tag, panelOpen: true)).FavoriteSurface);
        Assert.True(Plan(Input(GalleryChromeMode.Tag, panelOpen: true)).Favorite);
        // The passive star is no block: the viewer never hides it for a mode (it is not in the plan).
        Assert.DoesNotContain("_favoriteIndicator", Source("Desktop/GalleryViewer.Chrome.cs"));
    }

    [Fact]
    public void TheHintsSilenceWhatTheSlideshowDoesButNeverWhyItWaits()
    {
        Assert.True(GalleryChromePlan.IsHint(SlideshowStatus.Running));
        Assert.True(GalleryChromePlan.IsHint(SlideshowStatus.Ready));
        foreach (SlideshowStatus reason in new[] { SlideshowStatus.Failed, SlideshowStatus.Context, SlideshowStatus.Paused, SlideshowStatus.TagHold,
                     SlideshowStatus.Inactive, SlideshowStatus.SeekPaused })
            Assert.False(GalleryChromePlan.IsHint(reason));
    }

    // ---- the slideshow in the tagging mode (Tom's E3 addition) -----------------------------------------------------------------

    [Theory]
    [InlineData(true, true, true, false, false, true)]   // the focus is in the panel
    [InlineData(true, true, false, true, false, true)]   // text waits in the field
    [InlineData(true, true, false, false, true, true)]   // the suggestion list is open
    [InlineData(true, true, false, false, false, false)] // the picture has the focus: the slideshow runs on
    [InlineData(false, true, true, true, true, false)]   // outside the mode the open panel pauses instead (PanelBlocks)
    [InlineData(true, false, true, true, true, false)]   // no tag panel
    public void TheTagPanelHoldsTheSlideshowWhileItIsWorkedIn(bool mode, bool open, bool focused, bool text, bool list, bool holds) =>
        Assert.Equal(holds, TaggingSlideshowHold.Holds(mode, open, focused, text, list));

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]  // the post: reading stops the slideshow
    [InlineData(true, false, true, true)]   // the post in the mode too
    [InlineData(true, true, false, true)]   // the tag panel outside the mode: as before
    [InlineData(true, true, true, false)]   // the tag panel in the mode: part of the work, the slideshow may run
    public void OnlyTheTagPanelOfTheModeLetsTheSlideshowRun(bool open, bool tagPanel, bool mode, bool blocks) =>
        Assert.Equal(blocks, TaggingSlideshowHold.PanelBlocks(open, tagPanel, mode));

    [Fact]
    public void TheSlideshowLineOfTheModeIsTheHoldAndAFailure()
    {
        SlideshowStatusInput running = new(Selected: true, Running: true, Started: true, Terminal: false, HasMedia: true, Failed: false,
            Loading: false, Context: false, Help: false, Menu: false, Active: true, SeekPaused: false, Tagging: true);
        Assert.Equal(SlideshowStatus.None, running.Resolve());
        Assert.Equal(SlideshowStatus.TagHold, (running with { TagHold = true }).Resolve());
        Assert.Equal(SlideshowStatus.None, (running with { TagHold = true, Running = false }).Resolve());
        Assert.Equal(SlideshowStatus.Failed, (running with { TagHold = true, Failed = true }).Resolve());
        Assert.Equal(SlideshowStatus.Running, (running with { Tagging = false, TagHold = true }).Resolve()); // only the mode knows the hold
    }

    // ---- the viewer applies the plan in one place ------------------------------------------------------------------------------

    [Fact]
    public void TheViewerAppliesThePlanInOnePlace()
    {
        string chrome = Source("Desktop/GalleryViewer.Chrome.cs");
        Assert.Contains("private GalleryChromePlan BuildChromePlan() => GalleryChromePlan.Resolve(BuildChromeInput());", chrome);
        Assert.Contains("private void ApplyChrome(bool force = false)", chrome);
        string viewer = string.Concat(Directory.EnumerateFiles(Root("Desktop"), "GalleryViewer*.cs").Where(path => !path.EndsWith("GalleryViewer.Chrome.cs"))
            .Select(File.ReadAllText));
        // The blocks the plan decides are set nowhere else.
        foreach (string block in new[] { "_ratingStatus.IsVisible =", "_quality.IsVisible =", "_taggingStrip.IsVisible =", "_toolsToggle.IsVisible =",
                     "_favorite.IsVisible =", "_contextToggle.IsVisible =", "_workViewTools.IsVisible = _workViewTools" })
            Assert.DoesNotContain(block, viewer);
        Assert.DoesNotContain("UpdateMediaFocusVisibility", viewer);
        Assert.Contains("ApplyChrome(force: true);", Source("Desktop/GalleryViewer.WorkView.cs"));
        Assert.Contains("FilmstripEffectiveFor(BuildChromePlan())", Source("Desktop/GalleryViewer.Filmstrip.cs"));
        Assert.Contains("SlideshowBlockedByPanel", Source("Desktop/GalleryViewer.Overlay.cs"));
        Assert.Contains("bool tagHold = UpdateTaggingHold();", Source("Desktop/GalleryViewer.Overlay.cs"));
        Assert.False(File.Exists(Root("Desktop/GalleryTaggingChrome.cs"))); // folded into the plan
    }

    [Fact]
    public void TheModeSwitchHasACommandAKeyAndTheWindowStartsAndEndsTheMode()
    {
        // Tag works in every gallery: the switch is never disabled with an "add to the queue first" reason.
        Assert.DoesNotContain("_modeTag.IsEnabled", Source("Desktop/GalleryViewer.Chrome.cs"));
        Assert.DoesNotContain("gallery.mode.tagUnavailable", Source("Desktop/Localization/GalleryActionsCatalog.cs"));
        Assert.Contains("_gallery.StartFreeTagMode(", Source("Desktop/Shell/MainWindow.TagQueue.cs"));
        Assert.Contains("_gallery.EndFreeTagMode();", Source("Desktop/Shell/MainWindow.TagQueue.cs"));
        // Tag without a walk opens the tag panel on the shown medium; nothing in that path touches the tagging queue.
        string free = Source("Desktop/GalleryViewer.Chrome.cs");
        int start = free.IndexOf("internal void StartFreeTagMode", StringComparison.Ordinal);
        string body = free[start..free.IndexOf("internal void EndFreeTagMode", start, StringComparison.Ordinal)];
        Assert.Contains("ShowTagPanel(owner);", body);
        Assert.DoesNotContain("Queue", body);
        Assert.Contains("CommandBindings.Matches(e, \"gallery.mode-toggle\")", Source("Desktop/GalleryViewer.cs"));
        string chrome = Source("Desktop/GalleryViewer.Chrome.cs");
        Assert.Contains("TaggingModeRequested?.Invoke(tag);", chrome);
        Assert.Contains("AutomationProperties.SetLiveSetting(_modeAnnouncement, AutomationLiveSetting.Polite);", chrome);
        string mode = Source("Desktop/GalleryViewer.TaggingMode.cs");
        Assert.Equal(2, mode.Split("OnGalleryModeChanged();").Length - 1); // StartTaggingMode and EndTaggingMode
        string window = Source("Desktop/Shell/MainWindow.TagQueue.cs");
        Assert.Contains("_gallery.EndTaggingMode();", window);
        Assert.Contains("StartGalleryTagging(parked, list);", window);
        Assert.Contains("_gallery.StartTaggingMode(walk,", window); // the existing ways in stay
        Assert.Contains("SwitchGalleryTagging(tag)", Source("Desktop/Shell/MainWindow.Gallery.cs"));
        WorkViewEntry row = WorkViewPolicy.Get("gallery.mode");
        Assert.Equal("gallery.mode-toggle", row.CommandId);
        Assert.Equal(Presence.Hidden, row.Barebones);
        Assert.True(row.Simple == Presence.Direct && row.Advanced == Presence.Direct);
    }

    [Fact]
    public void AFreeTagModeEndsWithItsPanelAndAWalkKeepsItsMode()
    {
        // M1: every way the tag panel goes (its ×, Ctrl+Shift+T, the side panel's Post segment or ×, the slot closing, the viewer stopping)
        // runs through ReleaseTagPanel; that is where a Tag mode without a walk ends, so the switch shows View and Tag opens the panel again.
        string panel = Source("Desktop/GalleryViewer.TagPanel.cs");
        int release = panel.IndexOf("private bool ReleaseTagPanel()", StringComparison.Ordinal);
        string releaseBody = panel[release..panel.IndexOf("return true;", release, StringComparison.Ordinal)];
        Assert.Contains("EndFreeTagModeWithPanel();", releaseBody);
        Assert.Contains("surface.CloseRequested += CloseTagPanel;", panel);
        Assert.Contains("if (CommandBindings.Matches(e, \"media.tags.edit\")) { CloseTagPanel();", panel);
        Assert.Contains("ReleaseTagPanel();", Source("Desktop/GalleryViewer.SidePanel.cs"));
        Assert.Contains("bool tagOnly = !open && ReleaseTagPanel();", Source("Desktop/GalleryViewer.cs"));

        string chrome = Source("Desktop/GalleryViewer.Chrome.cs");
        int end = chrome.IndexOf("private void EndFreeTagModeWithPanel()", StringComparison.Ordinal);
        Assert.True(end > 0);
        string body = chrome[end..chrome.IndexOf("internal void SetTaggingAvailable", end, StringComparison.Ordinal)];
        // Only the free mode: a walk of the tagging queue keeps its Tag mode and decision row when the panel closes.
        Assert.Contains("if (!_freeTagMode || _tagging != null) return;", body);
        Assert.Contains("_freeTagMode = false;", body);
        Assert.Contains("OnGalleryModeChanged();", body); // the switch, the chrome plan and the announcement follow
        Assert.True(body.IndexOf("if (_terminalRetirement) return;", StringComparison.Ordinal) > body.IndexOf("_freeTagMode = false;", StringComparison.Ordinal));
        // The Tag half compares with IsTagMode; after the panel went it is false again, so "Tag" starts the mode anew.
        Assert.Contains("internal bool IsTagMode => _tagging != null || _freeTagMode;", chrome);
        // EndFreeTagMode clears the flag before it closes the panel, so the release does not end the mode a second time.
        int explicitEnd = chrome.IndexOf("internal void EndFreeTagMode()", StringComparison.Ordinal);
        string explicitBody = chrome[explicitEnd..chrome.IndexOf("private void EndFreeTagModeWithPanel()", StringComparison.Ordinal)];
        Assert.True(explicitBody.IndexOf("_freeTagMode = false;", StringComparison.Ordinal) < explicitBody.IndexOf("CloseTagPanel();", StringComparison.Ordinal));
    }

    private static string Source(string relative) => File.ReadAllText(Root(relative));

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
