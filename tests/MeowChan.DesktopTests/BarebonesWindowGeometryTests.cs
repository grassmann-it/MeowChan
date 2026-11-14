using Avalonia.Controls;

namespace MeowChan.DesktopTests;

// Tests the state actually used by MainWindow; no native window, profile or fabricated/uninitialized shell.
public sealed class BarebonesWindowGeometryTests
{
    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    public void DisabledShrinkNeitherCapturesEntryNorRestoresOldGeometry(WindowState state)
    {
        var geometry = new BarebonesWindowGeometry();
        Assert.False(geometry.Capture(false, true, 1450, 920, state));
        Assert.Null(geometry.TakeRestore(false));
        Assert.Null(geometry.TakeRestore(true));
    }

    [Fact]
    public void EnabledShrinkRestoresExactlyOnceAndKeepsThePriorWindowState()
    {
        var geometry = new BarebonesWindowGeometry();
        Assert.True(geometry.Capture(true, true, 1450, 920, WindowState.Maximized));
        var captured = geometry.TakeRestore(true);
        Assert.NotNull(captured);
        Assert.Equal((1450d, 920d, WindowState.Maximized), captured!.Value);
        Assert.Null(geometry.TakeRestore(true));
    }

    [Fact]
    public void EnabledInitialBarebonesDoesNotResizeAndRetainsTheHistoricalExitDefault()
    {
        var geometry = new BarebonesWindowGeometry();
        Assert.False(geometry.Capture(true, false, 1210, 710, WindowState.Normal));
        Assert.Equal((1380d, 850d, WindowState.Normal), geometry.TakeRestore(true)!.Value);
    }

    [Fact]
    public void DisabledInitialBarebonesAlsoOwnsNoRestoreWhenEnabledLater()
    {
        var geometry = new BarebonesWindowGeometry();
        Assert.False(geometry.Capture(false, false, 1210, 710, WindowState.Maximized));
        Assert.Null(geometry.TakeRestore(true));
    }

    [Fact]
    public void DisablingDuringBarebonesDiscardsCaptureEvenWhenReenabledBeforeExit()
    {
        var geometry = new BarebonesWindowGeometry();
        Assert.True(geometry.Capture(true, true, 1450, 920, WindowState.Maximized));
        geometry.DiscardIfDisabled(false);
        geometry.DiscardIfDisabled(true);
        Assert.Null(geometry.TakeRestore(true));
    }

    [Fact]
    public void DisablingAtExitDoesNotLeaveAStaleCaptureForTheNextExit()
    {
        var geometry = new BarebonesWindowGeometry();
        Assert.True(geometry.Capture(true, true, 1450, 920, WindowState.Maximized));
        Assert.Null(geometry.TakeRestore(false));
        Assert.Null(geometry.TakeRestore(true));
        Assert.True(geometry.Capture(true, true, 1130, 650, WindowState.Normal));
        Assert.Equal((1130d, 650d, WindowState.Normal), geometry.TakeRestore(true)!.Value);
    }

    [Fact]
    public void TheExistingFallbackForNonFiniteWindowDimensionsIsRetained()
    {
        var geometry = new BarebonesWindowGeometry();
        Assert.True(geometry.Capture(true, true, double.NaN, double.PositiveInfinity, WindowState.Normal));
        Assert.Equal((1380d, 850d, WindowState.Normal), geometry.TakeRestore(true)!.Value);
    }
}
