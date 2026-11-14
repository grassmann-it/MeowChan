using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using Ursa.Controls;

namespace MeowChan.DesktopTests;

/// <summary>Plan AP5 §1.1 (header, app menu) and §1.3 (Downloads) as WV1 delivers them: policy rows and the view-switch command.</summary>
public sealed class WorkViewShellDownloadsTests
{
    private static Presence[] Across(string id) => WorkViewSetting.All.Select(view => WorkViewPolicy.Resolve(id, view)).ToArray();

    // The header graph's own layout is replaced elsewhere (setting and window width only). These rows only register the
    // elements, so they stay the same in every view and nothing here can reintroduce a work-view dependency for the graph.
    [Theory]
    [InlineData("shell.header.tagline")]
    [InlineData("shell.header.graph")]
    [InlineData("shell.header.pause")]
    [InlineData("shell.header.fullscreen")]
    [InlineData("shell.header.theme")]
    [InlineData("shell.header.continue-reading")]
    [InlineData("shell.header.app-menu")]
    public void HeaderElementsAreTheSameInEveryView(string id)
    {
        Presence[] rows = Across(id);
        Assert.Single(rows.Distinct());
        Assert.True(WorkViewPolicy.OnSurface(rows[0]), id);
    }

    [Fact]
    public void TheAppMenuIsCompleteAndItsViewEntriesAreNeverReduced()
    {
        foreach (string id in new[] { "shell.appmenu.view-group", "shell.appmenu.view-cycle", "shell.appmenu.theme", "shell.appmenu.fullscreen" })
            Assert.All(Across(id), presence => Assert.Equal(Presence.Required, presence));
        Assert.True(WorkViewPolicy.Get("shell.appmenu.view-group").Safety);
        Assert.True(WorkViewPolicy.Get("shell.appmenu.view-cycle").Safety);
        // The footer's view selector stays too (R8): the way back is always visible.
        Assert.All(Across("shell.footer.view-switch"), presence => Assert.Equal(Presence.Required, presence));
    }

    // Plan §1.3 table: Search is under More only in Barebones, Details likewise, Columns… and Copy URLs until Advanced.
    [Theory]
    [InlineData("downloads.toolbar.search", Presence.Overflow, Presence.Direct, Presence.Direct, "input.focus-download-search")]
    [InlineData("downloads.toolbar.details", Presence.Overflow, Presence.Direct, Presence.Direct, "thread.details")]
    [InlineData("downloads.toolbar.columns", Presence.Overflow, Presence.Overflow, Presence.Direct, "table.choose-columns")]
    [InlineData("downloads.toolbar.copy-urls", Presence.Overflow, Presence.Overflow, Presence.Direct, "watch.copy-all-links")]
    public void DownloadsToolbarFollowsThePlanMatrix(string id, Presence barebones, Presence simple, Presence advanced, string command)
    {
        WorkViewEntry entry = WorkViewPolicy.Get(id);
        Assert.Equal((barebones, simple, advanced), (entry.Barebones, entry.Simple, entry.Advanced));
        Assert.Equal(command, entry.CommandId);
        // A shortcut command keeps its default or assignment whatever the placement (R2): the row only names the command.
        Assert.True(CommandCatalog.TryGet(command, out _));
        Assert.Equal(OverflowMode.Always, WorkViewPresenter.ToolBarMode(entry.Barebones));
    }

    [Fact]
    public void ToolbarItemsOnlyMoveNeverDisappear()
    {
        foreach (string id in new[] { "downloads.toolbar.search", "downloads.toolbar.details", "downloads.toolbar.columns", "downloads.toolbar.copy-urls" })
            Assert.All(WorkViewSetting.All, view => Assert.NotEqual(Presence.Hidden, WorkViewPolicy.Resolve(id, view)));
    }

    [Fact]
    public void TechnicalDownloadColumnsAreOfferedFromSimpleOnAndOffByDefault()
    {
        foreach (string column in new[] { "MissingMedia", "EarliestRetry" })
        {
            string id = WorkViewPolicy.DownloadColumnId("ThreadsDataGrid", column);
            Assert.False(WorkViewPolicy.ColumnOffered(id, WorkView.Barebones));
            Assert.True(WorkViewPolicy.ColumnOffered(id, WorkView.Simple));
            Assert.True(WorkViewPolicy.ColumnOffered(id, WorkView.Advanced));
            foreach (WorkView view in WorkViewSetting.All)
            {
                Assert.False(WorkViewPolicy.DefaultColumnVisible(id, view), column + " is on by default in " + view);
                // R10: an explicit choice of the user wins wherever the column is offered, never where it is not.
                Assert.Equal(view != WorkView.Barebones, WorkViewPolicy.ColumnVisible(id, view, saved: true));
                Assert.False(WorkViewPolicy.ColumnVisible(id, view, saved: false));
            }
        }
    }

