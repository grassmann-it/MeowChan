using Avalonia;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

// The play mark scales with the picture that is shown and stays centred inside it (pure sizing rule, plus the frame panel that
// applies it without a window). Archive surfaces draw the mark with the shared badge view (MB1b): the same rule, checked on the badge plan
// that the frame panel and the tile panel hand their picture to. The GUI evidence is the media-slot, reader-preview and archive list smokes.
[Collection("Localization")] // the badge limits (hide below) are process-wide; the other badge tests change them
public sealed class VideoPlayMarkTests
{
    public VideoPlayMarkTests()
    {
        MediaBadgeLimits defaults = MediaBadgeLimits.Default;
        MediaBadgeLimits.Configure(defaults.CollapseBelow, defaults.HideBelow, defaults.MaxPerCorner);
    }

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
    public void TheMarkIsADarkDiscInBothThemesBecauseItLiesOnArbitraryPictures()
    {
        // Palette item 4: no theme token (a light disc on a light picture has too little contrast); the same dark disc and white ring always.
        VideoPlayMarkView mark = new();
        Avalonia.Media.SolidColorBrush disc = Assert.IsType<Avalonia.Media.SolidColorBrush>(mark.Background);
        Assert.True(disc.Color.R < 40 && disc.Color.G < 40 && disc.Color.B < 40 && disc.Color.A >= 180);
        Avalonia.Media.SolidColorBrush ring = Assert.IsType<Avalonia.Media.SolidColorBrush>(mark.BorderBrush);
        Assert.True(ring.Color.R > 240 && ring.Color.G > 240 && ring.Color.B > 240);
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
    // ---- MB1b: the archive's attachment frame draws the mark with the shared badge view ----

    private static MediaBadgeState Video => new(Kind: MediaBadgeKind.Video);

    private static PlacedBadge? PlayMarkOf(MediaBadgeView view) =>
        view.Plan.Badges.Where(placed => placed.Badge.Id == MediaBadgeId.PlayMark).Select(placed => (PlacedBadge?)placed).FirstOrDefault();

    [Theory]
    [InlineData("landscape in the list", 16d / 9, 76, 240, 180)]
    [InlineData("portrait in the list", 2d / 3, 76, 240, 180)]
    [InlineData("square in the list", 1d, 76, 240, 180)]
    [InlineData("landscape reader card", 16d / 9, 1000, 240, 180)]
    [InlineData("portrait reader card", 2d / 3, 1000, 360, 280)]
    [InlineData("enlarged focus card", 4d / 3, 1000, 480, 360)]
    [InlineData("no picture yet (reserved wide-screen box)", double.NaN, 76, 240, 180)]
    public void TheBadgeViewLiesOnTheShownPictureAndItsPlayMarkIsCentredInsideItAtTheScaledSize(string label, double aspect, double available, double maxWidth, double maxHeight)
    {
        Border image = new();
        MediaBadgeView badges = new() { Options = MediaBadgeOptions.Archive };
        badges.Apply(Video, new Size(1, 1));
        MediaFramePanel frame = new()
        {
            Aspect = double.IsNaN(aspect) ? null : aspect, MaximumWidthOverride = maxWidth, MaximumHeightOverride = maxHeight,
            Children = { image, badges }
        };
        frame.Measure(new Size(available, double.PositiveInfinity));
        frame.Arrange(new Rect(default, frame.DesiredSize));
        Rect picture = MediaSlotGeometry.Letterbox(frame.DesiredSize, frame.Aspect);
        const double Tolerance = 1;
        // The badge view is exactly the picture, the picture button (here: the first child) keeps the whole frame.
        Assert.True(Math.Abs(badges.Bounds.X - picture.X) <= Tolerance && Math.Abs(badges.Bounds.Y - picture.Y) <= Tolerance
            && Math.Abs(badges.Bounds.Width - picture.Width) <= Tolerance && Math.Abs(badges.Bounds.Height - picture.Height) <= Tolerance,
            $"{label}: the badge view {badges.Bounds} is not on the picture {picture}.");
        Assert.Equal(new Rect(default, frame.DesiredSize), image.Bounds);
        Assert.False(badges.IsHitTestVisible);
        PlacedBadge mark = PlayMarkOf(badges) ?? throw new Xunit.Sdk.XunitException($"{label}: no play mark.");
        // The plan is in the view's own coordinates: the mark is centred on the picture, inside it, 45 percent of its shorter side (20 to 44 dip).
        Assert.True(Math.Abs(mark.Bounds.Center.X - picture.Width / 2) <= Tolerance && Math.Abs(mark.Bounds.Center.Y - picture.Height / 2) <= Tolerance,
            $"{label}: the mark {mark.Bounds.Center} is not centred on the picture {picture.Size}.");
        Assert.True(mark.Bounds.Left >= -Tolerance && mark.Bounds.Top >= -Tolerance && mark.Bounds.Right <= picture.Width + Tolerance && mark.Bounds.Bottom <= picture.Height + Tolerance,
            $"{label}: the mark {mark.Bounds} leaves the picture {picture.Size}.");
        Assert.True(Math.Abs(mark.Bounds.Width - VideoPlayMark.Diameter(picture.Size)) <= Tolerance,
            $"{label}: the mark is {mark.Bounds.Width} wide, the rule gives {VideoPlayMark.Diameter(picture.Size)}.");
        Assert.InRange(mark.Bounds.Width, VideoPlayMark.MinimumDiameter - Tolerance, VideoPlayMark.MaximumDiameter + Tolerance);
    }

    [Theory]
    [InlineData("tiny owner: the frame is wider than the picture", 16d / 9, 30, 240, 180)]
    [InlineData("tiny owner, panorama", 4d, 30, 240, 180)]
    [InlineData("thin strip", 1d / 50, 1000, 240, 180)]
    public void APictureBelowTheHideLimitCarriesNoBadgeAtAllButStaysWhole(string label, double aspect, double available, double maxWidth, double maxHeight)
    {
        Border image = new();
        MediaBadgeView badges = new() { Options = MediaBadgeOptions.Archive };
        badges.Apply(Video with { Favorite = true }, new Size(1, 1));
        MediaFramePanel frame = new() { Aspect = aspect, MaximumWidthOverride = maxWidth, MaximumHeightOverride = maxHeight, Children = { image, badges } };
        frame.Measure(new Size(available, double.PositiveInfinity));
        frame.Arrange(new Rect(default, frame.DesiredSize));
        Size picture = MediaSlotGeometry.Letterbox(frame.DesiredSize, aspect).Size;
        Assert.True(Math.Min(picture.Width, picture.Height) < MediaBadgeLimits.Current.HideBelow, label);
        Assert.Empty(badges.Plan.Badges);
        Assert.False(badges.IsVisible);
        // The picture is still the frame's whole click target (a thin strip is a frame of at least 24 dip).
        Assert.Equal(new Rect(default, frame.DesiredSize), image.Bounds);
        Assert.True(image.Bounds.Width >= Math.Min(MediaSlotGeometry.MinimumExtent, available) - 1e-9);
    }

    [Fact]
    public void TheBadgeViewFollowsTheFrameWhenThePictureChangesItsAspect()
    {
        MediaBadgeView badges = new() { Options = MediaBadgeOptions.Archive };
        badges.Apply(Video, new Size(1, 1));
        MediaFramePanel frame = new() { Aspect = 16d / 9, Children = { new Border(), badges } };
        frame.Measure(new Size(1000, double.PositiveInfinity)); frame.Arrange(new Rect(default, frame.DesiredSize));
        Size first = badges.Bounds.Size;
        frame.Aspect = 2d / 3;
        frame.InvalidateMeasure();
        frame.Measure(new Size(1000, double.PositiveInfinity)); frame.Arrange(new Rect(default, frame.DesiredSize));
        Assert.NotEqual(first, badges.Bounds.Size);
        Assert.Equal(MediaSlotGeometry.Letterbox(frame.DesiredSize, 2d / 3).Size.Width, badges.Bounds.Width, 1);
        Assert.NotNull(PlayMarkOf(badges));
    }

    [Fact]
    public void TheTilePanelGivesTheBadgeViewThePictureNotTheMarksBand()
    {
        // A cell of 170 x 125 with a wide picture (letterboxed to 170 x 42.5): the badges lie on the picture, the other marks on the 64 dip band.
        MediaBadgeView badges = new() { Options = MediaBadgeOptions.Archive };
        badges.Apply(new MediaBadgeState(Favorite: true, Kind: MediaBadgeKind.Gif), new Size(1, 1));
        Border other = new();
        LetterboxPanel panel = new() { Aspect = 4, Children = { new Border(), other, badges } };
        panel.Measure(new Size(170, 125)); panel.Arrange(new Rect(0, 0, 170, 125));
        Rect picture = MediaSlotGeometry.Letterbox(new Size(170, 125), 4), band = MediaSlotGeometry.Marks(new Size(170, 125), 4);
        // Whole device pixels (layout rounding): compare within a pixel.
        void Near(Rect expected, Rect actual) => Assert.True(Math.Abs(expected.X - actual.X) <= 1 && Math.Abs(expected.Y - actual.Y) <= 1
            && Math.Abs(expected.Width - actual.Width) <= 1 && Math.Abs(expected.Height - actual.Height) <= 1, $"{actual} is not {expected}.");
        Near(picture, badges.Bounds);
        Near(band, other.Bounds);
        Assert.NotEqual(picture.Height, band.Height);
        Assert.Equal(picture, panel.PictureBounds);
        Assert.Equal(band, panel.MarksBounds);
        // Picture shorter than the collapse limit: dots; the favourite stays a dot in the top left corner.
        Assert.Equal(MediaBadgeMode.Dots, badges.Plan.Mode);
    }
}
