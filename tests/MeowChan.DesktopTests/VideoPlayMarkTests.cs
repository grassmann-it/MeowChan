using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// The play mark scales with the picture that is shown and stays centred inside it (pure sizing rule, plus the frame panel that
// applies it without a window). The GUI evidence is the media-slot, reader-preview and archive list smokes.
public sealed class VideoPlayMarkTests
{
    [Theory]
    [InlineData(76, 42.75, 20)]      // list thumbnail, landscape: 0.45 x 42.75 = 19.2, raised to the smallest circle
    [InlineData(38, 57, 20)]         // list thumbnail, portrait: 0.45 x 38 = 17.1, raised to 20
    [InlineData(240, 135, 44)]       // reader card: the largest circle
    [InlineData(100, 100, 44)]       // 45 is capped
    [InlineData(60, 60, 27)]         // in between: 45 % of the shorter side
    [InlineData(80, 200, 36)]
    [InlineData(24, 24, 20)]
    [InlineData(16, 16, 16)]         // smaller than the smallest circle: as small as the picture, never past it
    [InlineData(3.6, 180, 3.6)]      // a thin strip
    [InlineData(0, 100, 0)]
    [InlineData(100, 0, 0)]
    [InlineData(double.NaN, 100, 0)]
    [InlineData(-5, 100, 0)]
    public void TheCircleIs45PercentOfTheShorterSideBetween20And44ButNeverLargerThanThePicture(double width, double height, double expected) =>
        Assert.Equal(expected, VideoPlayMark.Diameter(new Size(width, height)), 3);

    [Fact]
    public void TheCircleNeverExceedsAPictureAndNeverShrinksWhenThePictureGrows()
    {
        double previous = 0;
        for (double side = 1; side <= 400; side += 0.5)
        {
            double diameter = VideoPlayMark.Diameter(new Size(side, side * 1.7));
            Assert.InRange(diameter, 0, side + 1e-9);
            Assert.InRange(diameter, 0, VideoPlayMark.MaximumDiameter);
            if (side >= VideoPlayMark.MinimumDiameter) Assert.InRange(diameter, VideoPlayMark.MinimumDiameter, VideoPlayMark.MaximumDiameter);
            Assert.True(diameter >= previous - 1e-9, $"the circle shrank at {side}");
            previous = diameter;
        }
    }

    [Fact]
    public void TheTriangleScalesWithTheCircleAndKeepsItsOpticalOffset()
    {
        // The 44 dip badge had a 14 x 18 dip triangle, nudged right so its centre of mass sits on the circle's centre.
        Size full = VideoPlayMark.GlyphSize(44);
        Assert.Equal(14, full.Width, 6);
        Assert.Equal(18, full.Height, 6);
        Assert.Equal(14d / 6, VideoPlayMark.GlyphOffset(44), 6);
        Size small = VideoPlayMark.GlyphSize(20);
        Assert.Equal(full.Width * 20 / 44, small.Width, 6);
        Assert.Equal(full.Height * 20 / 44, small.Height, 6);
        Assert.Equal(VideoPlayMark.GlyphOffset(44) * 20 / 44, VideoPlayMark.GlyphOffset(20), 6);
        // Inside the circle with room around it, at every size.
        for (double d = 4; d <= 44; d += 4)
            Assert.True(VideoPlayMark.GlyphSize(d).Height < d && VideoPlayMark.GlyphSize(d).Width / 2 + VideoPlayMark.GlyphOffset(d) < d / 2);
    }

    [Theory]
    [InlineData(0, 0, 76, 42.75)]
    [InlineData(19, 3.5, 38, 57)]
    [InlineData(10.2, 0, 3.6, 180)]
    [InlineData(0, 3.5625, 30, 16.875)]
    public void TheMarkIsCentredOnThePictureAndInsideIt(double x, double y, double width, double height)
    {
        Rect picture = new(x, y, width, height), mark = VideoPlayMark.Place(picture);
        Assert.Equal(picture.Center.X, mark.Center.X, 6);
        Assert.Equal(picture.Center.Y, mark.Center.Y, 6);
        Assert.Equal(mark.Width, mark.Height, 6);
        Assert.True(mark.Left >= picture.Left - 1e-9 && mark.Right <= picture.Right + 1e-9 && mark.Top >= picture.Top - 1e-9 && mark.Bottom <= picture.Bottom + 1e-9);
    }

