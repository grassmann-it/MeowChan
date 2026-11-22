using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Motion;
using MeowChan.Desktop;
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

    [Fact]
    public void ArchiveToolbarMediaHeaderWrapsBelowItsActionsAtNarrowWidths() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable platform = BooruReviewTestPlatform.Enter();
        Texts.Initialize();
        ReaderMediaSidebar sidebar = new();
        sidebar.ShowReaderHeader();
        sidebar.Describe("No. 22486939", "wide-example-file-1234567890.jpg", "500 KiB · 2001×3000",
            canOpen: true, postLinkTip: "Copy post reference", threadPosition: "3 of 37");
        sidebar.ConfigureFavorite(() => true, () => true, () => Task.CompletedTask);
        Window window = new() { Content = sidebar, Width = 820, Height = 480, Classes = { MotionPolicy.OffClass } };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Control header = sidebar.HeaderRow;
            Control number = header.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ReaderMediaSidebarPostLink");
            TextBlock details = header.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "ReaderMediaSidebarHeader");
            TextBlock title = header.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("section-title"));
            TextBlock counter = header.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Name == "ReaderMediaSidebarThreadPosition");
            Point At(Control item) => item.TranslatePoint(default, header)!.Value;

            Assert.Equal(Texts.T("reader.sidebar.compact"), title.Text);
            Assert.Equal(0, At(number).X);
            Assert.True(At(counter).X > At(details).X);
            Assert.True(At(sidebar.Open).X > At(sidebar.FavoriteButton).X);
            double singleLineHeight = details.Bounds.Height;

            BooruReviewTestPlatform.Resize(window, 272);
            Assert.Equal(0, At(number).X);
            Assert.True(At(details).Y > At(sidebar.Open).Y);
            Assert.True(details.Bounds.Height > singleLineHeight);
            Assert.True(At(sidebar.Open).X + sidebar.Open.Bounds.Width <= header.Bounds.Width);
            Assert.Contains("2001×3000", details.Text);
        }
        finally { window.Close(); }
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
            // Review 15.11.: full screen ends the bar and overflows only when it does not fit; Barebones keeps it under More.
            ("reader.toolbar.fullscreen", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.toolbar.thread", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.sort", Presence.Direct, Presence.Direct, Presence.Direct),
            ("reader.media.layout-tiles", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.layout-table", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.favorites-filter", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.rating-filter", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.columns", Presence.Overflow, Presence.Overflow, Presence.Direct),
            ("reader.media.version-scope", Presence.Overflow, Presence.Overflow, Presence.Direct),
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
    // full screen (the first to move when the room ends), the rarely used history return, Thread, the settings gear, export, tags, find, read, gallery.
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
        Assert.Equal(["reader.toolbar.export", "reader.toolbar.thread", "reader.toolbar.settings", "reader.toolbar.history-back", "reader.toolbar.fullscreen"], order.Skip(4));
        foreach (string later in new[] { "reader.toolbar.fullscreen", "reader.toolbar.thread", "reader.toolbar.settings", "reader.toolbar.history-back" })
            Assert.True(Rank("reader.toolbar.export") < Rank(later), later);
        Assert.True(Rank("reader.toolbar.thread") < Rank("reader.toolbar.settings"));
        Assert.Equal("reader.toolbar.fullscreen", order[^1]);
        Assert.DoesNotContain("reader.toolbar.view", order);
        Assert.DoesNotContain("reader.toolbar.capture", order);
        // Required tool rows never take part in the width race.
        Assert.All(ReaderRows().Where(entry => entry.Id.StartsWith("reader.toolbar.", StringComparison.Ordinal) && !tools.Contains(entry)),
            entry => Assert.All(WorkViewSetting.All, view => Assert.Equal(OverflowMode.Never, WorkViewPresenter.ToolBarMode(entry.For(view)))));
    }

    // The width race in Advanced with the bar's visual order and the policy priorities: whenever the room ends before the tail,
    // export stays and full screen, history, Thread and settings go first (full screen ends the order, so it moves first). The pixel check at 1520 and 1280 is in the work-views
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
        Assert.All(moved, name => Assert.Contains(name, new[] { "ArchiveReaderFullscreen", "ReaderSettingsOpen", "ReaderThreadOpen", "ArchiveHistoryBack" }));
        Assert.Contains("ArchiveReaderFullscreen", moved);
    }

    // With the way out in the header row, the wide Advanced reader (1520) keeps every tool on the bar, full screen included: with room to
    // spare nothing is forced under More (review 15.11.; RL1 had put full screen there in every view, a "More" with a single action).
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
        // The default-bound tools that Barebones moves under More (their shortcut runs via the WV0 reduced-control route).
        Assert.Equal(["reader.find", "thread.tags", "export.open-html-dialog", "window.fullscreen"],
            ReaderRows().Where(entry => entry.Barebones == Presence.Overflow && entry.CommandId != null
                && entry.Id.StartsWith("reader.toolbar.", StringComparison.Ordinal) && CommandCatalog.Get(entry.CommandId).DefaultGestures.Count > 0)
                .OrderBy(entry => entry.OverflowOrder).Select(entry => entry.CommandId!));
    }

    private static string Root(string path, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../..", path));
}
