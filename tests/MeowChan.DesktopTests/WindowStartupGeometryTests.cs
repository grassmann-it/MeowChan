using Avalonia;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

public sealed class WindowStartupGeometryTests
{
    [Theory]
    [InlineData(1, 1024, 728)]
    [InlineData(2, 2048, 1456)]
    public void WindowsRunnerFrameFitsWorkingAreaIncludingNativeDecorations(double scale, int width, int height)
    {
        WindowStartupFit fit = WindowStartupGeometry.Fit(new Size(1028, 749), new Size(1044, 788),
            new Size(760, 500), new PixelRect(0, 0, width, height), scale, new PixelPoint(78, 78));
        Assert.Equal(new Size(1008, 689), fit.Client);
        Assert.Equal(new Size(760, 500), fit.Minimum);
        Assert.Equal(new PixelPoint(0, 0), fit.Position);
        Assert.Equal(width, (fit.Client.Width + 16) * scale);
        Assert.Equal(height, (fit.Client.Height + 39) * scale);
    }

    [Fact]
    public void LargeDesktopKeepsRequestedSizeAndExistingPosition()
    {
        WindowStartupFit fit = WindowStartupGeometry.Fit(new Size(1380, 850), new Size(1396, 889),
            new Size(760, 500), new PixelRect(0, 0, 1920, 1040), 1, new PixelPoint(78, 78));
        Assert.Equal(new Size(1380, 850), fit.Client);
        Assert.Equal(new PixelPoint(78, 78), fit.Position);
    }

    [Fact]
    public void SmallScaledDesktopCapsMinimumAndHonorsNegativeMonitorOrigin()
    {
        PixelRect area = new(-1000, -800, 1000, 700);
        WindowStartupFit fit = WindowStartupGeometry.Fit(new Size(760, 500), new Size(776, 539),
            new Size(760, 500), area, 1.5, new PixelPoint(-50, -50));
        Assert.Equal(new Size(650, 427), fit.Client);
        Assert.Equal(fit.Client, fit.Minimum);
        Assert.True(fit.Position.X >= area.X && fit.Position.Y >= area.Y);
        Assert.True(fit.Position.X + Math.Ceiling((fit.Client.Width + 16) * 1.5) <= area.Right);
        Assert.True(fit.Position.Y + Math.Ceiling((fit.Client.Height + 39) * 1.5) <= area.Bottom);
    }

    [Fact]
    public void LateFrameMetricsCanTightenAnInitiallyUndecoratedFit()
    {
        PixelRect area = new(0, 0, 1024, 728);
        WindowStartupFit first = WindowStartupGeometry.Fit(new Size(1028, 749), null,
            new Size(760, 500), area, 1, new PixelPoint(78, 78));
        WindowStartupFit final = WindowStartupGeometry.Fit(first.Client,
            new Size(first.Client.Width + 16, first.Client.Height + 39), first.Minimum, area, 1, first.Position);
        Assert.Equal(new Size(1008, 689), final.Client);
        Assert.Equal(new PixelPoint(0, 0), final.Position);
    }
}