    [Fact]
    public void IdentityAndStateColumnsStayRequiredInEveryView()
    {
        foreach (string column in new[] { "Title", "Completion" })
            Assert.All(Across(WorkViewPolicy.DownloadColumnId("ThreadsDataGrid", column)), presence => Assert.Equal(Presence.Required, presence));
        // The Boards table offers every column, on by default, in every view.
        Assert.All(WorkViewSetting.All, view =>
        {
            Assert.True(WorkViewPolicy.ColumnOffered(WorkViewPolicy.DownloadColumnId("BoardsDataGrid", "Status"), view));
            Assert.True(WorkViewPolicy.DefaultColumnVisible(WorkViewPolicy.DownloadColumnId("BoardsDataGrid", "Status"), view));
        });
    }

    // R4/R5: errors, the next step, the check/retry way out and the repair banner are in every view; the inspector is opened on purpose.
    [Fact]
    public void ErrorsRepairAndTheInspectorAreNeverReduced()
    {
        foreach (string id in new[] { "downloads.results.repair", "downloads.inspector.error", "downloads.inspector.check", "downloads.inspector.retry-media" })
        {
            Assert.True(WorkViewPolicy.Get(id).Safety, id);
            Assert.All(Across(id), presence => Assert.Equal(Presence.Required, presence));
        }
        Assert.All(Across("downloads.inspector.next-step"), presence => Assert.Equal(Presence.Required, presence));
        foreach (string id in new[] { "downloads.inspector.local-actions", "downloads.inspector.folder", "downloads.inspector.browser", "downloads.inspector.remove" })
            Assert.All(Across(id), presence => Assert.True(WorkViewPolicy.OnSurface(presence) && presence != Presence.Collapsed, id));
        // Only the source details are folded, as they always were; the capture report is the collapsed section of the intake card.
        Assert.All(Across("downloads.inspector.source-details"), presence => Assert.Equal(Presence.Collapsed, presence));
        Assert.All(Across("downloads.results.panel"), presence => Assert.Equal(Presence.Collapsed, presence));
    }

    [Fact]
    public void SectionsFoldOnlyWhenTheViewChanges()
    {
        Avalonia.Controls.Expander details = new() { IsExpanded = true };
        WorkViewElement.SetId(details, "downloads.inspector.source-details");
        WorkViewPresenter.ApplyExpander(details, WorkView.Simple, viewChanged: false);
        Assert.True(details.IsExpanded);
        WorkViewPresenter.ApplyExpander(details, WorkView.Simple, viewChanged: true);
        Assert.False(details.IsExpanded);
    }

    // ---- workspace.view-cycle (R8) ----

    [Fact]
    public void ViewCycleIsABindableCommandWithoutADefaultGestureAndWithAnAppMenuEntry()
    {
        CommandDefinition command = CommandCatalog.Get("workspace.view-cycle");
        Assert.Equal("workview.viewCycle", command.LabelKey);
        Assert.Equal(CommandScope.Application, command.Scope);
        Assert.Contains("app-menu", command.Contexts);
        Assert.True(command.IsImplemented);
        Assert.Null(command.Shortcut);
        foreach (ShortcutPlatform platform in Enum.GetValues<ShortcutPlatform>())
        {
            EffectiveShortcutBindings bindings = EffectiveShortcutBindings.Build(null, platform);
            Assert.Empty(bindings.For("workspace.view-cycle"));
            Assert.Empty(bindings.Issues);
        }
        Assert.Equal("workspace.view-cycle", WorkViewPolicy.Get("shell.appmenu.view-cycle").CommandId);
    }

    [Fact]
    public void ThemeFullscreenAndTheViewSwitchAreInTheAppMenu()
    {
        foreach (string id in new[] { "appearance.theme", "window.fullscreen", "workspace.view-cycle" })
            Assert.Contains("app-menu", CommandCatalog.Get(id).Contexts);
        Assert.Contains("workspace.view-cycle", MacMenuModel.CommandIds());
    }

    [Fact]
    public void ViewCycleLabelExistsInEnglishAndGerman()
    {
        Assert.Equal("Switch work view", BuiltInCatalog.English["workview.viewCycle"]);
        Assert.Equal("Arbeitsansicht wechseln", BuiltInCatalog.German["workview.viewCycle"]);
    }

    [Fact]
    public void ViewCycleVisitsMinimalSimpleAdvancedAndReturns()
    {
        Assert.Equal(WorkView.Simple, MeowChan.MainWindow.NextWorkView(WorkView.Barebones));
        Assert.Equal(WorkView.Advanced, MeowChan.MainWindow.NextWorkView(WorkView.Simple));
        Assert.Equal(WorkView.Barebones, MeowChan.MainWindow.NextWorkView(WorkView.Advanced));
    }
}
