using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

// GL6 (UX spec S1/S2 of PLAN-GALERIE-LAYOUT-BLOECKE): "the toolbar is one row at 760 dip", proved without a window. The overflow stages of
// GalleryChromePlan say which controls are on the bar; this model adds what each of them costs in dip (the widths the viewer's styles give:
// an icon-only button is a 32 dip square, a labelled one its text plus padding, groups stand 12 dip apart) and the test compares the sum with
// the room of the bar. A width where a combination of view, language and slideshow state needs more than the bar has would wrap; the toolbar
// is a Grid of three auto-sized sides, so the plan's overflow order is the only thing that keeps it on one row. The real layout is
// measured again by the gallery-toolbar smoke (RequireToolbar: the toolbar is at most 64 dip high at 760x500 and 1440x920, en and de).
[Collection("Localization")]
public sealed class GalleryToolbarFitTests : IDisposable
{
    public GalleryToolbarFitTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    // ---- the cost of each control (dip) -----------------------------------------------------------------------------------

    private const double Square = 32;            // Workspace.Size.Input: every icon-only button
    private const double GroupGap = 12;          // the sides' Spacing and the toolbar's ColumnSpacing
    private const double InGroup = 8;            // workspace-tool-group Spacing (the mode switch uses 4)
    private const double ButtonPadding = 24;     // a labelled button: 12 either side
    private const double CharWidth = 7.5;        // an average character of the 13 px UI font, rounded up so the check errs on the narrow side
    // The room of the bar: the viewer's width minus the dock's margin (8 + 8) and padding (8 + 8): GalleryViewer._chrome.
    private static double Room(double viewerWidth) => viewerWidth - 32;

    private static double Text(string text) => Math.Ceiling(text.Length * CharWidth);

    // The widest label the control can show: a toggle changes its text with its state.
    private static double Labelled(params string[] states) => states.Max(Text);

    private static double ModeSwitch(GalleryChromePlan plan) => plan.ModeIcons
        ? 2 * Square + 4
        : Labelled(Texts.T("gallery.mode.view")) + ButtonPadding + 4 + Labelled(Texts.T("gallery.mode.tag")) + ButtonPadding;

    // The arrows with the position between them: the position text is at least 44 dip (MinWidth) and as wide as its digits ("9999 / 9999" at most).
    private static double Navigation() => Square + Math.Max(44, Text("9999 / 9999")) + Square + 2 * InGroup;

    private static double Group(params double[] controls) => controls.Sum() + InGroup * (controls.Length - 1);

    private static double SlideshowToggle(GalleryChromePlan plan, string start, string pause)
    {
        // Compact (below 1100): the icon and the split button's arrow. Wider: icon, 4, the longer of the two labels, and the arrow.
        double toggle = plan.ModeIcons ? Square : ButtonPadding + 14 + 4 + Labelled(start, pause);
        return toggle + Square;
    }


    private static double LayoutButton(GalleryChromePlan plan) => ButtonPadding + 16 + 8 + Text(Texts.T("gallery.layout.button")) + 8 + 16; // icon, label, chevron

    /// <summary>The width the plan's bar needs in dip: the groups of the three sides, 12 dip apart.</summary>
    private static double Demand(GalleryChromePlan plan)
    {
        string start = Texts.T("viewer.playback.start"), pause = Texts.T("viewer.playback.pause");
        List<double> groups = new();
        if (plan.ModeSwitch) groups.Add(ModeSwitch(plan));
        if (plan.Nav) groups.Add(Navigation());
        if (plan.Zoom) groups.Add(plan.ActualSize ? Group(Square, Square) : Square);
        if (plan.PanelToggle || plan.Favorite)
            groups.Add(Group(Enumerable.Repeat(Square, (plan.PanelToggle ? 1 : 0) + (plan.Favorite ? 1 : 0)).ToArray()));
        List<double> tools = new();
        if (plan.Tags) tools.Add(Square);
        if (plan.Slideshow) tools.Add(SlideshowToggle(plan, start, pause));
        if (plan.Help) tools.Add(Square);
        if (tools.Count > 0) groups.Add(Group(tools.ToArray()));
        List<double> view = new();
        if (plan.More) view.Add(Square);
        if (plan.LayoutOnBar) view.Add(LayoutButton(plan));
        if (view.Count > 0) groups.Add(Group(view.ToArray()));
        if (plan.ToolsToggle) groups.Add(Square);
        return groups.Sum() + GroupGap * (groups.Count - 1);
    }

