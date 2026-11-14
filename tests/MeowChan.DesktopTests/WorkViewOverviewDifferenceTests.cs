using System.Reflection;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Views.Settings;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.DesktopTests;

/// <summary>
/// WV12 → WV11 on the actual detached overview and policy/diff model. No Window, native platform,
/// layout/GUI run, configuration, personal profile, database or network. Native layout/a11y remains a parent gate.
/// </summary>
[Collection("Localization")]
public sealed class WorkViewOverviewDifferenceTests
{
    public static IEnumerable<object[]> ViewPairs() =>
        from fromView in WorkViewSetting.All
        from toView in WorkViewSetting.All
        select new object[] { fromView, toView };

    [Theory]
    [MemberData(nameof(ViewPairs))]
    public void DifferenceCanPrecedeFirstOpenAndLocaleRefreshWithoutBuildingAnything(WorkView from, WorkView to) =>
        OnOwnedDispatcher(() =>
        {
            using var overview = new WorkViewOverviewControl(WorkView.Advanced);
            overview.SetPageFilter(WorkViewPolicy.Entries.First().Page);
            overview.SetTextFilter("not a policy element");
            DiffSummary diff = WorkViewPolicy.Diff(from, to);

            overview.SetDifferenceFilter(diff);
            overview.LocaleRefresh();
            overview.SetActiveView(WorkView.Simple);

            Assert.False(overview.IsOverviewLoaded);
            Assert.Null(overview.Content);
            Assert.Empty(Field<IReadOnlyList<WorkViewOverview.Row>>(overview, "_rows"));
            Assert.Null(overview.PageFilter);
            Assert.Equal("", overview.TextFilter);
            Assert.Equal(WorkView.Simple, overview.ActiveView);

            overview.EnsureLoaded();
            AssertIds(diff.Changes.Select(change => change.Entry.Id), View(overview));
            AssertActiveHeaders(overview, WorkView.Simple);
            Assert.True(Clear(overview).IsEnabled); // Even an empty comparison must have a way back.
            AssertStatus(overview);
            DataGrid table = Table(overview);
            DataGridCollectionView view = View(overview);
            overview.EnsureLoaded();
            Assert.Same(table, Table(overview));
            Assert.Same(view, View(overview));
        });

