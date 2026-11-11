using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class ArchiveFooterLayoutTests
{
    [Fact]
    public void PagerAndRangeShareOneLineWhenBothFit()
    {
        Assert.Equal((true, false), ArchiveFooterLayout.Resolve(true, 96, 69, 254, false));
        // Exact fit including the gap still stays on one line.
        Assert.Equal((true, false), ArchiveFooterLayout.Resolve(true, 100, 50, 100 + ArchiveFooterLayout.InlineGap + 50, false));
    }

    [Fact]
    public void RangeMovesBelowThePagerWhenTheyDoNotFit()
    {
        Assert.Equal((false, true), ArchiveFooterLayout.Resolve(true, 230, 110, 254, false));
    }

    [Fact]
    public void PagingWithinOneResultSetKeepsTwoLinesOnceTheyWereNeeded()
    {
        // A later page with a narrower pager would fit again, but the latch keeps the
        // pager where the pointer is until the result total or width changes.
        Assert.Equal((false, true), ArchiveFooterLayout.Resolve(true, 96, 69, 254, true));
    }

    [Fact]
    public void ASingleVisiblePartIsNeverInlineAndDoesNotChangeTheLatch()
    {
        Assert.Equal((false, false), ArchiveFooterLayout.Resolve(false, 0, 69, 254, false));
        Assert.Equal((false, true), ArchiveFooterLayout.Resolve(false, 0, 69, 254, true));
    }
}
