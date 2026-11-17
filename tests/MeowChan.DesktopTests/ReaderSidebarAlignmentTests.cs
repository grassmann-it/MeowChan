using Avalonia;
using MeowChan;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

/// <summary>
/// Tom (16.11.2026): the reader sidebar's media frame has exactly the top and bottom of the reader's bordered box, and its header line stands above the
/// frame. The arithmetic (like the Media page's panel alignment) is pure; the layout itself is captured in the reader-sidebar smokes (archive view and
/// large reader, left and right, 1920 and 1280, en and de, light and dark).
/// </summary>
public sealed class ReaderSidebarAlignmentTests
{
    private const double Header = ReaderMediaSidebar.HeaderLineHeight, Gap = ReaderMediaSidebar.HeaderGap;

    private static string Source(string relative, [System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", relative)));

    [Fact]
    public void TheHeaderLineAndItsGapAreTheMediaPanelsLift()
    {
        Assert.Equal(32, Header);
        Assert.Equal(8, Gap);
        Assert.Equal(MediaPage.PanelHeaderLineHeight, Header);
        Assert.Equal(Header + Gap, ReaderSidebarAlignment.HeaderBand);
        // The archive view's sidebar reaches up by exactly that band, as the panel's negative top margin does.
        Assert.Equal(-(MediaPage.PanelHeaderLineHeight + Gap), ReaderSidebarAlignment.HostMargin(archiveView: true).Top);
        Assert.Equal(default, ReaderSidebarAlignment.HostMargin(archiveView: false));
    }

    [Fact]
    public void InTheArchiveViewTheLiftedSidebarNeedsNoInsetAndItsFrameHasTheBoxEdges()
    {
        // The pane's box row starts at 96 (header rows 88 + 8 spacing) and ends at 984; the sidebar reaches 40 above it and ends with it.
        double boxTop = 96, boxBottom = 984;
        double sidebarTop = boxTop - ReaderSidebarAlignment.HeaderBand, sidebarBottom = boxBottom;
        ReaderSidebarAlignment.Insets insets = ReaderSidebarAlignment.InsetsFor(sidebarTop, sidebarBottom, boxTop, boxBottom);
        Assert.Equal(new ReaderSidebarAlignment.Insets(0, 0), insets);
        ReaderSidebarAlignment.Placement placement = ReaderSidebarAlignment.PlacementOf(sidebarTop, sidebarBottom, insets);
        Assert.Equal(boxTop, placement.FrameTop);
        Assert.Equal(boxBottom, placement.FrameBottom);
        Assert.Equal(boxTop - Gap, placement.HeaderBottom);
        Assert.Equal(boxTop - Gap - Header, placement.HeaderTop);
        Assert.True(ReaderSidebarAlignment.IsAligned(placement, boxTop, boxBottom));
    }

    [Theory]
    [InlineData(0, 1000, 96, 984)]    // one toolbar row
    [InlineData(0, 1000, 136, 984)]   // a wrapped toolbar (a longer language) and a status line
    [InlineData(0, 1000, 232, 1000)]  // find bar, tag bar and wrapped toolbar open; no gap under the box
    [InlineData(0, 780, 96, 764)]     // the 1280 wide window's height
    [InlineData(20, 1000, 120, 970)]  // the frame in the shell: the reader does not start at the host's top
    public void InTheLargeReaderTheFrameTakesTheBoxEdgesWhateverTheHeaderRowsHeightIs(double sidebarTop, double sidebarBottom, double boxTop, double boxBottom)
    {
        ReaderSidebarAlignment.Insets insets = ReaderSidebarAlignment.InsetsFor(sidebarTop, sidebarBottom, boxTop, boxBottom);
        ReaderSidebarAlignment.Placement placement = ReaderSidebarAlignment.PlacementOf(sidebarTop, sidebarBottom, insets);
        Assert.Equal(boxTop, placement.FrameTop, 3);
        Assert.Equal(boxBottom, placement.FrameBottom, 3);
        // The title line is above the frame, one gap away, and inside the sidebar.
        Assert.Equal(placement.FrameTop - Gap, placement.HeaderBottom, 3);
        Assert.Equal(Header, placement.HeaderBottom - placement.HeaderTop, 3);
        Assert.True(placement.HeaderTop >= sidebarTop);
        Assert.True(ReaderSidebarAlignment.IsAligned(placement, boxTop, boxBottom));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(104)]
    [InlineData(144)]
    [InlineData(208)]
    public void AToolbarThatWrapsOrCompactsMovesTheFrameWithTheBoxNotTheOtherWayRound(double boxTop)
    {
        // The same sidebar before and after the header rows changed height: the top inset follows the box, the bottom inset stays.
        ReaderSidebarAlignment.Insets first = ReaderSidebarAlignment.InsetsFor(0, 1000, 96, 984), now = ReaderSidebarAlignment.InsetsFor(0, 1000, boxTop, 984);
        Assert.Equal(boxTop - ReaderSidebarAlignment.HeaderBand, now.Top);
        Assert.Equal(first.Bottom, now.Bottom);
        Assert.True(ReaderSidebarAlignment.IsAligned(ReaderSidebarAlignment.PlacementOf(0, 1000, now), boxTop, 984));
    }

    [Fact]
    public void WithoutRoomAboveTheBoxTheHeaderStaysInTheSidebarAndTheAlignmentIsReportedAsMissed()
    {
        ReaderSidebarAlignment.Insets insets = ReaderSidebarAlignment.InsetsFor(0, 600, 24, 590);
        Assert.Equal(0, insets.Top);
        Assert.Equal(10, insets.Bottom);
        ReaderSidebarAlignment.Placement placement = ReaderSidebarAlignment.PlacementOf(0, 600, insets);
        Assert.Equal(0, placement.HeaderTop);
        Assert.Equal(ReaderSidebarAlignment.HeaderBand, placement.FrameTop);
        Assert.False(ReaderSidebarAlignment.IsAligned(placement, 24, 590));
    }

    [Fact]
    public void ABoxThatEndsBelowTheSidebarNeverGivesANegativeBottomInset()
    {
        Assert.Equal(0, ReaderSidebarAlignment.InsetsFor(0, 900, 100, 950).Bottom);
    }

    [Fact]
    public void OnlyRealDifferencesChangeThePaddingAndUnmeasuredValuesAreNotUsed()
    {
        Thickness padding = new(16, 56, 16, 16);
        Assert.False(ReaderSidebarAlignment.Differs(padding, new ReaderSidebarAlignment.Insets(56.1, 15.9)));
        Assert.True(ReaderSidebarAlignment.Differs(padding, new ReaderSidebarAlignment.Insets(57, 16)));
        Assert.True(ReaderSidebarAlignment.Differs(padding, new ReaderSidebarAlignment.Insets(56, 17)));
        Assert.False(ReaderSidebarAlignment.IsUsable(double.NaN, 100, 10, 90));
        Assert.False(ReaderSidebarAlignment.IsUsable(0, 0, 10, 90));   // not laid out yet
        Assert.False(ReaderSidebarAlignment.IsUsable(0, 100, 10, 10));
        Assert.True(ReaderSidebarAlignment.IsUsable(0, 100, 10, 90));
    }

    [Fact]
    public void TheArchiveViewsFrameIsFlushWithItsColumnAndTheLargeReaderKeepsItsInset()
    {
        Assert.Equal(new Thickness(0, 0, 0, 0), ReaderSidebarAlignment.Padding(archiveView: true));
        Assert.Equal(new Thickness(16, 0, 16, 0), ReaderSidebarAlignment.Padding(archiveView: false));
        Assert.Equal(new Thickness(0, 5, 0, 7), ReaderSidebarAlignment.Padding(archiveView: true, 5, 7));
    }

    [Fact]
    public void TheArchiveViewIsWiredWithoutASectionCardAndWithHeaderRowsOverThePostsColumnOnly()
    {
        string host = Source("Desktop/Views/Archive/ArchivePage.MediaSidebar.cs");
        // The header line has a fixed height (the arithmetic needs it) and the alignment runs after every layout pass of the sidebar.
        Assert.Contains("sidebar.HeaderHeight = ReaderMediaSidebar.HeaderLineHeight;", host);
        Assert.Contains("void OnSidebarFrameLayoutUpdated(object? sender, EventArgs e) => AlignMediaSidebarFrame();", host);
        Assert.Contains("sidebar.LayoutUpdated += OnSidebarFrameLayoutUpdated;", host);
        Assert.Contains("sidebar.LayoutUpdated -= OnSidebarFrameLayoutUpdated;", host);
        Assert.Contains("sidebar.AttachedToVisualTree += SubscribeLayout;", host);
        Assert.Contains("sidebar.DetachedFromVisualTree += UnsubscribeLayout;", host);
        // No section card around the sidebar in the archive view, and the header rows no longer span the sidebar's column.
        Assert.DoesNotContain("sidebar.Classes.Add(\"workspace-section-flush\")", host);
        Assert.DoesNotContain("Grid.SetColumnSpan(child, 3)", host);
        Assert.Contains("Grid.SetColumn(headerRows, postsColumn)", host);
        Assert.Contains("ApplyMediaSidebarInsets(sidebar, archiveView);", host);
        string alignment = Source("Desktop/Views/Archive/ArchivePage.MediaSidebarAlignment.cs");
        // Measured: the box and the sidebar in the sidebar's host, not a number.
        Assert.Contains("box.TranslatePoint(default, host)", alignment);
        Assert.Contains("ReaderSidebarAlignment.InsetsFor(", alignment);
        Assert.Contains("sidebar.Padding = new Thickness(padding.Left, wanted.Top, padding.Right, wanted.Bottom);", alignment);
        // The Media page's panel uses the same line height.
        Assert.Contains("PanelHeaderLineHeight = ReaderMediaSidebar.HeaderLineHeight", Source("Desktop/Views/Media/MediaPage.PanelAlignment.cs"));
    }
}
