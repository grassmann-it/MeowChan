using Avalonia;
using Avalonia.Controls;

namespace MeowChan.DesktopTests;

// Pure state used by MainWindow's tray/app.show restore, Barebones transitions and fullscreen button.
// No native window: compositor behaviour still needs GUI validation.
public sealed class WindowStateRestoreTests
{
    [Theory]
    [InlineData(WindowState.Normal)]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    public void RestoreFromMinimizedReturnsToThePriorState(WindowState prior)
    {
        var restore = new MainWindow.MinimizeRestoreState();
        restore.StateChanged(WindowState.Normal, prior);
        restore.StateChanged(prior, WindowState.Minimized);
        Assert.Equal(prior, restore.RestoreTarget);
    }

    [Fact]
    public void RepeatedMinimizedReportsKeepTheStateBeforeTheFirstOne()
    {
        var restore = new MainWindow.MinimizeRestoreState();
        restore.StateChanged(WindowState.Maximized, WindowState.Minimized);
        // X11 can repeat the same state while a request awaits the window manager.
        restore.StateChanged(WindowState.Minimized, WindowState.Minimized);
        Assert.Equal(WindowState.Maximized, restore.RestoreTarget);
    }

    [Fact]
    public void ALaterMinimizeFromNormalReplacesAnEarlierMaximizedOrigin()
    {
        var restore = new MainWindow.MinimizeRestoreState();
        restore.StateChanged(WindowState.Maximized, WindowState.Minimized);
        restore.StateChanged(WindowState.Minimized, WindowState.Maximized);
        restore.StateChanged(WindowState.Maximized, WindowState.Normal);
        restore.StateChanged(WindowState.Normal, WindowState.Minimized);
        Assert.Equal(WindowState.Normal, restore.RestoreTarget);
    }

    [Fact]
    public void NeverMinimizedDefaultsToNormal() =>
        Assert.Equal(WindowState.Normal, new MainWindow.MinimizeRestoreState().RestoreTarget);

    [Theory]
    [InlineData(WindowState.Maximized)]
    [InlineData(WindowState.FullScreen)]
    public void BarebonesEntryOutsideNormalRestoresTheLastNormalSizeUnderThatState(WindowState state)
    {
        var geometry = new BarebonesWindowGeometry();
        // Width/Height report the maximized/fullscreen client; the remembered normal client is 1210x710.
        Assert.True(geometry.Capture(true, true, 2560, 1400, state, new Size(1210, 710)));
        Assert.Equal((1210d, 710d, state), geometry.TakeRestore(true)!.Value);
    }

    [Fact]
    public void BarebonesEntryFromNormalKeepsTheCurrentSize()
    {
        var geometry = new BarebonesWindowGeometry();
        Assert.True(geometry.Capture(true, true, 1450, 920, WindowState.Normal, new Size(1210, 710)));
        Assert.Equal((1450d, 920d, WindowState.Normal), geometry.TakeRestore(true)!.Value);
    }

    [Fact]
    public void BarebonesIgnoresAnUnusableNormalSnapshot()
    {
        var geometry = new BarebonesWindowGeometry();
        Assert.True(geometry.Capture(true, true, 2560, 1400, WindowState.Maximized, new Size(0, double.NaN)));
        Assert.Equal((2560d, 1400d, WindowState.Maximized), geometry.TakeRestore(true)!.Value);
    }

    [Theory]
    [InlineData(WindowState.Normal, WindowState.FullScreen, true)]
    [InlineData(WindowState.FullScreen, WindowState.Normal, true)]
    [InlineData(WindowState.FullScreen, WindowState.Maximized, true)]
    [InlineData(WindowState.Maximized, WindowState.FullScreen, true)]
    [InlineData(WindowState.FullScreen, WindowState.Minimized, true)]
    [InlineData(WindowState.Normal, WindowState.Maximized, false)]
    [InlineData(WindowState.Maximized, WindowState.Minimized, false)]
    [InlineData(WindowState.FullScreen, WindowState.FullScreen, false)]
    public void ExternalFullscreenTransitionsRefreshTheFullscreenButton(WindowState previous, WindowState current, bool refresh) =>
        Assert.Equal(refresh, MainWindow.FullscreenPresentationChanged(previous, current));
}
