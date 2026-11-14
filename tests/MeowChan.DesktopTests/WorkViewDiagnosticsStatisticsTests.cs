using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>Plan AP5 §1.11/§1.12 (WV7): what the work view changes on the Diagnostics and Statistics pages, and nothing more.</summary>
public sealed class WorkViewDiagnosticsStatisticsTests
{
    private static readonly WorkViewPage[] Pages = [WorkViewPage.Diagnostics, WorkViewPage.Statistics];

    private static WorkViewEntry[] Rows(WorkViewPage page) => WorkViewPolicy.Entries.Where(entry => entry.Page == page).ToArray();

    [Fact]
    public void BothPagesHaveRowsAndNoneIsHidden()
    {
        foreach (WorkViewPage page in Pages)
        {
            Assert.NotEmpty(Rows(page));
            // R1/R4: these pages move or collapse; no row disappears from a view.
            Assert.All(Rows(page), entry => Assert.All(WorkViewSetting.All, view => Assert.NotEqual(Presence.Hidden, entry.For(view))));
        }
    }

    // R4/R5: rows, details, summary, errors and warnings are the same in every view.
    [Theory]
    [InlineData("diagnostics.summary")]
    [InlineData("diagnostics.table")]
    [InlineData("diagnostics.details")]
    [InlineData("diagnostics.filter-summary")]
    [InlineData("diagnostics.error")]
    [InlineData("diagnostics.toolbar.check-now")]
    [InlineData("diagnostics.toolbar.retry-selection")]
    [InlineData("diagnostics.toolbar.show-in-downloads")]
    [InlineData("statistics.now")]
    [InlineData("statistics.history")]
    [InlineData("statistics.sources")]
    [InlineData("statistics.reset-confirm")]
    [InlineData("statistics.load-notice")]
    [InlineData("statistics.persist-error")]
    public void DataStateAndTheErrorPathAreRequiredInEveryView(string id) =>
        Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, WorkViewPolicy.Resolve(id, view)));

    [Fact]
    public void WarningsAndErrorsAreSafetyRows() =>
        Assert.All(new[] { "diagnostics.error", "statistics.reset-confirm", "statistics.load-notice", "statistics.persist-error" },
            id => Assert.True(WorkViewPolicy.Get(id).Safety, id));

    [Fact]
    public void RarelyUsedActionsMoveUnderMoreAsThePlanSays()
    {
        Assert.Equal([Presence.Overflow, Presence.Overflow, Presence.Direct], PresenceOf("diagnostics.toolbar.retry-filtered"));
        Assert.Equal([Presence.Overflow, Presence.Direct, Presence.Direct], PresenceOf("diagnostics.toolbar.copy"));
        Assert.Equal([Presence.Overflow, Presence.Overflow, Presence.Direct], PresenceOf("statistics.reset"));
        Assert.Equal([Presence.Collapsed, Presence.Direct, Presence.Direct], PresenceOf("diagnostics.filters"));
        Assert.Equal([Presence.Collapsed, Presence.Collapsed, Presence.Collapsed], PresenceOf("diagnostics.session-messages"));
    }

    [Fact]
    public void MovedActionsKeepTheirCommandSoTheShortcutKeepsWorking()
    {
        Assert.Equal("diagnostics.retry-filtered", WorkViewPolicy.Get("diagnostics.toolbar.retry-filtered").CommandId);
        Assert.Equal("diagnostics.copy", WorkViewPolicy.Get("diagnostics.toolbar.copy").CommandId);
        Assert.Equal("watch.check", WorkViewPolicy.Get("diagnostics.toolbar.check-now").CommandId);
    }

    // R6: an active filter is shown wherever the view would close the group; a collapsed group becomes direct.
    [Fact]
    public void AnActiveFilterRaisesTheClosedGroupAndNothingElseMoves()
    {
        Assert.Equal(Presence.Collapsed, WorkViewPolicy.Resolve("diagnostics.filters", WorkView.Barebones));
        Assert.Equal(Presence.Direct, WorkViewPolicy.Resolve("diagnostics.filters", WorkView.Barebones, nonDefaultState: true));
        Assert.All(WorkViewSetting.All, view => Assert.True(WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("diagnostics.filters", view))));
    }

    // R7: the More button of the Diagnostics page lists exactly the rows the view puts under More.
    [Fact]
    public void MoreListsExactlyTheOverflowRowsOfTheView()
    {
        string[] Overflowing(WorkView view) => Rows(WorkViewPage.Diagnostics)
            .Where(entry => entry.Kind == WorkViewKind.Action && WorkViewPolicy.Resolve(entry.Id, view) == Presence.Overflow)
            .Select(entry => entry.Id).Order().ToArray();
        Assert.Equal(["diagnostics.toolbar.copy", "diagnostics.toolbar.retry-filtered"], Overflowing(WorkView.Barebones));
        Assert.Equal(["diagnostics.toolbar.retry-filtered"], Overflowing(WorkView.Simple));
        Assert.Empty(Overflowing(WorkView.Advanced));
        // Every overflow row has a command the forwarding menu entry can show (Populate needs it).
        Assert.All(Rows(WorkViewPage.Diagnostics).Where(entry => entry.Kind == WorkViewKind.Action && entry.Barebones == Presence.Overflow),
            entry => Assert.NotNull(entry.CommandId));
    }

    // No orphaned rows (pure form of the smoke's registration check): every control-backed row is in the page's XAML,
    // every id in the XAML is a row of this page.
    [Theory]
    [InlineData(WorkViewPage.Diagnostics, "Desktop/Views/Diagnostics/DiagnosticsPage.axaml")]
    [InlineData(WorkViewPage.Statistics, "Desktop/Views/Statistics/StatisticsPage.axaml")]
    [InlineData(WorkViewPage.Status, "Desktop/Views/Status/StatusPage.axaml")]
    public void EveryRowIsRegisteredByTheXamlOfItsPageAndNothingElse(WorkViewPage page, string xaml)
    {
        string[] used = Regex.Matches(File.ReadAllText(Path.Combine(Root(), xaml)), @"WorkViewElement\.Id=""([^""]+)""")
            .Select(match => match.Groups[1].Value).ToArray();
        string[] rows = Rows(page).Where(entry => entry.Kind is WorkViewKind.Action or WorkViewKind.Element or WorkViewKind.Section)
            .Select(entry => entry.Id).Order().ToArray();
        Assert.Equal(rows, used.Order().ToArray());
        Assert.Equal(used.Length, used.Distinct().Count());
    }

    // The Status container (SM2) owns only the status line and the segment bar; both stay in every view, the bar is the way
    // between the segments (safety). The segments keep their own pages' rows; the footer has one Status destination.
    [Fact]
    public void TheStatusContainerOwnsOnlyItsLineAndTheSegmentBar()
    {
        Assert.Equal(["status.segments", "status.strip"], Rows(WorkViewPage.Status).Select(entry => entry.Id).Order().ToArray());
        Assert.All(Rows(WorkViewPage.Status), entry => Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, entry.For(view))));
        Assert.True(WorkViewPolicy.Get("status.segments").Safety);
        Assert.Equal([Presence.Overflow, Presence.Overflow, Presence.Direct], PresenceOf("shell.footer.status"));
        Assert.Null(WorkViewPolicy.Find("shell.footer.diagnostics"));
        Assert.Null(WorkViewPolicy.Find("shell.footer.statistics"));
        Assert.Equal("shell.footer.status", WorkViewPolicy.Get("shell.header.disk-graph").Alternative);
    }

    private static Presence[] PresenceOf(string id) => WorkViewSetting.All.Select(view => WorkViewPolicy.Get(id).For(view)).ToArray();

    private static string Root([CallerFilePath] string source = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "../.."));
}
