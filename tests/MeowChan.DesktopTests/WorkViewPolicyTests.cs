using MeowChan.Desktop.Commands;
using MeowChan.Desktop.WorkViews;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>Plan AP5 §3.3: the rules every row of <see cref="WorkViewPolicy"/> must keep (pure, no window).</summary>
public sealed class WorkViewPolicyTests
{
    // Pages whose partial is still empty after the foundation; each page package (WV3–WV9) removes its page here.
    private static readonly WorkViewPage[] PagesWithoutRowsYet =
    [
        WorkViewPage.Reader, WorkViewPage.Gallery, WorkViewPage.Media, WorkViewPage.Rules, WorkViewPage.Sources,
        WorkViewPage.Plugins, WorkViewPage.Diagnostics, WorkViewPage.Statistics, WorkViewPage.Settings, WorkViewPage.Dialogs
    ];

    [Fact]
    public void IdsAreUniqueAndShapedByPage()
    {
        Assert.Equal(WorkViewPolicy.Entries.Count, WorkViewPolicy.Entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(WorkViewPolicy.Entries, entry =>
        {
            Assert.Matches(@"^[a-z]+(\.[A-Za-z0-9*-]+)+$", entry.Id);
            Assert.StartsWith(entry.Page.ToString().ToLowerInvariant() + ".", entry.Id, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CommandIdsExistInTheCatalog() =>
        Assert.All(WorkViewPolicy.Entries.Where(entry => entry.CommandId != null),
            entry => Assert.Equal(entry.CommandId, CommandCatalog.Get(entry.CommandId!).Id));

    [Fact]
    public void SafetyRowsAreRequiredInEveryView() =>
        Assert.All(WorkViewPolicy.Entries.Where(entry => entry.Safety), entry =>
            Assert.All(WorkViewSetting.All, view => Assert.Equal(Presence.Required, entry.For(view))));

    // R1: reducing means moving. A view without an own control names the way it reaches the function in that view.
    // Columns are exempt: a column not offered in a view hides no row (R4); the inspector and Advanced show the value.
    [Fact]
    public void EveryHiddenRowNamesAnAlternativeReachableInThatView()
    {
        foreach (WorkViewEntry entry in WorkViewPolicy.Entries.Where(entry => entry.Kind != WorkViewKind.Column))
            foreach (WorkView view in WorkViewSetting.All.Where(view => entry.For(view) == Presence.Hidden))
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Alternative), entry.Id + " is hidden in " + view + " without an alternative.");
                if (WorkViewPolicy.Find(entry.Alternative!) is { } alternative)
                    Assert.True(WorkViewPolicy.Rank(alternative.For(view)) >= WorkViewPolicy.Rank(Presence.Overflow),
                        entry.Id + ": its alternative " + alternative.Id + " is not reachable in " + view + ".");
                else
                    CommandCatalog.Get(entry.Alternative!); // a command id: a context menu, the app menu or a shortcut carries it
            }
    }

    // R12: Barebones ⊆ Simple ⊆ Advanced.
    [Fact]
    public void PresenceNeverShrinksTowardsAdvanced() =>
        Assert.All(WorkViewPolicy.Entries.Where(entry => entry.MonotonyException == null), entry =>
        {
            Assert.True(WorkViewPolicy.Rank(entry.Barebones) <= WorkViewPolicy.Rank(entry.Simple), entry.Id + ": Barebones shows more than Simple.");
            Assert.True(WorkViewPolicy.Rank(entry.Simple) <= WorkViewPolicy.Rank(entry.Advanced), entry.Id + ": Simple shows more than Advanced.");
        });

    [Fact]
    public void MonotonyExceptionsAreExplainedAndNoneIsKnown() =>
        Assert.DoesNotContain(WorkViewPolicy.Entries, entry => entry.MonotonyException != null);

    // R6: a state that differs from its default is shown, wherever the view would reduce the element.
    [Fact]
    public void NonDefaultStateRaisesToDirectAndNeverLowers()
    {
        foreach (WorkViewEntry entry in WorkViewPolicy.Entries)
            foreach (WorkView view in WorkViewSetting.All)
            {
                Presence raised = WorkViewPolicy.Resolve(entry.Id, view, nonDefaultState: true);
                Assert.True(WorkViewPolicy.Rank(raised) >= WorkViewPolicy.Rank(Presence.Direct), entry.Id);
                if (entry.For(view) == Presence.Required) Assert.Equal(Presence.Required, raised);
                Assert.Equal(entry.For(view), WorkViewPolicy.Resolve(entry.Id, view));
            }
    }

    [Fact]
    public void EveryPageHasRowsExceptThePagesStillWaitingForTheirPackage()
    {
        WorkViewPage[] empty = Enum.GetValues<WorkViewPage>().Where(page => WorkViewPolicy.Entries.All(entry => entry.Page != page)).ToArray();
        // Shrinks only: a page package that adds rows must also remove its page from PagesWithoutRowsYet.
        Assert.Equal(PagesWithoutRowsYet, empty);
    }

    [Fact]
    public void EveryColumnGridHasAFallbackRowSoLaterColumnsFollowTheGridRule()
    {
        foreach (string grid in WorkViewPolicy.Entries.Where(entry => entry.Kind == WorkViewKind.Column)
            .Select(entry => entry.Id[..entry.Id.LastIndexOf('.')]).Distinct())
            Assert.NotNull(WorkViewPolicy.Find(grid + ".*"));
        Assert.Equal("downloads.columns.ThreadsDataGrid.*", WorkViewPolicy.ColumnEntryId("downloads.columns.ThreadsDataGrid.Future"));
        Assert.Equal("downloads.columns.ThreadsDataGrid.Title", WorkViewPolicy.ColumnEntryId("downloads.columns.ThreadsDataGrid.Title"));
    }

    [Fact]
    public void SettingStringsRoundTripAndKeepEachCallSitesNormalization()
    {
        Assert.All(WorkViewSetting.All, view => Assert.Equal(view, WorkViewSetting.Parse(view.ToSetting())));
        Assert.Equal(WorkView.Advanced, WorkViewSetting.Parse(" advanced "));
        Assert.Equal(WorkView.Barebones, WorkViewSetting.Parse("unknown"));
        Assert.Equal(WorkView.Barebones, WorkViewSetting.Parse(null));
        // The archive page compared ordinally and fell back to Simple.
        Assert.Equal(WorkView.Simple, WorkViewSetting.Parse("advanced", WorkView.Simple, exact: true));
        Assert.Equal(WorkView.Simple, WorkViewSetting.Parse(" Advanced", WorkView.Simple, exact: true));
        Assert.Equal(WorkView.Advanced, WorkViewSetting.Parse("Advanced", WorkView.Simple, exact: true));
        Assert.Equal(["view-barebones", "view-simple", "view-advanced"], WorkViewSetting.All.Select(view => view.StyleClass()));
    }

    // ---- WV0 ports today's special cases without changing them: the old rules, verbatim, against the policy ----

    private static readonly string[] Modes = ["Barebones", "Simple", "Advanced"];

    [Fact]
    public void DownloadThreadColumnsKeepTheirFormerDefaultsAndChooser()
    {
        string[] columns = ["SourceBoard", "ThreadNumber", "Title", "Status", "Completion", "Queued", "Active", "MissingMedia", "EarliestRetry", "Future"];
        foreach (string mode in Modes)
        {
            bool barebones = mode == "Barebones", advanced = mode == "Advanced";
            WorkView view = WorkViewSetting.Parse(mode);
            foreach (string column in columns)
            {
                bool work = column is "MissingMedia" or "EarliestRetry";
                bool formerDefault = column switch
                {
                    "SourceBoard" => !barebones,
                    "MissingMedia" or "EarliestRetry" => false,
                    "ThreadNumber" or "Status" or "Queued" or "Active" => advanced,
                    _ => true
                };
                string id = WorkViewPolicy.DownloadColumnId("ThreadsDataGrid", column);
                Assert.Equal(!(work && !advanced), WorkViewPolicy.ColumnOffered(id, view));
                foreach (bool? saved in new bool?[] { null, true, false })
                {
                    bool former = work ? advanced && saved == true : saved ?? formerDefault;
                    Assert.Equal(former, WorkViewPolicy.ColumnVisible(id, view, saved));
                }
            }
            Assert.True(WorkViewPolicy.ColumnOffered(WorkViewPolicy.DownloadColumnId("BoardsDataGrid", "NiceName"), view));
        }
    }

    [Fact]
    public void ArchiveColumnsKeepTheirFormerDefaults()
    {
        foreach (string mode in Modes.Append("other"))
        {
            foreach (string key in new[] { "identity", "title", "posts", "media", "source", "work", "lastCapture", "tags", "future" })
                Assert.Equal(mode switch
                {
                    "Barebones" => key is "identity" or "title" or "posts" or "media",
                    "Simple" => key is not "work" and not "lastCapture" and not "tags",
                    _ => true
                }, MeowChan.ArchivePage.DefaultColumnVisible("ArchiveThreadTable", mode, key));
            foreach (string key in new[] { "name", "size", "version", "rating", "tags" })
                Assert.Equal(mode != "Barebones" || key is "name" or "size",
                    MeowChan.ArchivePage.DefaultColumnVisible("ArchiveMediaGrid", mode, key));
        }
    }

    [Fact]
    public void ToolbarsAndShellKeepTheirFormerPlacement()
    {
        foreach (WorkView view in WorkViewSetting.All)
        {
            bool barebones = view == WorkView.Barebones, advanced = view == WorkView.Advanced;
            Ursa.Controls.OverflowMode former = barebones ? Ursa.Controls.OverflowMode.Always : Ursa.Controls.OverflowMode.AsNeeded;
            Assert.Equal(former, WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve("downloads.toolbar.columns", view)));
            Assert.Equal(former, WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve("downloads.toolbar.copy-urls", view)));
            Assert.Equal(former, WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve("archive.results.other", view)));
            Assert.Equal(Ursa.Controls.OverflowMode.AsNeeded, WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve("archive.results.gallery", view)));
            Assert.Equal(Ursa.Controls.OverflowMode.Never, WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve("archive.results.layout-list", view)));
            Assert.Equal(Ursa.Controls.OverflowMode.Never, WorkViewPresenter.ToolBarMode(WorkViewPolicy.Resolve("archive.results.layout-table", view)));
            Assert.Equal(!barebones, WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("downloads.header.heading", view)));
            Assert.Equal(advanced, WorkViewPolicy.OnSurface(WorkViewPolicy.Resolve("downloads.intake.url-hint", view)));
            foreach (string page in MainPageOrder.CommandIds.Skip(1))
                Assert.Equal(page is "navigation.archive" or "navigation.media" ? !barebones : advanced, WorkViewPolicy.FooterDirect(page, view));
            Assert.Equal(advanced, IoStatsDisplay.HeaderShowsDisk(IoStatsDisplay.HeaderGraphAuto, view.ToSetting()));
            Assert.Equal(barebones, WorkViewDensity.Compact(view));
            Assert.Equal(advanced ? 140 : 300, WorkViewDensity.DownloadCompletionWidth(view));
            Assert.Equal(advanced ? 900 : 650, WorkViewDensity.ArchiveTableMinWidth(view));
        }
        Assert.True(IoStatsDisplay.HeaderShowsDisk(IoStatsDisplay.HeaderGraphAuto, " ADVANCED ")); // trimmed, case-insensitive as before
        Assert.False(IoStatsDisplay.HeaderShowsDisk(IoStatsDisplay.HeaderGraphAuto, "unknown"));
    }
}
