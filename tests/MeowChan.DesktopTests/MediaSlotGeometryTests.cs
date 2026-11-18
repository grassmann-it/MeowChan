using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// The preview frame is the drawn picture, and what sits on it sits on the picture (pure geometry, plus the two panels that apply it
// without a window). The GUI evidence is the media-slot smoke.
public sealed class MediaSlotGeometryTests
{
    [Theory]
    [InlineData(400, 600, 240, 180, 120, 180)]    // portrait 2:3: the height cap makes the frame narrow, not a wide box
    [InlineData(640, 360, 240, 180, 240, 135)]    // 16:9 fills the width
    [InlineData(800, 600, 240, 180, 240, 180)]    // 4:3 fills both
    [InlineData(800, 200, 240, 180, 240, 60)]     // panorama: short frame
    [InlineData(400, 600, 360, 280, 186.6667, 280)] // the large reader keeps its own caps
    public void TheFrameHugsThePictureWithinTheCaps(int pixelWidth, int pixelHeight, double maxWidth, double maxHeight, double width, double height)
    {
        Size frame = MediaSlotGeometry.Fit(MediaSlotGeometry.AspectOf(pixelWidth, pixelHeight), 1000, maxWidth, maxHeight);
        Assert.Equal(width, frame.Width, 3);
        Assert.Equal(height, frame.Height, 3);
    }

    [Fact]
    public void WithoutAPictureTheWideScreenBoxIsReservedAtTheAvailableWidth()
    {
        Assert.Equal(new Size(240, 135), MediaSlotGeometry.Fit(null, 1000, 240, 180));
        Assert.Equal(new Size(200, 112.5), MediaSlotGeometry.Fit(null, 200, 240, 180));
        Assert.Equal(new Size(240, 135), MediaSlotGeometry.Fit(null, double.PositiveInfinity, 240, 180));
        Assert.Equal(new Size(240, 135), MediaSlotGeometry.Fit(double.NaN, 1000, 240, 180));
        Assert.Null(MediaSlotGeometry.AspectOf(0, 100));
        Assert.Null(MediaSlotGeometry.AspectOf(100, 0));
    }

    [Fact]
    public void ANarrowOwnerScalesTheHeightCapWithItsWidth()
    {
        // The thread list gives 76 dip: a portrait thumbnail stays small there (cap 180 * 76/240 = 57), landscape keeps 16:9.
        Size portrait = MediaSlotGeometry.Fit(2d / 3, 76, 240, 180);
        Assert.Equal(57, portrait.Height, 3);
        Assert.Equal(38, portrait.Width, 3);
        Assert.Equal(new Size(76, 42.75), MediaSlotGeometry.Fit(16d / 9, 76, 240, 180));
    }

    [Fact]
    public void AThinStripStaysAUsableFrame()
    {
        Size frame = MediaSlotGeometry.Fit(1d / 50, 1000, 240, 180);
        Assert.Equal(MediaSlotGeometry.MinimumExtent, frame.Width);
        Assert.Equal(180, frame.Height);
    }

    [Fact]
    public void TheFileLineKeepsAModestMinimumButNeverMoreThanTheOwnerGives()
    {
        Assert.Equal(MediaSlotGeometry.MinimumBlockWidth, MediaSlotGeometry.BlockWidth(90, 600));
        Assert.Equal(240, MediaSlotGeometry.BlockWidth(240, 600));
        Assert.Equal(76, MediaSlotGeometry.BlockWidth(38, 76));
        Assert.Equal(MediaSlotGeometry.MinimumBlockWidth, MediaSlotGeometry.BlockWidth(90, double.PositiveInfinity));
    }

