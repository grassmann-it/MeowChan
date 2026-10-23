using MeowChan.Archive;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class ArchiveAppliedSearchTests
{
    [Theory]
    [InlineData(0, 0)] [InlineData(1, 1)] [InlineData(49, 1)] [InlineData(50, 1)]
    [InlineData(51, 2)] [InlineData(100, 2)] [InlineData(101, 3)] [InlineData(int.MaxValue, 42949673)]
    public void PageBoundsUseFiftyRowsWithoutOverflow(int total, int pages)
    {
        Assert.Equal(pages, ArchivePage.ResultsPageCount(total));
        Assert.False(ArchivePage.TryResultsOffset(0, total, out _));
        Assert.False(ArchivePage.TryResultsOffset(-1, total, out _));
        Assert.False(ArchivePage.TryResultsOffset(int.MaxValue, total, out _));
        if (pages > 0)
        {
            Assert.True(ArchivePage.TryResultsOffset(pages, total, out int offset));
            Assert.Equal(((long)pages - 1) * 50, offset);
            Assert.False(ArchivePage.TryResultsOffset(pages + 1, total, out _));
        }
    }

    [Fact]
    public void RemovingOneAppliedValuePreservesAllOtherCategoriesAndSort()
    {
        ArchiveSearchSpec applied = new("needle", ["A", "B"],
            [new("A", "x"), new("B", "y"), new(null, "global")],
            [ThreadSourceState.Active, ThreadSourceState.Archived], false, true,
            ArchiveThreadSort.Title, ArchiveWorkFilter.Completed,
            new ArchiveDateRange(new(2026, 10, 1), new(2026, 10, 31), "Europe/Berlin"));
        ArchiveSearchSpec next = ArchivePage.CopySearch(applied, "source", "A");
        Assert.Equal(["B"], next.Sources);
        Assert.Equal(applied.Boards, next.Boards);
        Assert.Equal(applied.SourceStates, next.SourceStates);
        Assert.Equal(applied.Text, next.Text);
        Assert.Equal(applied.DateRange, next.DateRange);
        Assert.Equal(applied.WorkFilter, next.WorkFilter);
        Assert.Equal(applied.Sort, next.Sort);
        Assert.Null(ArchivePage.CopySearch(next, "date").DateRange);
        Assert.Equal(["A", "B"], applied.Sources); // original snapshot remains immutable
    }

    [Fact]
    public void GlobalAndQualifiedBoardChipsHaveDistinctStableKeysAndCanBeRemovedSeparately()
    {
        ArchiveSearchSpec applied = new(boards: [new(null, "x"), new("A", "x")]);
        var boards = ArchivePage.AppliedFilterLabels(applied).Where(tag => tag.Field == "board").ToArray();
        Assert.Equal(2, boards.Select(tag => tag.Value).Distinct().Count());
        foreach (var chip in boards)
        {
            ArchiveSearchSpec next = ArchivePage.CopySearch(applied, chip.Field, chip.Value);
            Assert.Single(next.Boards);
            Assert.DoesNotContain(ArchivePage.AppliedFilterLabels(next), tag => tag.Value == chip.Value);
        }
    }

    [Fact]
    public void MultipleSetsAndDateExplainAnEmptyResultWhileSortAloneDoesNot()
    {
        Assert.False(ArchivePage.HasArchiveConstraints(new ArchiveSearchSpec(sort: ArchiveThreadSort.Title)));
        Assert.True(ArchivePage.HasArchiveConstraints(new ArchiveSearchSpec(sources: ["A", "B"])));
        Assert.True(ArchivePage.HasArchiveConstraints(new ArchiveSearchSpec(boards: [new("A", "x")])));
        Assert.True(ArchivePage.HasArchiveConstraints(new ArchiveSearchSpec(dateRange: new(new(2026, 10, 1), null, "Europe/Berlin"))));
        Assert.False(ArchivePage.HasArchiveConstraints(new ArchiveSearchSpec(dateRange: new(null, null, "Europe/Berlin"))));
    }
}
