using System.Collections;
using MeowChan.Desktop.WorkViews;
using static MeowChan.Desktop.WorkViews.Presence;

namespace MeowChan.DesktopTests;

/// <summary>WV12: pure default-policy comparisons; no Avalonia initialization or personal profile.</summary>
public sealed class WorkViewDiffTests
{
    public static IEnumerable<object[]> ViewPairs() =>
        from fromView in WorkViewSetting.All
        from toView in WorkViewSetting.All
        select new object[] { fromView, toView };

    [Theory]
    [MemberData(nameof(ViewPairs))]
    public void EveryChangedPresenceAppearsExactlyOnceInPolicyOrder(WorkView from, WorkView to)
    {
        DiffSummary diff = WorkViewPolicy.Diff(from, to);
        WorkViewEntry[] expected = WorkViewPolicy.Entries.Where(entry => entry.For(from) != entry.For(to)).ToArray();

        Assert.Equal(from, diff.From);
        Assert.Equal(to, diff.To);
        Assert.Equal(expected.Select(entry => entry.Id), diff.Changes.Select(change => change.Entry.Id));
        Assert.Equal(expected.Length, diff.Changes.Select(change => change.Entry.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(expected.Length, diff.Counts.Total);
        Assert.Equal(expected.Length == 0, diff.IsEmpty);
        Assert.All(diff.Changes, change =>
        {
            Assert.Same(WorkViewPolicy.Get(change.Entry.Id), change.Entry);
            Assert.Equal(change.Entry.For(from), change.From);
            Assert.Equal(change.Entry.For(to), change.To);
            Assert.NotEqual(change.From, change.To);
            Assert.Equal(WorkViewPolicy.Rank(change.To) - WorkViewPolicy.Rank(change.From), change.RankDelta);
        });
        AssertCounts(diff.Changes, diff.Counts);
    }

    [Theory]
    [MemberData(nameof(ViewPairs))]
    public void ReverseKeepsRowsAndReversesEveryPresenceAndRank(WorkView from, WorkView to)
    {
        DiffSummary forward = WorkViewPolicy.Diff(from, to), reverse = WorkViewPolicy.Diff(to, from);
        Assert.Equal(forward.Changes.Select(change => change.Entry.Id), reverse.Changes.Select(change => change.Entry.Id));
        foreach (var (a, b) in forward.Changes.Zip(reverse.Changes))
        {
            Assert.Same(a.Entry, b.Entry);
            Assert.Equal(a.From, b.To);
            Assert.Equal(a.To, b.From);
            Assert.Equal(a.FromPlacement, b.ToPlacement);
            Assert.Equal(a.ToPlacement, b.FromPlacement);
            Assert.Equal(a.RankDelta, -b.RankDelta);
        }
        Assert.Equal(forward.Counts.Total, reverse.Counts.Total);
        Assert.Equal(forward.Counts.MoreDirect, reverse.Counts.MoreReduced);
        Assert.Equal(forward.Counts.MoreReduced, reverse.Counts.MoreDirect);
        Assert.Equal(forward.Counts.SameRank, reverse.Counts.SameRank);
        Assert.Equal(forward.Pages.Select(page => page.Page), reverse.Pages.Select(page => page.Page));
    }

    [Theory]
    [MemberData(nameof(ViewPairs))]
    public void PageCountsPartitionTheWholeDiffWithoutEmptyPages(WorkView from, WorkView to)
    {
        DiffSummary diff = WorkViewPolicy.Diff(from, to);
        Assert.Equal(diff.Changes.Select(change => change.Entry.Page).Distinct(), diff.Pages.Select(page => page.Page));
        Assert.Equal(diff.Counts.Total, diff.Pages.Sum(page => page.Counts.Total));
        Assert.Equal(diff.Counts.MoreDirect, diff.Pages.Sum(page => page.Counts.MoreDirect));
        Assert.Equal(diff.Counts.MoreReduced, diff.Pages.Sum(page => page.Counts.MoreReduced));
        foreach (WorkViewPageDiff page in diff.Pages)
        {
            Assert.NotEmpty(page.Changes);
            Assert.Equal(diff.Changes.Where(change => change.Entry.Page == page.Page), page.Changes);
            AssertCounts(page.Changes, page.Counts);
        }
        foreach (WorkViewDiffPlacement placement in Enum.GetValues<WorkViewDiffPlacement>())
            Assert.Equal(diff.Changes.Count(change => change.ToPlacement == placement),
                diff.Pages.Sum(page => page.Changes.Count(change => change.ToPlacement == placement)));
    }

    [Theory]
    [MemberData(nameof(ViewPairs))]
    public void RequiredAndSafetyRowsNeverEnterTheActualPolicyDiff(WorkView from, WorkView to)
    {
        Assert.DoesNotContain(WorkViewPolicy.Diff(from, to).Changes, change => change.IsLocked
            || change.From == Required || change.To == Required || change.Entry.Safety);
    }

    [Fact]
    public void SameViewIsAnEmptyComparisonInEveryView()
    {
        foreach (WorkView view in WorkViewSetting.All)
        {
            DiffSummary diff = WorkViewPolicy.Diff(view, view);
            Assert.True(diff.IsEmpty);
            Assert.Empty(diff.Changes);
            Assert.Empty(diff.Pages);
            AssertCounts(diff.Changes, diff.Counts);
        }
    }

    [Fact]
    public void ColumnsAreDefaultsNotToolsUnderMoreOrHiddenData()
    {
        DiffSummary down = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones);
        WorkViewDiffChange off = Change(down, "downloads.columns.ThreadsDataGrid.SourceBoard");
        Assert.Equal(WorkViewKind.Column, off.Entry.Kind);
        Assert.Equal(WorkViewDiffPlacement.ColumnOn, off.FromPlacement);
        Assert.Equal(WorkViewDiffPlacement.ColumnOff, off.ToPlacement);
        Assert.Equal(WorkViewDiffPlacement.ColumnNotOffered,
            Change(down, "downloads.columns.ThreadsDataGrid.MissingMedia").ToPlacement);
        Assert.Equal(WorkViewDiffPlacement.ColumnOff,
            Change(down, "archive.columns.ArchiveThreadTable.*").ToPlacement);

        foreach (WorkView from in WorkViewSetting.All)
            foreach (WorkView to in WorkViewSetting.All)
                foreach (WorkViewDiffChange change in WorkViewPolicy.Diff(from, to).Changes.Where(change => change.Entry.Kind == WorkViewKind.Column))
                {
                    AssertColumnPlacement(change.From, change.FromPlacement);
                    AssertColumnPlacement(change.To, change.ToPlacement);
                }

        // This transition only offers these technical columns; both views still default them to off.
        WorkViewDiffChange offered = Change(WorkViewPolicy.Diff(WorkView.Barebones, WorkView.Simple),
            "downloads.columns.ThreadsDataGrid.MissingMedia");
        Assert.Equal(Hidden, offered.From);
        Assert.Equal(Overflow, offered.To);
        Assert.Equal(WorkViewDiffPlacement.ColumnOff, offered.ToPlacement);
        Assert.True(offered.RankDelta > 0);
        Assert.All(down.Changes.Where(change => change.ToPlacement is WorkViewDiffPlacement.Overflow or WorkViewDiffPlacement.Hidden),
            change => Assert.NotEqual(WorkViewKind.Column, change.Entry.Kind));
    }

    [Fact]
    public void AllColumnTransitionsCountChooserDefaultsSeparately()
    {
        Presence[] presences = [Hidden, Overflow, Direct];
        WorkViewEntry[] entries = (from before in presences from after in presences
            select Row("test.column." + before + "." + after, before, after, after)
                with { Kind = WorkViewKind.Column }).ToArray();
        DiffSummary diff = WorkViewPolicy.Diff(WorkView.Barebones, WorkView.Simple, entries);
        Assert.Equal(6, diff.Counts.Total);
        Assert.Equal(3, diff.Counts.MoreDirect);
        Assert.Equal(3, diff.Counts.MoreReduced);
        Assert.Equal(2, diff.Counts.ToColumnOn);
        Assert.Equal(2, diff.Counts.ToColumnOff);
        Assert.Equal(2, diff.Counts.ToColumnNotOffered);
        Assert.Equal(0, diff.Counts.ToDirect + diff.Counts.ToOverflow + diff.Counts.ToCollapsed + diff.Counts.ToHidden);
        AssertCounts(diff.Changes, diff.Counts);
    }

    [Fact]
    public void PageOrderIsFirstAppearanceEvenWhenRowsAreInterleaved()
    {
        WorkViewEntry[] entries =
        [
            Row("test.first", Hidden, Direct, Direct) with { Page = WorkViewPage.Media },
            Row("test.second", Collapsed, Direct, Direct) with { Page = WorkViewPage.Settings },
            Row("test.third", Overflow, Direct, Direct) with { Page = WorkViewPage.Media }
        ];
        DiffSummary diff = WorkViewPolicy.Diff(WorkView.Barebones, WorkView.Simple, entries);
        Assert.Equal(new[] { WorkViewPage.Media, WorkViewPage.Settings }, diff.Pages.Select(page => page.Page));
        Assert.Equal(new[] { "test.first", "test.third" }, diff.Pages[0].Changes.Select(change => change.Entry.Id));
        Assert.Equal(new[] { "test.first", "test.second", "test.third" }, diff.Changes.Select(change => change.Entry.Id));
        Assert.Equal(2, diff.Pages[0].Counts.Total);
        Assert.Equal(1, diff.Pages[1].Counts.Total);
    }

    [Fact]
    public void RawRowsKeepAlternativesCommandsSectionsAndDerivedDefaults()
    {
        DiffSummary diff = WorkViewPolicy.Diff(WorkView.Advanced, WorkView.Barebones);
        WorkViewDiffChange hidden = Change(diff, "downloads.header.heading");
        Assert.Equal(WorkViewDiffPlacement.Hidden, hidden.ToPlacement);
        Assert.Equal("shell.footer.downloads", hidden.Entry.Alternative);
        WorkViewDiffChange action = Change(diff, "downloads.toolbar.search");
        Assert.Equal(WorkViewDiffPlacement.Overflow, action.ToPlacement);
        Assert.Equal("input.focus-download-search", action.Entry.CommandId);
        Assert.Equal(WorkViewKind.Destination, Change(diff, "shell.footer.tagging").Entry.Kind);
        WorkViewDiffChange section = Change(diff, "settings.group.interface-more");
        Assert.Equal(WorkViewKind.Section, section.Entry.Kind);
        Assert.Equal(WorkViewDiffPlacement.Collapsed, section.ToPlacement);
        Assert.Equal(WorkViewKind.Derived, Change(diff, "shell.header.disk-graph").Entry.Kind);
        // R6 is not live input to the diff: a default reduction is still listed even if state can raise it at runtime.
        Assert.Equal(Direct, WorkViewPolicy.Resolve(action.Entry.Id, WorkView.Barebones, nonDefaultState: true));
        Assert.Equal(Overflow, action.To);
    }

    [Fact]
    public void ChangedRowsWithSharedCommandIdsAreNotDeduplicated()
    {
        WorkViewEntry[] entries =
        [
            Row("test.z", Overflow, Direct, Direct) with { CommandId = "same.command", OverflowOrder = 20 },
            Row("test.a", Hidden, Direct, Direct) with { CommandId = "same.command", Alternative = "other.command", OverflowOrder = 10 },
            Row("test.unchanged", Direct, Direct, Direct)
        ];
        DiffSummary diff = WorkViewPolicy.Diff(WorkView.Barebones, WorkView.Simple, entries);
        Assert.Equal(new[] { "test.z", "test.a" }, diff.Changes.Select(change => change.Entry.Id));
        Assert.Equal(2, diff.Counts.Total);
        Assert.Equal(2, diff.Counts.MoreDirect);
        Assert.Equal(2, diff.Counts.ToDirect);
    }

    [Fact]
    public void EveryNonColumnTransitionCountsOneRowNotRankSteps()
    {
        Presence[] presences = [Hidden, Overflow, Collapsed, Direct];
        WorkViewEntry[] entries = (from before in presences from after in presences
            select Row("test." + before + "." + after, before, after, after)).ToArray();
        DiffSummary diff = WorkViewPolicy.Diff(WorkView.Barebones, WorkView.Simple, entries);
        Assert.Equal(12, diff.Counts.Total);
        Assert.Equal(6, diff.Counts.MoreDirect);
        Assert.Equal(6, diff.Counts.MoreReduced);
        Assert.Equal(0, diff.Counts.SameRank);
        Assert.Equal(3, diff.Counts.ToDirect);
        Assert.Equal(3, diff.Counts.ToCollapsed);
        Assert.Equal(3, diff.Counts.ToOverflow);
        Assert.Equal(3, diff.Counts.ToHidden);
        AssertCounts(diff.Changes, diff.Counts);
    }

    [Fact]
    public void RequiredAndDirectHaveEqualRankButDifferentPresenceStillAppearsOnce()
    {
        WorkViewEntry row = Row("test.lock", Required, Direct, Required);
        DiffSummary diff = WorkViewPolicy.Diff(WorkView.Barebones, WorkView.Simple, [row]);
        WorkViewDiffChange change = Assert.Single(diff.Changes);
        Assert.True(change.IsLocked);
        Assert.Equal(0, change.RankDelta);
        Assert.Equal(WorkViewDiffPlacement.Direct, change.FromPlacement);
        Assert.Equal(WorkViewDiffPlacement.Direct, change.ToPlacement);
        Assert.Equal(1, diff.Counts.SameRank);
        Assert.Equal(0, diff.Counts.MoreDirect);
        Assert.Equal(0, diff.Counts.MoreReduced);
        Assert.Equal(1, diff.Counts.ToDirect);
        DiffSummary reverse = WorkViewPolicy.Diff(WorkView.Simple, WorkView.Barebones, [row]);
        Assert.Equal(Required, Assert.Single(reverse.Changes).To);
        AssertCounts(reverse.Changes, reverse.Counts);
    }

    [Theory]
    [InlineData(Hidden)]
    [InlineData(Overflow)]
    [InlineData(Collapsed)]
    public void InvalidRequiredReductionsFailRatherThanClaimHideability(Presence reduced)
    {
        WorkViewEntry row = Row("test.lock", Required, reduced, Direct);
        Assert.Throws<InvalidOperationException>(() => WorkViewPolicy.Diff(WorkView.Barebones, WorkView.Simple, [row]));
        Assert.Throws<InvalidOperationException>(() => WorkViewPolicy.Diff(WorkView.Simple, WorkView.Barebones, [row]));
    }

    [Fact]
    public void InvalidPolicyDoesNotDisappearBehindANoOpOrSafetyFilter()
    {
        WorkViewEntry unsafeRow = Row("test.safety", Direct, Direct, Direct) with { Safety = true };
        Assert.Throws<InvalidOperationException>(() => WorkViewPolicy.Diff(WorkView.Simple, WorkView.Simple, [unsafeRow]));
        WorkViewEntry invalidColumn = Row("test.column", Collapsed, Direct, Direct) with { Kind = WorkViewKind.Column };
        Assert.Throws<InvalidOperationException>(() => WorkViewPolicy.Diff(WorkView.Simple, WorkView.Advanced, [invalidColumn]));
        WorkViewEntry invalidPresence = Row("test.presence", (Presence)999, Direct, Direct);
        Assert.Throws<InvalidOperationException>(() => WorkViewPolicy.Diff(WorkView.Simple, WorkView.Advanced, [invalidPresence]));
        WorkViewEntry row = Row("test.duplicate", Hidden, Direct, Direct);
        Assert.Throws<InvalidOperationException>(() => WorkViewPolicy.Diff(WorkView.Barebones, WorkView.Simple, [row, row]));
    }

    [Fact]
    public void UnknownViewsAndNullSnapshotsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkViewPolicy.Diff((WorkView)(-1), WorkView.Simple));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkViewPolicy.Diff(WorkView.Simple, (WorkView)999));
        Assert.Throws<ArgumentNullException>(() => WorkViewPolicy.Diff(WorkView.Simple, WorkView.Advanced, null!));
    }

    [Fact]
    public void SummaryIsASingleEnumerationReadOnlySnapshotIncludingItsPages()
    {
        WorkViewEntry[] rows = [Row("test.one", Hidden, Direct, Direct)];
        var once = new Once(rows);
        DiffSummary diff = WorkViewPolicy.Diff(WorkView.Barebones, WorkView.Simple, once);
        Assert.Equal(1, once.Enumerations);
        rows[0] = Row("test.replaced", Direct, Direct, Direct);
        Assert.Equal("test.one", Assert.Single(diff.Changes).Entry.Id);
        Assert.True(((IList<WorkViewDiffChange>)diff.Changes).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((IList<WorkViewDiffChange>)diff.Changes).Clear());
        Assert.True(((IList<WorkViewPageDiff>)diff.Pages).IsReadOnly);
        Assert.Throws<NotSupportedException>(() => ((IList<WorkViewPageDiff>)diff.Pages).Clear());
        WorkViewPageDiff page = Assert.Single(diff.Pages);
        Assert.Same(diff.Changes[0], Assert.Single(page.Changes));
        Assert.Throws<NotSupportedException>(() => ((IList<WorkViewDiffChange>)page.Changes).Clear());
        Assert.Equal(1, diff.Counts.Total);
        Assert.Equal(1, once.Enumerations);
        Assert.True(WorkViewPolicy.Diff(WorkView.Simple, WorkView.Advanced, []).IsEmpty);
    }

    private static WorkViewEntry Row(string id, Presence barebones, Presence simple, Presence advanced) =>
        new(id, WorkViewPage.Shell, WorkViewKind.Element, barebones, simple, advanced);

    private static WorkViewDiffChange Change(DiffSummary diff, string id) =>
        Assert.Single(diff.Changes, change => change.Entry.Id == id);

    private static void AssertColumnPlacement(Presence presence, WorkViewDiffPlacement placement) =>
        Assert.Equal(presence switch
        {
            Direct or Required => WorkViewDiffPlacement.ColumnOn,
            Overflow => WorkViewDiffPlacement.ColumnOff,
            Hidden => WorkViewDiffPlacement.ColumnNotOffered,
            _ => throw new InvalidOperationException("Unexpected column presence in the policy.")
        }, placement);

    private static void AssertCounts(IReadOnlyList<WorkViewDiffChange> changes, WorkViewDiffCounts counts)
    {
        Assert.Equal(changes.Count, counts.Total);
        Assert.Equal(changes.Count(change => WorkViewPolicy.Rank(change.To) > WorkViewPolicy.Rank(change.From)), counts.MoreDirect);
        Assert.Equal(changes.Count(change => WorkViewPolicy.Rank(change.To) < WorkViewPolicy.Rank(change.From)), counts.MoreReduced);
        Assert.Equal(changes.Count(change => WorkViewPolicy.Rank(change.To) == WorkViewPolicy.Rank(change.From)), counts.SameRank);
        Assert.Equal(counts.Total, counts.MoreDirect + counts.MoreReduced + counts.SameRank);
        Assert.Equal(changes.Count(change => change.Entry.Kind != WorkViewKind.Column && change.To is Direct or Required), counts.ToDirect);
        Assert.Equal(changes.Count(change => change.Entry.Kind != WorkViewKind.Column && change.To == Overflow), counts.ToOverflow);
        Assert.Equal(changes.Count(change => change.Entry.Kind != WorkViewKind.Column && change.To == Collapsed), counts.ToCollapsed);
        Assert.Equal(changes.Count(change => change.Entry.Kind != WorkViewKind.Column && change.To == Hidden), counts.ToHidden);
        Assert.Equal(changes.Count(change => change.Entry.Kind == WorkViewKind.Column && change.To is Direct or Required), counts.ToColumnOn);
        Assert.Equal(changes.Count(change => change.Entry.Kind == WorkViewKind.Column && change.To == Overflow), counts.ToColumnOff);
        Assert.Equal(changes.Count(change => change.Entry.Kind == WorkViewKind.Column && change.To == Hidden), counts.ToColumnNotOffered);
        Assert.Equal(counts.Total, counts.ToDirect + counts.ToOverflow + counts.ToCollapsed + counts.ToHidden
            + counts.ToColumnOn + counts.ToColumnOff + counts.ToColumnNotOffered);
    }

    private sealed class Once(IEnumerable<WorkViewEntry> entries) : IEnumerable<WorkViewEntry>
    {
        public int Enumerations { get; private set; }
        public IEnumerator<WorkViewEntry> GetEnumerator()
        {
            if (++Enumerations != 1) throw new InvalidOperationException("Snapshot was enumerated more than once.");
            return entries.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