    [Fact]
    public void TheMarkIsNeverAClickTarget_ThePictureAlwaysIs() =>
        Assert.False(new VideoPlayMarkView().IsHitTestVisible);

    [Theory]
    [InlineData("landscape in the list", 16d / 9, 76, 240, 180)]
    [InlineData("portrait in the list", 2d / 3, 76, 240, 180)]
    [InlineData("square in the list", 1d, 76, 240, 180)]
    [InlineData("landscape reader card", 16d / 9, 1000, 240, 180)]
    [InlineData("portrait reader card", 2d / 3, 1000, 360, 280)]
    [InlineData("enlarged focus card", 4d / 3, 1000, 480, 360)]
    [InlineData("tiny owner: the frame is wider than the picture", 16d / 9, 30, 240, 180)]
    [InlineData("tiny owner, panorama", 4d, 30, 240, 180)]
    [InlineData("thin strip", 1d / 50, 1000, 240, 180)]
    [InlineData("no picture yet (reserved wide-screen box)", double.NaN, 76, 240, 180)]
    public void TheMarkStaysInsideTheShownPictureAndCentredOnIt(string label, double aspect, double available, double maxWidth, double maxHeight)
    {
        Border image = new();
        VideoPlayMarkView mark = new();
        MediaFramePanel frame = new()
        {
            Aspect = double.IsNaN(aspect) ? null : aspect, MaximumWidthOverride = maxWidth, MaximumHeightOverride = maxHeight,
            Children = { image, mark }
        };
        frame.Measure(new Size(available, double.PositiveInfinity));
        frame.Arrange(new Rect(default, frame.DesiredSize));
        // The picture is the letterboxed rectangle inside the frame (the frame itself only differs for a very small owner or a thin strip).
        Rect picture = MediaSlotGeometry.Letterbox(frame.DesiredSize, frame.Aspect);
        // Whole device pixels: compare within a pixel.
        const double Tolerance = 1;
        Assert.True(mark.Bounds.Left >= picture.Left - Tolerance && mark.Bounds.Right <= picture.Right + Tolerance
            && mark.Bounds.Top >= picture.Top - Tolerance && mark.Bounds.Bottom <= picture.Bottom + Tolerance,
            $"{label}: the mark {mark.Bounds} leaves the picture {picture}.");
        Assert.True(Math.Abs(mark.Bounds.Center.X - picture.Center.X) <= Tolerance && Math.Abs(mark.Bounds.Center.Y - picture.Center.Y) <= Tolerance,
            $"{label}: the mark {mark.Bounds.Center} is not centred on the picture {picture.Center}.");
        Assert.True(Math.Abs(mark.Bounds.Width - VideoPlayMark.Diameter(picture.Size)) <= Tolerance,
            $"{label}: the mark is {mark.Bounds.Width} wide, the rule gives {VideoPlayMark.Diameter(picture.Size)}.");
        Assert.True(mark.Bounds.Width <= VideoPlayMark.MaximumDiameter + Tolerance);
    }

    [Fact]
    public void ThePictureStaysWholeInTheFrameAndItsOtherChildrenKeepTheFrame()
    {
        Border image = new();
        MediaFramePanel frame = new() { Aspect = 2d / 3, Children = { image, new VideoPlayMarkView() } };
        frame.Measure(new Size(76, double.PositiveInfinity));
        frame.Arrange(new Rect(default, frame.DesiredSize));
        Assert.Equal(new Rect(default, frame.DesiredSize), image.Bounds);
    }
}
