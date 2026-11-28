using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

/// <summary>WV3 (plan AP5 §1.5): the reader rows of <see cref="WorkViewPolicy"/> and their registration in ArchivePage.axaml.</summary>
[Collection("Localization")]
public sealed class ReaderWorkViewTests
{
    private static readonly XNamespace A = "https://github.com/avaloniaui";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private const string Wv = "using:MeowChan.Desktop.WorkViews";
    private static readonly string[] ReaderBars = ["ReaderToolbar", "ReaderMediaTools", "ReaderMediaActions"];

    private static WorkViewEntry[] ReaderRows() => WorkViewPolicy.Entries.Where(entry => entry.Page == WorkViewPage.Reader).ToArray();

    private static XElement Page() => XDocument.Load(Root("Desktop/Views/Archive/ArchivePage.axaml")).Root!;

    private static XElement Bar(string name) => Page().Descendants().Single(element => (string?)element.Attribute(X + "Name") == name);

    private static string? Id(XElement element) => (string?)element.Attribute(XName.Get("WorkViewElement.Id", Wv));

    private static XElement[] Items(XElement bar) => bar.Elements().Where(element => !element.Name.LocalName.EndsWith(".Styles", StringComparison.Ordinal)).ToArray();

    [Fact]
    public void EveryReaderBarItemIsRegisteredAndTheViewGroupDocksToTheRight()
    {
        foreach (string name in ReaderBars)
        {
            XElement bar = Bar(name);
            Assert.Equal("True", (string?)bar.Attribute("PrioritizedOverflow"));
            XElement[] items = Items(bar);
            Assert.NotEmpty(items);
            foreach (XElement item in items.SelectMany(item => item.Name.LocalName == "SidebarToggleGroup" ? item.Elements() : [item]))
            {
                string label = name + "/" + (string?)item.Attribute(X + "Name");
                string id = Id(item) ?? throw new Xunit.Sdk.XunitException(label + " has no wv:WorkViewElement.Id.");
                Assert.Equal(WorkViewPage.Reader, WorkViewPolicy.Get(id).Page);
                Assert.DoesNotContain(item.Attributes(), attribute => attribute.Name.LocalName == "ToolBar.OverflowMode");
            }
        }
        XElement readerToolbar = Bar("ReaderToolbar");
        XNamespace toolbarNamespace = readerToolbar.Name.Namespace;
        XElement[] toolbar = Items(readerToolbar);
        XElement[] viewGroup = toolbar.Where(item => (bool?)item.Attribute(toolbarNamespace + "ActionToolBar.End") == true).ToArray();
        Assert.Equal(new[] { "reader.toolbar.unread-return", "reader.toolbar.history-back", "reader.toolbar.fullscreen", null }, viewGroup.Select(Id));
        Assert.Equal(new[] { "reader.toolbar.thread-list", "reader.toolbar.media-sidebar" }, viewGroup[^1].Elements().Select(Id));
        Assert.Equal(viewGroup, toolbar.TakeLast(viewGroup.Length));
        Assert.NotNull(Assert.Single(viewGroup.Select(item => (int?)item.Attribute(toolbarNamespace + "ActionToolBar.Group")).Distinct()));
    }

