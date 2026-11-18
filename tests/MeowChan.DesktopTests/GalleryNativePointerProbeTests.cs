using Avalonia;
using MeowChan.DesktopSmoke;

namespace MeowChan.DesktopTests;

public sealed class GalleryNativePointerProbeTests
{
    [Theory]
    [InlineData(20, 1)]
    [InlineData(32, 44)]
    [InlineData(400, 60)]
    public void MotionPointsAreInteriorAndCannotAcknowledgeEachOther(double width, double height)
    {
        var points = GalleryNativePointerProbe.EntryPoints(new Size(width, height));
        Assert.Equal(2, points.Length);
        Assert.All(points, point => Assert.True(new Rect(0, 0, width, height).Contains(point)));
        Assert.False(GalleryNativePointerProbe.IsAt(points[0], points[1]));
        Assert.False(GalleryNativePointerProbe.IsAt(points[1], points[0]));
        Assert.Equal(new Point(width / 2, height / 2), points[1]);
        // Even the closest accepted edges of the two tolerance boxes cannot overlap.
        Assert.True(points[1].X - points[0].X > 4);
    }

    [Theory]
    [InlineData(0, 44)]
    [InlineData(19, 44)]
    [InlineData(100, 0)]
    [InlineData(double.NaN, 44)]
    [InlineData(double.PositiveInfinity, 44)]
    public void UnrealizedOrAmbiguousGeometryIsRejected(double width, double height) =>
        Assert.Throws<ArgumentException>(() => GalleryNativePointerProbe.EntryPoints(new Size(width, height)));

    [Fact]
    public void DeliveryMustMatchBothCoordinates()
    {
        Point expected = new(150, 80);
        Assert.True(GalleryNativePointerProbe.IsAt(new Point(152, 78), expected));
        Assert.False(GalleryNativePointerProbe.IsAt(new Point(153, 80), expected));
        Assert.False(GalleryNativePointerProbe.IsAt(new Point(150, 83), expected));
    }
}
