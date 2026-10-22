using Avalonia;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class X11WindowMinimumTests
{
    [Theory]
    [InlineData(1, 9, 818, 658, 658, 498)]
    [InlineData(1.5, 9.333333333333334, 1228, 988, 988, 748)]
    [InlineData(2, 9, 1636, 1316, 1316, 996)]
    public void NativeMinimumIncludesMeasuredClientDecorationWithoutRaisingContentMinimum(
        double scale, double inset, int nativeWidth, int nativeHeight, int expectedWidth, int expectedHeight)
    {
        Assert.Equal(new PixelSize(expectedWidth, expectedHeight), X11WindowMinimum.NativeMinimum(
            new Size(640, 480), new Size(800, 640), new Rect(inset, inset, 800, 640),
            new PixelSize(nativeWidth, nativeHeight), scale));
    }

    [Fact]
    public void AWorkingAreaReducedMinimumKeepsItsIndependentAxis()
    {
        Assert.Equal(new PixelSize(618, 518), X11WindowMinimum.NativeMinimum(
            new Size(600, 500), new Size(600, 640), new Rect(9, 9, 600, 640), new PixelSize(618, 658), 1));
    }

    [Fact]
    public void PendingNativeResizeCannotBeMistakenForDecoration()
    {
        Assert.Null(X11WindowMinimum.NativeMinimum(new Size(640, 480), new Size(800, 640),
            new Rect(9, 9, 800, 640), new PixelSize(1018, 858), 1));
    }

    [Fact]
    public void ServerDecoratedAndUndecoratedContentNeedsNoCompensation()
    {
        Assert.Null(X11WindowMinimum.NativeMinimum(new Size(640, 480), new Size(800, 640),
            new Rect(0, 0, 800, 640), new PixelSize(800, 640), 1));
    }
}