    [Fact]
    public void LoadedDifferenceResetsBothInputsAndRefreshesTheExistingProjectionAndView() =>
        OnOwnedDispatcher(() =>
        {
            using var overview = new WorkViewOverviewControl(WorkView.Simple);
            overview.EnsureLoaded();
            DataGrid table = Table(overview);
            DataGridCollectionView view = View(overview);
            var rows = Field<IReadOnlyList<WorkViewOverview.Row>>(overview, "_rows");
            var predicate = view.Filter;
            overview.SetPageFilter(WorkViewPolicy.Entries.First().Page);
            overview.SetTextFilter("no element has this label");
            Assert.Empty(view.Cast<WorkViewOverview.Row>());
            DiffSummary diff = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones);
            Assert.NotEmpty(diff.Changes);

            overview.SetDifferenceFilter(diff);

            Assert.Null(overview.PageFilter);
            Assert.Equal("", overview.TextFilter);
            Assert.Equal("", Field<TextBox>(overview, "_filter").Text);
            Assert.Equal(0, Field<ComboBox>(overview, "_pages").SelectedIndex);
            AssertIds(diff.Changes.Select(change => change.Entry.Id), view);
            Assert.Same(table, Table(overview));
            Assert.Same(view, View(overview));
            Assert.Same(predicate, view.Filter);
            Assert.Same(rows, Field<IReadOnlyList<WorkViewOverview.Row>>(overview, "_rows"));
            foreach (var row in view.Cast<WorkViewOverview.Row>())
                Assert.Same(rows.Single(original => original.Id == row.Id), row);
            AssertActiveHeaders(overview, WorkView.Simple); // Neither Diff.From nor Diff.To is active.
            AssertStatus(overview);
        });

    [Fact]
    public void LaterPageAndLiteralTextFiltersIntersectRatherThanEscapeTheDifferenceSet() =>
        OnOwnedDispatcher(() =>
        {
            using var overview = new WorkViewOverviewControl();
            DiffSummary diff = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones);
            overview.SetDifferenceFilter(diff);
            overview.EnsureLoaded();
            DataGridCollectionView view = View(overview);
            WorkViewEntry target = diff.Changes.First().Entry;

            overview.SetPageFilter(target.Page);
            overview.SetTextFilter(target.Id.ToUpperInvariant());
            var expected = WorkViewOverview.Build().Where(row => diff.Changes.Any(change => change.Entry.Id == row.Id)
                && WorkViewOverview.Matches(row, target.Page, target.Id.ToUpperInvariant()));
            AssertIds(expected.Select(row => row.Id), view);
            Assert.Contains(view.Cast<WorkViewOverview.Row>(), row => row.Id == target.Id);

            overview.SetTextFilter("^.*$"); // Literal, not a regex that can expand the id set.
            Assert.Empty(view.Cast<WorkViewOverview.Row>());
            AssertStatus(overview);
            overview.SetTextFilter(null);
            AssertIds(diff.Changes.Where(change => change.Entry.Page == target.Page).Select(change => change.Entry.Id), view);
            overview.SetPageFilter(null);
            AssertIds(diff.Changes.Select(change => change.Entry.Id), view);
            overview.SetTextFilter("nothing matches");
            overview.SetPageFilter(target.Page);
            overview.SetDifferenceFilter(diff); // Reopening Details must remove stale local restrictions again.
            AssertIds(diff.Changes.Select(change => change.Entry.Id), view);
            Assert.Same(view, View(overview));
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NullRestoresEveryCurrentPolicyRowBeforeOrAfterLoading(bool loaded) =>
        OnOwnedDispatcher(() =>
        {
            using var overview = new WorkViewOverviewControl();
            overview.SetDifferenceFilter(WorkViewPolicy.Diff(WorkView.Simple, WorkView.Barebones));
            if (loaded) overview.EnsureLoaded();
            var previousView = loaded ? View(overview) : null;
            overview.SetPageFilter(WorkViewPolicy.Entries.First().Page);
            overview.SetTextFilter("not an element");

            overview.SetDifferenceFilter(null);

            Assert.Equal(loaded, overview.IsOverviewLoaded);
            Assert.Null(overview.PageFilter);
            Assert.Equal("", overview.TextFilter);
            overview.EnsureLoaded();
            AssertIds(WorkViewPolicy.Entries.Select(entry => entry.Id), View(overview));
            if (loaded) Assert.Same(previousView, View(overview));
            Assert.False(Clear(overview).IsEnabled);
            AssertStatus(overview);
        });

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void VisibleAccessibleResetClearsEmptyDifferenceAndOrdinaryFiltersWithoutRebuilding(string language) =>
        OnOwnedDispatcher(() =>
        {
            Texts.SetLanguage(language);
            using var overview = new WorkViewOverviewControl();
            overview.SetDifferenceFilter(WorkViewPolicy.Diff(WorkView.Simple, WorkView.Simple));
            overview.EnsureLoaded();
            DataGridCollectionView view = View(overview);
            Button clear = Clear(overview);
            Assert.Empty(view.Cast<WorkViewOverview.Row>());
            Assert.True(clear.IsVisible);
            Assert.True(clear.IsEnabled);
            Assert.NotEqual("diagnostics.clearFilters", Texts.T("diagnostics.clearFilters"));
            Assert.Equal(Texts.T("diagnostics.clearFilters"), Assert.IsType<TextBlock>(clear.Content).Text);
            Assert.Equal(Texts.T("diagnostics.clearFilters"), AutomationProperties.GetName(clear));
            Assert.Contains(clear, Assert.IsType<Grid>(Assert.IsType<Grid>(overview.Content).Children[1]).Children);

            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            AssertIds(WorkViewPolicy.Entries.Select(entry => entry.Id), view);
            Assert.False(clear.IsEnabled);
            overview.SetPageFilter(WorkViewPolicy.Entries.First().Page);
            overview.SetTextFilter("no matches");
            Assert.True(clear.IsEnabled);
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Null(overview.PageFilter);
            Assert.Equal("", overview.TextFilter);
            AssertIds(WorkViewPolicy.Entries.Select(entry => entry.Id), view);
            Assert.Same(view, View(overview));
            Assert.Same(clear, Clear(overview));
            AssertStatus(overview);
        });

    [Fact]
    public void LocaleAndAppliedViewChangesKeepDifferenceIdsLiteralFiltersAndSelection() =>
        OnOwnedDispatcher(() =>
        {
            using var overview = new WorkViewOverviewControl(WorkView.Simple);
            DiffSummary diff = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones);
            overview.SetDifferenceFilter(diff);
            overview.EnsureLoaded();
            DataGrid table = Table(overview);
            HashSet<string> ids = Field<HashSet<string>>(overview, "_differenceIds");
            WorkViewEntry target = diff.Changes.First().Entry;
            overview.SetPageFilter(target.Page);
            overview.SetTextFilter(target.Id);
            table.SelectedItem = View(overview).Cast<WorkViewOverview.Row>().First(row => row.Id == target.Id);
            var expectedIds = View(overview).Cast<WorkViewOverview.Row>().Select(row => row.Id).ToArray();

            foreach (string language in new[] { "de", "en", "de" })
            {
                Texts.SetLanguage(language);
                overview.LocaleRefresh();
                Assert.Same(table, Table(overview));
                Assert.Same(ids, Field<HashSet<string>>(overview, "_differenceIds"));
                Assert.Equal(target.Page, overview.PageFilter);
                Assert.Equal(target.Id, overview.TextFilter);
                Assert.Equal(target.Id, Assert.IsType<WorkViewOverview.Row>(table.SelectedItem).Id);
                AssertIds(expectedIds, View(overview));
                Assert.Equal(Texts.T("diagnostics.clearFilters"), Assert.IsType<TextBlock>(Clear(overview).Content).Text);
                foreach (WorkView active in WorkViewSetting.All)
                {
                    overview.SetActiveView(active);
                    AssertActiveHeaders(overview, active);
                    AssertIds(expectedIds, View(overview));
                    Assert.Same(ids, Field<HashSet<string>>(overview, "_differenceIds"));
                }
                AssertStatus(overview);
            }
            overview.SetPageFilter(null);
            overview.SetTextFilter(null);
            AssertIds(diff.Changes.Select(change => change.Entry.Id), View(overview));
        });

    [Fact]
    public void DifferenceIdsAreAnOrdinalSetOfEntryIdsNotACommandOrLabelProjection() =>
        OnOwnedDispatcher(() =>
        {
            using var overview = new WorkViewOverviewControl();
            var change = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones).Changes.First();
            var wrongCase = new WorkViewDiffChange(change.Entry with { Id = change.Entry.Id.ToUpperInvariant() }, change.From, change.To);
            var onlyWrongCase = new DiffSummary(WorkView.Advanced, WorkView.Barebones, [wrongCase]);
            overview.SetDifferenceFilter(onlyWrongCase);
            overview.EnsureLoaded();
            Assert.Empty(View(overview).Cast<WorkViewOverview.Row>());

            // The real immutable summary constructor is also the boundary seam for duplicate/unknown ids.
            overview.SetDifferenceFilter(new DiffSummary(WorkView.Advanced, WorkView.Barebones, [change, change, wrongCase]));
            AssertIds([change.Entry.Id], View(overview));
            HashSet<string> ids = Field<HashSet<string>>(overview, "_differenceIds");
            Assert.Same(StringComparer.Ordinal, ids.Comparer);
            Assert.Equal(2, ids.Count);
            Assert.True(ids.Contains(change.Entry.Id));
            Assert.True(ids.Contains(wrongCase.Entry.Id));
        });

    [Fact]
    public void NormalProjectionAndPageChoicesFollowTheCurrentPolicyAndPageEnumRatherThanOldPageLists() =>
        OnOwnedDispatcher(() =>
        {
            using var overview = new WorkViewOverviewControl();
            overview.SetDifferenceFilter(null);
            overview.EnsureLoaded();
            ComboBox pages = Field<ComboBox>(overview, "_pages");
            Assert.Equal(Enum.GetValues<WorkViewPage>().Length + 1, pages.ItemCount);
            foreach (WorkViewPage page in Enum.GetValues<WorkViewPage>())
            {
                overview.SetPageFilter(page);
                AssertIds(WorkViewPolicy.Entries.Where(entry => entry.Page == page).Select(entry => entry.Id), View(overview));
                string label = WorkViewOverview.PageLabel(page);
                Assert.False(string.IsNullOrWhiteSpace(label));
                Assert.DoesNotContain("workview.overview.page.", label);
                Assert.Equal(label, pages.SelectedItem!.ToString());
                Assert.All(View(overview).Cast<WorkViewOverview.Row>(), row => Assert.Equal(label, row.PageLabel));
            }
        });

    [Fact]
    public void DisposeReleasesTheIdSetAndResetHandlerAndRejectsFurtherDifferenceCalls() =>
        OnOwnedDispatcher(() =>
        {
            var overview = new WorkViewOverviewControl();
            DiffSummary diff = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones);
            overview.SetDifferenceFilter(diff);
            overview.EnsureLoaded();
            Button clear = Clear(overview);
            DataGridCollectionView view = View(overview);
            overview.Dispose();
            overview.Dispose();
            Assert.Null(overview.Content);
            Assert.False(overview.IsOverviewLoaded);
            Assert.Null(Field<HashSet<string>?>(overview, "_differenceIds"));
            Assert.Null(view.Filter);
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); // Detached handler must not call disposed API.
            Assert.Throws<ObjectDisposedException>(() => overview.SetDifferenceFilter(diff));
            Assert.Throws<ObjectDisposedException>(() => overview.SetDifferenceFilter(null));
        });

    private static DataGrid Table(WorkViewOverviewControl overview) => Field<DataGrid>(overview, "_table");
    private static DataGridCollectionView View(WorkViewOverviewControl overview) => Assert.IsType<DataGridCollectionView>(Table(overview).ItemsSource);
    private static Button Clear(WorkViewOverviewControl overview) => Field<Button>(overview, "_clearFilters");

    private static T Field<T>(WorkViewOverviewControl overview, string name) =>
        (T)(typeof(WorkViewOverviewControl).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Missing overview test seam: " + name)).GetValue(overview)!;

    private static void AssertIds(IEnumerable<string> expected, DataGridCollectionView view) =>
        Assert.Equal(expected.Order(StringComparer.Ordinal), view.Cast<WorkViewOverview.Row>().Select(row => row.Id).Order(StringComparer.Ordinal));

    private static void AssertActiveHeaders(WorkViewOverviewControl overview, WorkView active)
    {
        Assert.Equal(active, overview.ActiveView);
        for (int i = 0; i < WorkViewSetting.All.Count; i++)
        {
            WorkView view = WorkViewSetting.All[i];
            Assert.Equal(WorkViewOverview.ViewLabel(view, view == active), Table(overview).Columns[i + 1].Header);
        }
    }

    private static void AssertStatus(WorkViewOverviewControl overview)
    {
        int count = View(overview).Count;
        string expected = count == 0 ? WorkViewOverviewCatalog.T("workview.overview.empty")
            : WorkViewOverviewCatalog.T("workview.overview.count", count, WorkViewPolicy.Entries.Count);
        Assert.Equal(expected, Field<TextBlock>(overview, "_status").Text);
    }

    // Existing windowless Avalonia 12.1.4 seam (PluginFooterLanguageTransitionBoundaryTests/ThemeLogoTests).
    // Localization disables parallelization. Own the primary dispatcher only during this synchronous body,
    // including control disposal; restore it without resetting dispatchers, draining queues or starting a GUI.
    private static void OnOwnedDispatcher(Action test)
    {
        FieldInfo primarySlot = typeof(Dispatcher).GetField("s_uiThread", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Avalonia 12.1.4 primary dispatcher slot is missing.");
        object? previousPrimary = primarySlot.GetValue(null);
        Dispatcher owner = Dispatcher.CurrentDispatcher;
        string previousLanguage = Texts.Language;
        try
        {
            primarySlot.SetValue(null, owner);
            owner.Invoke(() =>
            {
                owner.VerifyAccess();
                Assert.Same(owner, Dispatcher.UIThread);
                try
                {
                    Texts.Initialize();
                    Texts.SetLanguage("en");
                    test();
                }
                finally
                {
                    Texts.Initialize();
                    Texts.SetLanguage(previousLanguage);
                }
            }, DispatcherPriority.Send);
        }
        finally { primarySlot.SetValue(null, previousPrimary); }
    }
}