    [Fact]
    public void OnlyTheSidebarGroupSetsAnInitialOverflowMode()
    {
        // The containing group owns placement. It stays on the toolbar and gives up its labels before either switch.
        XElement group = Assert.Single(ReaderBars.SelectMany(name => Items(Bar(name)))
            .Where(item => item.Attributes().Any(attribute => attribute.Name.LocalName == "ToolBar.OverflowMode")));
        Assert.Equal("SidebarToggleGroup", group.Name.LocalName);
        Assert.Equal("Never", group.Attributes().Single(attribute => attribute.Name.LocalName == "ToolBar.OverflowMode").Value);
        Assert.All(group.Elements(), segment => Assert.All(WorkViewSetting.All,
            view => Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(Id(segment)!, view))));
    }

    [Theory]
    [InlineData(820, true)]
    [InlineData(280, false)]
    public void ArchiveSidebarKeepsItsFactsAndActionsBelowAResponsiveHeader(int width, bool singleRow) => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        const string dimensions = "2001×3000";
        string fileName = "wide-example-" + new string('W', 100) + ".jpg";
        ReaderMediaSidebar sidebar = new();
        sidebar.Describe("No. 22486939", fileName, dimensions, canOpen: true, threadPosition: new(3, 37, false), savedBytes: 500 * 1024);
        sidebar.ConfigureFavorite(() => true, () => true, () => Task.CompletedTask);
        Window window = new() { Content = sidebar, Width = width, Height = 480, Classes = { MotionPolicy.OffClass } };
        try
        {
            window.Show();
            window.PlatformImpl!.Activated?.Invoke();
            Settle();
            Control header = Find<Control>("ReaderMediaSidebarHeaderRow");
            TextBlock title = Find<TextBlock>("ReaderMediaSidebarHeading");
            TextBlock file = Find<TextBlock>("ReaderMediaSidebarMeta");
            TextBlock position = Find<TextBlock>("ReaderMediaSidebarPosition");
            Button open = Find<Button>("ReaderMediaSidebarOpen");
            ToggleButton info = Find<ToggleButton>("ReaderMediaSidebarInfoToggle");
            Control facts = Find<Control>("ReaderMediaSidebarFacts");
            Control actions = Find<Control>("ReaderMediaSidebarInfoBar");
            Control card = Find<Control>("ReaderMediaSidebarCard");

            Assert.Equal(Texts.T("reader.sidebar.title"), AutomationProperties.GetName(sidebar));
            Assert.Contains(open, header.GetVisualDescendants());
            Assert.Contains(title, header.GetVisualDescendants());
            SidePanelDesignTests.AssertCompactHeader(sidebar);
            // W112-72, Fable variant A §§2.1–2.2: “Einzeilig, sobald es mit Icon-Knöpfen passt”; dimensions belong to the facts.
            Rect filename = BoundsIn(file, header), index = BoundsIn(position, header), gallery = BoundsIn(open, header);
            Assert.Equal(singleRow ? 32 : ReaderMediaSidebar.HeaderLineHeight, header.Bounds.Height);
            Assert.Equal(singleRow ? 8 : 32, filename.Top);
            if (singleRow)
            {
                Assert.Equal(gallery.Center.Y, filename.Center.Y, 0.5);
                Assert.True(BoundsIn(title, header).Right <= index.Left);
            }
            else Assert.True(gallery.Bottom <= filename.Top);
            Assert.Equal(filename.Top, index.Top, 0.5);
            Assert.True(index.Right <= filename.Left);
            Assert.True(gallery.Right <= header.Bounds.Width);
            Assert.Contains("…", file.Text);
            Assert.EndsWith(".jpg · 500 KiB", file.Text);
            Assert.Contains(fileName, ToolTip.GetTip(file)?.ToString());

            // Variant A §3 docks the actions and facts below the card and lets Info collapse just the facts.
            Assert.True(BoundsIn(card, sidebar).Bottom <= BoundsIn(actions, sidebar).Top);
            Assert.True(BoundsIn(actions, sidebar).Bottom <= BoundsIn(facts, sidebar).Top);
            Assert.Contains(facts.GetVisualDescendants().OfType<SelectableTextBlock>(), value => value.IsEffectivelyVisible && value.Text == dimensions);
            int galleryClicks = 0;
            open.Click += (_, _) => galleryClicks++;
            Click(open);
            Assert.Equal(1, galleryClicks);
            Click(info);
            Assert.False(facts.IsEffectivelyVisible);
            Assert.True(actions.IsEffectivelyVisible);
            Click(info);
            Assert.True(facts.IsEffectivelyVisible);
        }
        finally { window.Close(); }

        T Find<T>(string id) where T : Control => SidePanelDesignTests.ById<T>(sidebar, id);
        static Rect BoundsIn(Control control, Visual root) => new(control.TranslatePoint(default, root)!.Value, control.Bounds.Size);
        void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
        void Click(Button button)
        {
            Point point = BoundsIn(button, window).Center;
            window.MouseMove(point);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Settle();
        }
    });

    // Every reader row backed by a control is registered exactly once in the page (orphan check of the work-views smoke, statically).
    [Fact]
    public void EveryReaderRowHasItsControlInTheReaderPage()
    {
        string[] ids = Page().Descendants().Select(Id).OfType<string>().Where(id => id.StartsWith("reader.", StringComparison.Ordinal)).ToArray();
        Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ReaderRows().Select(entry => entry.Id).Order(StringComparer.Ordinal), ids.Order(StringComparer.Ordinal));
    }

    // Owner defaults: way back, the thread star, the reading-mark return, state returns, status line, errors and tabs are never reduced.
    [Fact]
    public void WayBackStateErrorsAndDataAreNeverReduced()
    {
        string[] required =
        [
            "reader.header.back", "reader.toolbar.close", "reader.toolbar.thread-favorite", "reader.toolbar.unread-return",
            "reader.toolbar.gallery-return", "reader.toolbar.media-return", "reader.toolbar.skip-unavailable",
            "reader.status.capture", "reader.status.follow-live", "reader.status.new-posts", "reader.status.show-new-posts",
            "reader.status.group-availability", "reader.media.rating-error"
        ];
        foreach (string id in required)
            Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view)));
        foreach (string id in new[] { "reader.status.capture", "reader.status.follow-live", "reader.status.new-posts", "reader.media.rating-error" })
            Assert.True(WorkViewPolicy.Get(id).Safety, id);
        foreach (string tab in new[] { "reader.tabs.posts", "reader.tabs.media", "reader.tabs.references" })
            Assert.All(WorkViewSetting.All, view => Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve(tab, view)), tab));
        // Reducing means moving (R1): no reader row leaves its surface, everything not on the bar sits under its "More".
        Assert.DoesNotContain(ReaderRows(), entry => WorkViewSetting.All.Any(view => entry.For(view) == Presence.Hidden));
    }

    [Fact]
    public void ViewsFollowThePlanMatrix()
    {
        (string Id, Presence Barebones, Presence Simple, Presence Advanced)[] matrix =
        [
            ("reader.toolbar.gallery", Presence.Direct, Presence.Direct, Presence.Direct),
            ("reader.toolbar.read", Presence.Direct, Presence.Direct, Presence.Direct),
            ("reader.toolbar.media-sidebar", Presence.Required, Presence.Required, Presence.Required),
            ("reader.toolbar.thread-list", Presence.Required, Presence.Required, Presence.Required),
            ("reader.toolbar.find", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.toolbar.tags", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.toolbar.settings", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.toolbar.export", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.toolbar.fullscreen", Presence.Required, Presence.Required, Presence.Required),
            ("reader.toolbar.thread", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.sort", Presence.Direct, Presence.Direct, Presence.Direct),
            ("reader.media.layout-tiles", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.layout-table", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.favorites-filter", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.rating-filter", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.columns", Presence.Overflow, Presence.Overflow, Presence.Direct),
            ("reader.media.version-scope", Presence.Overflow, Presence.Overflow, Presence.Direct),
            ("reader.media.thread-media-menu", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.edit-rating", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.preview", Presence.Overflow, Presence.Direct, Presence.Direct),
        ];
        foreach (var row in matrix)
        {
            WorkViewEntry entry = WorkViewPolicy.Get(row.Id);
            Assert.Equal((row.Barebones, row.Simple, row.Advanced), (entry.Barebones, entry.Simple, entry.Advanced));
        }
    }

    // R6: an active favourites or rating filter, or the latest-versions-only scope, stays on the bar in Barebones.
    [Fact]
    public void ActiveMediaFiltersStayOnTheBar()
    {
        // Policy only: a registered control here would join the window-wide id registry from a test thread.
        foreach (string id in new[] { "reader.media.favorites-filter", "reader.media.rating-filter", "reader.media.version-scope" })
        {
            Assert.Equal(OverflowMode.Always, WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve(id, WorkView.Barebones)));
            Assert.Equal(OverflowMode.AsNeeded, WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve(id, WorkView.Barebones, nonDefaultState: true)));
        }
    }

    // The single declarative keep-visible order (owner 14.11., RL1 15.11.): OverflowOrder follows it; a narrow window moves the tail first:
    // the rarely used history return, Thread, the settings gear, export, tags, find, read, gallery.
    // Required items are not in it (never move). The post-capture switch left the bar for the Thread menu (R2), so no "capture" tool exists.
    [Fact]
    public void ToolbarKeepVisibleOrderIsTheOverflowOrderAndEndsWithTheRarelyUsedActions()
    {
        IReadOnlyList<string> order = WorkViewPolicy.ReaderToolbarKeepVisibleOrder;
        Assert.Equal(order.Count, order.Distinct().Count());
        WorkViewEntry[] tools = ReaderRows().Where(entry => entry.Id.StartsWith("reader.toolbar.", StringComparison.Ordinal)
            && entry.Barebones != Presence.Required).ToArray();
        Assert.Equal(order.Order(StringComparer.Ordinal), tools.Select(entry => entry.Id).Order(StringComparer.Ordinal));
        Assert.Equal(order, tools.OrderBy(entry => entry.OverflowOrder).Select(entry => entry.Id));
        Assert.All(tools, entry => Assert.True(entry.OverflowOrder > 0, entry.Id));
        int Rank(string id) => order.ToList().IndexOf(id);
        Assert.Equal(["reader.toolbar.gallery", "reader.toolbar.read", "reader.toolbar.find", "reader.toolbar.tags"], order.Take(4));
        Assert.Equal(["reader.toolbar.export", "reader.toolbar.thread", "reader.toolbar.settings", "reader.toolbar.history-back"], order.Skip(4));
        foreach (string later in new[] { "reader.toolbar.thread", "reader.toolbar.settings", "reader.toolbar.history-back" })
            Assert.True(Rank("reader.toolbar.export") < Rank(later), later);
        Assert.True(Rank("reader.toolbar.thread") < Rank("reader.toolbar.settings"));
        Assert.DoesNotContain("reader.toolbar.fullscreen", order);
        Assert.DoesNotContain("reader.toolbar.view", order);
        Assert.DoesNotContain("reader.toolbar.capture", order);
        // Required tool rows never take part in the width race.
        Assert.All(ReaderRows().Where(entry => entry.Id.StartsWith("reader.toolbar.", StringComparison.Ordinal) && !tools.Contains(entry)),
            entry => Assert.All(WorkViewSetting.All, view => Assert.Equal(OverflowMode.Never, WorkViewPresenter.ToolBarMode(entry.For(view)))));
    }

    // The width race in Advanced with the bar's visual order and the policy priorities: whenever the room ends before the tail,
    // export stays and history, settings and Thread go first. The pixel check at 1520 and 1280 is in the work-views
    // reader smoke; these widths are the English labels measured roughly (icon 16 + gap 5 + text + padding + 8 margin).
    // The way out (close, "Back to …") left the bar for the header row's end slot (Tom's rule 14.11.), so it no longer races.
    private static readonly (string Name, string Id, double Width)[] AdvancedBar =
    [
        ("ArchiveCurrentThreadFavorite", "reader.toolbar.thread-favorite", 46),
        ("ArchiveReadPosts", "reader.toolbar.read", 150), ("ArchiveOpenGallery", "reader.toolbar.gallery", 158),
        ("ReaderFindOpen", "reader.toolbar.find", 100),
        ("ReaderTagsOpen", "reader.toolbar.tags", 92), ("ExportArchiveButton", "reader.toolbar.export", 172),
        ("ReaderThreadOpen", "reader.toolbar.thread", 104), ("ReaderSettingsOpen", "reader.toolbar.settings", 130),
        ("ArchiveHistoryBack", "reader.toolbar.history-back", 170), ("ArchiveReaderFullscreen", "reader.toolbar.fullscreen", 128)
    ];

    private static string[] MovedAt(double available)
    {
        var bar = AdvancedBar;
        bool[] overflow = ActionOverflowLayout.Resolve(bar.Select(item => item.Width).ToArray(),
            bar.Select(item => WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve(item.Id, WorkView.Advanced))).ToArray(),
            bar.Select(_ => true).ToArray(), available, 40, bar.Select(item => WorkViewPolicy.Get(item.Id).OverflowOrder).ToArray());
        return bar.Where((_, index) => overflow[index]).Select(item => item.Name).ToArray();
    }

    [Theory]
    [InlineData(1100)]
    [InlineData(1040)]
    [InlineData(900)]
    public void ExportStaysOnTheAdvancedBarWhileTheRarelyUsedActionsOverflow(double available)
    {
        string[] moved = MovedAt(available);
        Assert.DoesNotContain("ExportArchiveButton", moved);
        Assert.NotEmpty(moved); // the room really ends before the tail
        Assert.All(moved, name => Assert.Contains(name, new[] { "ReaderSettingsOpen", "ReaderThreadOpen", "ArchiveHistoryBack" }));
        Assert.Contains("ArchiveHistoryBack", moved);
    }

    // With the way out in the header row, the wide Advanced reader (1520) keeps every tool on the bar.
    [Fact]
    public void TheWideAdvancedBarKeepsEveryToolOnceTheWayOutLeftIt()
    {
        Assert.Empty(MovedAt(1520 - 48));
        Assert.DoesNotContain(AdvancedBar, item => item.Name is "ArchiveCloseReader" or "ArchiveGalleryReturn" or "ArchiveMediaReturn" or "ArchiveBackButton");
    }

    [Fact]
    public void BarebonesPutsEveryReducedReaderToolUnderMoreAndKeepsItsShortcutCommand()
    {
        foreach (WorkViewEntry entry in ReaderRows().Where(entry => entry.Barebones == Presence.Overflow))
        {
            Assert.Equal(OverflowMode.Always, WorkViewPresenter.ToolBarMode(entry.Barebones));
            if (entry.CommandId != null) Assert.Equal(entry.CommandId, CommandCatalog.Get(entry.CommandId).Id);
        }
        // TOGGLE-2 review: “das Menü vom Reader-Arbeitsziel aus per effektivem Kürzel erreichbar machen, auch bei geschlossenem Overflow.”
        // Barebones also moves the Thread menu itself under More.
        Assert.Equal(["reader.find", "thread.tags", "export.open-html-dialog", "reader.thread-menu"],
            ReaderRows().Where(entry => entry.Barebones == Presence.Overflow && entry.CommandId != null
                && entry.Id.StartsWith("reader.toolbar.", StringComparison.Ordinal) && CommandCatalog.Get(entry.CommandId).DefaultGestures.Count > 0)
                .OrderBy(entry => entry.OverflowOrder).Select(entry => entry.CommandId!));
    }

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
