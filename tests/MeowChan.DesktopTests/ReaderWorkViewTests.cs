using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

/// <summary>WV3 (plan AP5 §1.5): the reader rows of <see cref="WorkViewPolicy"/> and their registration in ArchivePage.axaml.</summary>
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

    // Every item of the three reader bars has a reader row and no XAML overflow mode of its own: the policy is the only source.
    [Fact]
    public void EveryReaderBarItemIsRegisteredAndCarriesNoOverflowModeOfItsOwn()
    {
        foreach (string name in ReaderBars)
        {
            XElement bar = Bar(name);
            Assert.Equal("True", (string?)bar.Attribute("PrioritizedOverflow"));
            XElement[] items = Items(bar);
            Assert.NotEmpty(items);
            foreach (XElement item in items)
            {
                string label = name + "/" + (string?)item.Attribute(X + "Name");
                string id = Id(item) ?? throw new Xunit.Sdk.XunitException(label + " has no wv:WorkViewElement.Id.");
                Assert.Equal(WorkViewPage.Reader, WorkViewPolicy.Get(id).Page);
                Assert.Null(item.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName == "ToolBar.OverflowMode"));
            }
        }
        // The toolbar carries the reader's eleven-plus labelled actions (P6: each with its icon).
        Assert.True(Items(Bar("ReaderToolbar")).Length >= 11);
    }

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
            ("reader.toolbar.find", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.toolbar.tags", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.toolbar.settings", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.toolbar.export", Presence.Overflow, Presence.Direct, Presence.Direct),
            // RL1: full screen left the bar in every view (the header button, F11 and More carry it).
            ("reader.toolbar.fullscreen", Presence.Overflow, Presence.Overflow, Presence.Overflow),
            ("reader.toolbar.thread", Presence.Overflow, Presence.Direct, Presence.Direct),
            ("reader.media.sort", Presence.Direct, Presence.Direct, Presence.Direct),
            ("reader.media.layout", Presence.Overflow, Presence.Direct, Presence.Direct),
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
    // full screen (under More in every view), the rarely used history return, Thread, the settings gear, export, tags, find, read, gallery.
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
        Assert.Equal(["reader.toolbar.gallery", "reader.toolbar.read", "reader.toolbar.find", "reader.toolbar.tags", "reader.toolbar.export"], order.Take(5));
        Assert.Equal(["reader.toolbar.settings", "reader.toolbar.thread", "reader.toolbar.history-back", "reader.toolbar.fullscreen"], order.Skip(5));
        foreach (string later in new[] { "reader.toolbar.fullscreen", "reader.toolbar.settings", "reader.toolbar.thread", "reader.toolbar.history-back" })
            Assert.True(Rank("reader.toolbar.export") < Rank(later), later);
        Assert.True(Rank("reader.toolbar.settings") < Rank("reader.toolbar.thread"));
        Assert.Equal("reader.toolbar.fullscreen", order[^1]);
        Assert.DoesNotContain("reader.toolbar.view", order);
        Assert.DoesNotContain("reader.toolbar.capture", order);
        // Required tool rows never take part in the width race.
        Assert.All(ReaderRows().Where(entry => entry.Id.StartsWith("reader.toolbar.", StringComparison.Ordinal) && !tools.Contains(entry)),
            entry => Assert.All(WorkViewSetting.All, view => Assert.Equal(OverflowMode.Never, WorkViewPresenter.ToolBarMode(entry.For(view)))));
    }

    // The width race in Advanced with the bar's visual order and the policy priorities: whenever the room ends before the tail,
    // export stays and settings, Thread and history go first (full screen is under More in every view, so it always moves). The pixel check at 1520 and 1280 is in the work-views
    // reader smoke; these widths are the English labels measured roughly (icon 16 + gap 5 + text + padding + 8 margin).
    // The way out (close, "Back to …") left the bar for the header row's end slot (Tom's rule 14.11.), so it no longer races.
    private static readonly (string Name, string Id, double Width)[] AdvancedBar =
    [
        ("ArchiveCurrentThreadFavorite", "reader.toolbar.thread-favorite", 46),
        ("ArchiveReadPosts", "reader.toolbar.read", 100), ("ArchiveOpenGallery", "reader.toolbar.gallery", 158),
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
    [InlineData(1280 - 48)]
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

    // With the way out in the header row, the wide Advanced reader (1520) keeps every tool on the bar except full screen, which is under More always.
    [Fact]
    public void TheWideAdvancedBarKeepsEveryToolButFullScreenOnceTheWayOutLeftIt()
    {
        Assert.Equal(["ArchiveReaderFullscreen"], MovedAt(1520 - 48));
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
