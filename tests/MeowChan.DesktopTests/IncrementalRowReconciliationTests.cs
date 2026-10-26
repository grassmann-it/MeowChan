using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class IncrementalRowReconciliationTests
{
    private sealed record Entry(long Id, int Value);
    private sealed class Row(long id, int value)
    {
        internal readonly long Id = id;
        internal int Value = value;
        internal bool Disposed;
    }
    private sealed class ManualTime : TimeProvider
    {
        internal long Ticks;
        public override long GetTimestamp() => Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
    private static IncrementalRowReconciliation<long, Entry, Row> Plan(Entry[] entries,
        Dictionary<long, Row> rows, ObservableCollection<Row> visible, Action? onUpdate = null, TimeProvider? clock = null) =>
        new(entries, rows, visible, entry => entry.Id, row => row.Id, entry => new Row(entry.Id, entry.Value),
            (row, entry) => { row.Value = entry.Value; onUpdate?.Invoke(); }, row => row.Disposed = true, clock: clock);

    [Fact]
    public void LargeCaptureYieldsAndKeepsRetainedRowsAndSelectionReferences()
    {
        Row[] original = Enumerable.Range(0, 5000).Select(id => new Row(id, 0)).ToArray();
        Dictionary<long, Row> rows = original.ToDictionary(row => row.Id);
        ObservableCollection<Row> visible = new(original);
        Row selection = original[2500], focusedRow = original[2501];
        List<NotifyCollectionChangedAction> events = [];
        visible.CollectionChanged += (_, e) => events.Add(e.Action);
        Entry[] entries = Enumerable.Range(1000, 5000).Select(id => new Entry(id, 1)).ToArray();
        var plan = Plan(entries, rows, visible);
        while (!plan.IsCompleted)
        {
            long before = plan.Operations;
            plan.Step(37);
            Assert.InRange(plan.Operations - before, 1, 37);
        }
        Assert.True(plan.Slices > 100);
        Assert.Equal(5000, visible.Count);
        Assert.Equal(entries.Select(entry => entry.Id), visible.Select(row => row.Id));
        Assert.Same(selection, rows[selection.Id]);
        Assert.Same(focusedRow, rows[focusedRow.Id]);
        Assert.All(visible, row => Assert.Equal(1, row.Value));
        Assert.All(original.Take(1000), row => Assert.True(row.Disposed));
        Assert.All(original.Skip(1000), row => Assert.False(row.Disposed));
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, events);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Replace, events);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Move, events);
    }

    [Fact]
    public void CaptureOrderDoesNotReorderStableRowsOrChangeUserSortCurrency()
    {
        Row a = new(1, 0), b = new(2, 0);
        Dictionary<long, Row> rows = new() { [1] = a, [2] = b };
        ObservableCollection<Row> visible = [a, b];
        var plan = Plan([new(2, 2), new(1, 1)], rows, visible);
        while (!plan.Step(1)) { }
        Assert.Equal(new[] { a, b }, visible);
        Assert.Equal(1, a.Value); Assert.Equal(2, b.Value);
    }

    [Fact]
    public void TimeBudgetYieldsAfterBoundedIndividualRowWorkAndMeasuresCost()
    {
        ManualTime clock = new();
        Row[] original = Enumerable.Range(0, 20).Select(id => new Row(id, 0)).ToArray();
        Dictionary<long, Row> rows = original.ToDictionary(row => row.Id);
        ObservableCollection<Row> visible = new(original);
        var plan = Plan(original.Select(row => new Entry(row.Id, 1)).ToArray(), rows, visible,
            () => clock.Ticks += TimeSpan.FromMilliseconds(2).Ticks, clock);
        Assert.False(plan.Step(1000, TimeSpan.FromMilliseconds(5)));
        Assert.Equal(3, visible.Count(row => row.Value == 1));
        Assert.Equal(TimeSpan.FromMilliseconds(6), plan.LongestSlice);
        while (!plan.Step(1000, TimeSpan.FromMilliseconds(5))) { }
        Assert.All(visible, row => Assert.Equal(1, row.Value));
    }

    [Fact]
    public void DuplicateIdentityIsRejectedBeforeAnyCollectionMutation()
    {
        Row row = new(1, 0);
        Dictionary<long, Row> rows = new() { [1] = row };
        ObservableCollection<Row> visible = [row];
        var plan = Plan([new(2, 1), new(2, 2)], rows, visible);
        Assert.Throws<ArgumentException>(() => plan.Step());
        Assert.Same(row, Assert.Single(visible));
        Assert.False(row.Disposed);
    }

    [Fact]
    public void EmptyCaptureRemovesInSlicesAndCompletionIsIdempotent()
    {
        Row[] original = Enumerable.Range(0, 10).Select(id => new Row(id, 0)).ToArray();
        Dictionary<long, Row> rows = original.ToDictionary(row => row.Id);
        ObservableCollection<Row> visible = new(original);
        var plan = Plan([], rows, visible);
        while (!plan.Step(2)) { }
        Assert.Empty(rows); Assert.Empty(visible);
        Assert.All(original, row => Assert.True(row.Disposed));
        long slices = plan.Slices;
        Assert.True(plan.Step());
        Assert.Equal(slices, plan.Slices);
    }
}
