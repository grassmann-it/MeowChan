using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class DownloadSearchReconciliationTests
{
    private sealed record Row(int Id);
    private sealed class ManualTime : TimeProvider
    {
        internal long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }

    [Fact]
    public void LargeFilterYieldsAndRemovesOnlyRealNonResultsWithoutReset()
    {
        Row[] source = Enumerable.Range(0, 3000).Select(id => new Row(id)).ToArray();
        ObservableCollection<Row> visible = new(source.Take(2000));
        HashSet<Row> selection = [source[2], source[3]];
        List<Row> excluded = [];
        List<NotifyCollectionChangedAction> changes = [];
        visible.CollectionChanged += (_, e) => changes.Add(e.Action);
        var pass = new DownloadSearchReconciliation<Row>(source, visible, row => row.Id % 2 == 1,
            row => { excluded.Add(row); selection.Remove(row); });
        while (!pass.IsCompleted)
        {
            long before = pass.Operations;
            pass.Step(17);
            Assert.InRange(pass.Operations - before, 1, 17);
        }
        Assert.Equal(source.Where(row => row.Id % 2 == 1), visible);
        Assert.All(visible, row => Assert.Same(source[row.Id], row));
        Assert.Equal(1000, excluded.Count);
        Assert.Same(source[3], Assert.Single(selection));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Replace, changes);
        Assert.True(pass.Slices > 100);
    }

    [Fact]
    public void ArbitraryReorderingBoundsEvenIndexSearchWork()
    {
        Row[] source = Enumerable.Range(0, 40).Select(id => new Row(id)).ToArray();
        ObservableCollection<Row> visible = new(source.Reverse());
        var pass = new DownloadSearchReconciliation<Row>(source, visible, _ => true);
        while (!pass.IsCompleted)
        {
            long before = pass.Operations;
            pass.Step(1);
            Assert.InRange(pass.Operations - before, 0, 1);
        }
        Assert.Equal(source, visible);
        Assert.True(pass.Slices > source.Length * 2);
        Assert.All(visible, row => Assert.Same(source[row.Id], row));
    }

    [Fact]
    public void EquivalentValueRowsRemainDistinctWatchInstances()
    {
        Row removed = new(1), retained = new(1);
        ObservableCollection<Row> visible = [removed];
        List<Row> excluded = [];
        var pass = new DownloadSearchReconciliation<Row>([retained], visible, _ => true, excluded.Add);
        while (!pass.Step(1)) { }
        Assert.Same(removed, Assert.Single(excluded));
        Assert.Same(retained, Assert.Single(visible));
    }

    [Fact]
    public void TimeBudgetBoundsFilterWorkBeforeAnyCollectionMutation()
    {
        ManualTime clock = new();
        Row[] source = Enumerable.Range(0, 10).Select(id => new Row(id)).ToArray();
        ObservableCollection<Row> visible = [];
        var pass = new DownloadSearchReconciliation<Row>(source, visible,
            _ => { clock.Ticks += TimeSpan.FromMilliseconds(2).Ticks; return true; }, clock: clock);
        Assert.False(pass.Step(1000, TimeSpan.FromMilliseconds(5)));
        Assert.Equal(3, pass.Result.Count);
        Assert.Empty(visible);
        Assert.False(pass.HasFiltered);
        Assert.Equal(TimeSpan.FromMilliseconds(6), pass.LongestSlice);
        while (!pass.Step(1000, TimeSpan.FromMilliseconds(5))) { }
        Assert.Equal(source, visible);
    }
}
