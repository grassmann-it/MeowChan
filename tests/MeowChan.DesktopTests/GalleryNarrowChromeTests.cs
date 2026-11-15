using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Half-screen gallery windows (Tom, 16.11.): the way back collapses to its arrow from its measured label, not from a breakpoint, and the info line
// shortens its longest segments instead of wrapping. Pure rules; the rendered result is the gallery-narrow smoke (private Xvfb).
public sealed class GalleryNarrowChromeTests
{
    private const double Gap = GalleryViewer.StarGap;

    [Fact]
    public void AnUnknownWidthOrAnUnmeasuredButtonShowsTheLabel()
    {
        Assert.True(GalleryBackLabel.ShowsLabel(0, 136, Gap));
        Assert.True(GalleryBackLabel.ShowsLabel(-1, 136, Gap));
        Assert.True(GalleryBackLabel.ShowsLabel(300, 0, Gap));
    }

    [Theory]
    [InlineData(136)]  // "Back to thread"
    [InlineData(166)]  // "Zurück zum Thread"
    [InlineData(260)]  // a long translation
    public void TheLabelShowsExactlyFromTheWidthTheMeasuredLabelNeeds(double labelled)
    {
        double from = GalleryBackLabel.LabelFromWidth(labelled, Gap);
        Assert.True(GalleryBackLabel.ShowsLabel(from, labelled, Gap));
        Assert.True(GalleryBackLabel.ShowsLabel(from + 200, labelled, Gap));
        Assert.False(GalleryBackLabel.ShowsLabel(from - 1, labelled, Gap));
        Assert.False(GalleryBackLabel.ShowsLabel(from / 2, labelled, Gap));
    }

    [Fact]
    public void ALongerLabelCollapsesAtAWiderViewerThanAShortOne()
    {
        double english = GalleryBackLabel.LabelFromWidth(136, Gap), german = GalleryBackLabel.LabelFromWidth(166, Gap);
        Assert.True(german > english);
        double between = (english + german) / 2;
        Assert.True(GalleryBackLabel.ShowsLabel(between, 136, Gap));
        Assert.False(GalleryBackLabel.ShowsLabel(between, 166, Gap));
    }

    [Fact]
    public void TheHalfScreenWidthKeepsTheLabelInBothLanguages()
    {
        // 945 dip (half of a 1920 px screen), the window minimums (760 and 640) and the longest labels of the catalog: the label fits.
        foreach (double width in new[] { 945, 960, 800, 760, 640 })
        foreach (double labelled in new[] { 136, 166, 190 })
            Assert.True(GalleryBackLabel.ShowsLabel(width, labelled, Gap), $"{labelled} dip label at {width} dip");
    }

    [Fact]
    public void ThePairWidthIsTheMarginsTheButtonTheGapAndTheStar()
    {
        Assert.Equal(12 + 136 + Gap + 32 + 12, GalleryBackLabel.PairWidth(136, Gap));
    }

    // ---- the info line ----

    [Fact]
    public void SegmentsThatFitKeepTheirWidth()
    {
        double[] widths = GalleryInfoSegmentsPanel.Allocate([100, 6, 80, 6, 120], 400, 8);
        Assert.Equal([100, 6, 80, 6, 120], widths);
    }

    [Fact]
    public void AnUnboundedRoomChangesNothing()
    {
        Assert.Equal([300, 500], GalleryInfoSegmentsPanel.Allocate([300, 500], double.PositiveInfinity, 8));
    }

    [Fact]
    public void TheLongestSegmentGivesWayFirstAndTheShortOnesStayWhole()
    {
        double[] natural = [90, 6, 520, 6, 110];
        double[] widths = GalleryInfoSegmentsPanel.Allocate(natural, 500, 8);
        Assert.Equal(90, widths[0]);
        Assert.Equal(6, widths[1]);
        Assert.Equal(6, widths[3]);
        Assert.Equal(110, widths[4]);
        Assert.True(widths[2] < 520);
        Assert.Equal(500, widths.Sum() + 8 * (widths.Length - 1), 6); // the row exactly fills its room
    }

    [Fact]
    public void SeveralLongSegmentsAreCutToOneCommonLevel()
    {
        double[] widths = GalleryInfoSegmentsPanel.Allocate([60, 400, 300], 400, 8);
        Assert.Equal(60, widths[0]);
        Assert.Equal(widths[1], widths[2], 6);   // both cut to the same level
        Assert.True(widths[1] < 300);
        Assert.Equal(400, widths.Sum() + 16, 6);
    }

    [Fact]
    public void ASegmentIsNeverCutBelowTheFloor()
    {
        double[] widths = GalleryInfoSegmentsPanel.Allocate([200, 6, 200], 30, 8);
        Assert.Equal(GalleryInfoSegmentsPanel.MinimumShrunkWidth, widths[0]);
        Assert.Equal(GalleryInfoSegmentsPanel.MinimumShrunkWidth, widths[2]);
        Assert.Equal(6, widths[1]); // a separator is already below the floor and stays
    }

    [Fact]
    public void NothingToLayOutIsEmpty()
    {
        Assert.Empty(GalleryInfoSegmentsPanel.Allocate([], 100, 8));
    }
}