    // ---- the widths worth checking: both sides of every stage edge, the window's minimum and wide ones -----------------------

    private static readonly double[] Widths = [760, 800, 899, 900, 1099, 1100, 1279, 1280, 1440, 1920];

    private static GalleryChromePlan Plan(WorkView view, double width, bool slideshowActive, bool video = false) =>
        GalleryChromePlan.Resolve(new GalleryChromeInput(GalleryChromeMode.View, view, GalleryLayoutChoice.Default, MediaFocus: false,
            Overlay: false, ControlsHidden: false, video, PanelOpen: false, PanelCoupled: false, width, Height: 1000)
        {
            SlideshowActive = slideshowActive, SlideshowRunning = slideshowActive, TagAvailable = true
        });

    public static IEnumerable<object[]> Cases()
    {
        foreach (WorkView view in new[] { WorkView.Advanced, WorkView.Simple, WorkView.Barebones })
            foreach (string language in new[] { "de", "en" })
                foreach (double width in Widths)
                    foreach (bool slideshow in new[] { false, true })
                        yield return [view, language, width, slideshow];
    }

    [Theory, MemberData(nameof(Cases))]
    public void TheBarOfEveryViewAndLanguageFitsOnOneRowAtEveryWidthFromTheWindowMinimum(WorkView view, string language, double width, bool slideshow)
    {
        Texts.SetLanguage(language);
        GalleryChromePlan plan = Plan(view, width, slideshow);
        double need = Demand(plan), room = Room(width);
        Assert.True(need <= room,
            $"{view} {language} at {width} dip (stage {GalleryChromePlan.OverflowStage(width)}) needs {need} dip of {room}: the bar would wrap. On the bar: "
            + string.Join(", ", plan.BarToolIds) + (plan.LayoutOnBar ? ", layout" : "") + (plan.ModeIcons ? ", mode icons" : ", mode words"));
    }

    [Fact]
    public void TheWindowMinimumIsTheNarrowestBarAndItIsStageThree()
    {
        // MainWindow.axaml: MinWidth 760. The gallery is as wide as the window, so 760 is the narrowest bar there is (stage 4 is only
        // reached by a viewer hosted in something narrower than the window).
        string window = File.ReadAllText(Root("Desktop/Shell/MainWindow.axaml"));
        Assert.Contains("MinWidth=\"760\"", window);
        Assert.Equal(3, GalleryChromePlan.OverflowStage(760));
    }

    [Theory]
    [InlineData("de")]
    [InlineData("en")]
    public void AtSevenHundredSixtyTheBarKeepsOnlyTheCoreAndLeavesMostOfItsRoomFree(string language)
    {
        Texts.SetLanguage(language);
        foreach (WorkView view in new[] { WorkView.Advanced, WorkView.Simple, WorkView.Barebones })
        {
            GalleryChromePlan plan = Plan(view, 760, slideshowActive: true);
            // Everything is icon-only below 1100; the movable tools and the layout button are under "More"; an active slideshow keeps its toggle.
            Assert.True(plan.ModeIcons);
            Assert.False(plan.LayoutOnBar);
            Assert.False(plan.Tags || plan.Help);
            Assert.True(plan.Slideshow); // R6: an active slideshow's pause stays one click away
            double need = Demand(plan), room = Room(760);
            Assert.True(need <= room * 0.85, $"{view} {language}: {need} of {room} dip leaves too little spare room for a longer position text or a wider font.");
        }
    }

    [Fact]
    public void TheStagesOnlyEverTakeControlsAwayAsTheBarGetsNarrower()
    {
        // The overflow order must be monotone: a control that left at some width never comes back below it, so the bar never grows
        // while the room shrinks (the check that catches a wrong LeaveStage entry).
        foreach (WorkView view in new[] { WorkView.Advanced, WorkView.Simple, WorkView.Barebones })
        {
            HashSet<string> gone = new();
            foreach (double width in Widths.OrderDescending())
            {
                GalleryChromePlan plan = Plan(view, width, slideshowActive: false);
                foreach (string id in gone) Assert.DoesNotContain(id, plan.BarToolIds);
                foreach (string id in WorkViewPolicy.GalleryMovableToolIds)
                    if (!plan.BarToolIds.Contains(id)) gone.Add(id);
            }
        }
    }

    private static string Root(string path, [System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