    [Theory]
    [InlineData(170, 125, 2d / 3, 43.3333, 0, 83.3333, 125)]   // portrait: centred, full height
    [InlineData(170, 125, 16d / 9, 0, 14.6875, 170, 95.625)]    // landscape: centred, full width
    [InlineData(170, 125, 4d, 0, 41.25, 170, 42.5)]             // wide strip
    [InlineData(170, 125, 170d / 125, 0, 0, 170, 125)]          // same aspect: the cell
    public void ThePictureIsLetterboxedAndCentredInAUniformCell(double cellWidth, double cellHeight, double aspect, double x, double y, double width, double height)
    {
        Rect picture = MediaSlotGeometry.Letterbox(new Size(cellWidth, cellHeight), aspect);
        Assert.Equal(x, picture.X, 3);
        Assert.Equal(y, picture.Y, 3);
        Assert.Equal(width, picture.Width, 3);
        Assert.Equal(height, picture.Height, 3);
    }

    [Fact]
    public void WithoutAPictureOrCellTheMarksUseTheWholeCell()
    {
        Assert.Equal(new Rect(0, 0, 170, 125), MediaSlotGeometry.Letterbox(new Size(170, 125), null));
        Assert.Equal(new Rect(0, 0, 170, 125), MediaSlotGeometry.Letterbox(new Size(170, 125), 0));
        Assert.Equal(new Rect(0, 0, 0, 0), MediaSlotGeometry.Letterbox(default, 1.5));
    }

    [Fact]
    public void MarksOfAFlatPictureGetABandSoBadgeAndStarDoNotStack()
    {
        // A 4:1 strip is 42.5 dip high in a 170 x 125 cell: its marks are arranged on a 64 dip band centred on it, never past the cell.
        Rect strip = MediaSlotGeometry.Marks(new Size(170, 125), 4);
        Assert.Equal(new Rect(0, 30.5, 170, 64), strip);
        Rect portrait = MediaSlotGeometry.Marks(new Size(170, 125), 2d / 3);
        Assert.Equal(MediaSlotGeometry.Letterbox(new Size(170, 125), 2d / 3), portrait);
        Assert.Equal(new Rect(0, 0, 170, 125), MediaSlotGeometry.Marks(new Size(170, 125), null));
        Assert.True(MediaSlotGeometry.Marks(new Size(50, 40), 10).Width <= 50);
    }

    [Fact]
    public void TheFramePanelArrangesPictureAndOverlaysOnTheSameRectangle()
    {
        Border picture = new(), play = new() { Width = 44, Height = 44, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        MediaFramePanel frame = new() { Aspect = 2d / 3, Children = { picture, play } };
        frame.Measure(new Size(500, double.PositiveInfinity));
        frame.Arrange(new Rect(default, frame.DesiredSize));
        Assert.Equal(new Size(120, 180), frame.DesiredSize);
        Assert.Equal(new Rect(0, 0, 120, 180), picture.Bounds);
        // The play button is centred on the picture, not on a wider slot.
        Assert.Equal(60, play.Bounds.Center.X, 3);
        Assert.Equal(90, play.Bounds.Center.Y, 3);
    }

    [Fact]
    public void TheLetterboxPanelPutsEveryChildOnThePictureNotTheCellEdge()
    {
        Border picture = new(), badge = new() { Width = 20, Height = 10, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        LetterboxPanel panel = new() { Width = 170, Height = 125, Children = { picture, badge } };
        panel.Measure(new Size(170, 125)); panel.Arrange(new Rect(0, 0, 170, 125));
        Assert.Equal(150, badge.Bounds.X, 3); // no picture yet: the marks use the whole cell

        panel.Aspect = 2d / 3;
        panel.Measure(new Size(170, 125)); panel.Arrange(new Rect(0, 0, 170, 125));
        // Layout rounding is on (whole device pixels): compare within a pixel.
        Assert.InRange(picture.Bounds.X, 42.5, 44);
        Assert.InRange(picture.Bounds.Width, 82.5, 84);
        // The badge is at the picture's top-right corner: 43.33 + 83.33 = 126.67, not the cell edge at 170.
        Assert.InRange(badge.Bounds.Right, 126, 127.5);
        Assert.Equal(0, badge.Bounds.Top, 3);
    }
}
