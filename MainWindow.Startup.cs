using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    private bool _minimumSizeCorrectionQueued;

    private Size WorkspaceMinimumSize => _config.WorkspaceMode == "Barebones"
        ? new Size(640, 480) : new Size(760, 500);

    private Size FitWorkspaceMinimum(Screen screen) => WindowStartupGeometry.Fit(
        ClientSize, FrameSize, WorkspaceMinimumSize, screen.WorkingArea, RenderScaling,
        Position, WindowStartupGeometry.NormalFrameInsetPixels).Minimum;

    private void ApplyWorkspaceMinimum(Size minimum)
    {
        if (MinWidth != minimum.Width) MinWidth = minimum.Width;
        if (MinHeight != minimum.Height) MinHeight = minimum.Height;
    }

    private void RefreshWorkspaceMinimumAfterResize()
    {
        if (_isClosing || WindowState != WindowState.Normal || _fullscreenOrigin != null
            || _restoringFullscreen || _fullscreenMaximizeRestoreClient != null
            || _updatingWorkspacePreferences || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        Screen? screen = Screens.ScreenFromWindow(this);
        if (screen == null) return;
        ApplyWorkspaceMinimum(FitWorkspaceMinimum(screen));

        // The native minimum is the primary resize constraint. Correct a platform
        // that reports an undersized normal client after an interactive resize.
        if (_minimumSizeCorrectionQueued || (ClientSize.Width >= MinWidth - 1 && ClientSize.Height >= MinHeight - 1)) return;
        _minimumSizeCorrectionQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _minimumSizeCorrectionQueued = false;
            if (_isClosing || WindowState != WindowState.Normal || _fullscreenOrigin != null
                || _restoringFullscreen || _fullscreenMaximizeRestoreClient != null
                || _updatingWorkspacePreferences) return;
            Screen? currentScreen = Screens.ScreenFromWindow(this);
            if (currentScreen == null) return;
            ApplyWorkspaceMinimum(FitWorkspaceMinimum(currentScreen));
            if (ClientSize.Width < MinWidth - 1 || ClientSize.Height < MinHeight - 1)
                RequestNormalWindowSize(new Size(System.Math.Max(ClientSize.Width, MinWidth),
                    System.Math.Max(ClientSize.Height, MinHeight)));
        }, DispatcherPriority.Loaded);
    }

    private void BeginStartupWindowFit()
    {
        // X11 can publish the native decorations only after Opened/Loaded.
        // Observe that initial measurement once; later resizes retain the mode minimum.
        PropertyChanged += OnStartupFrameMeasured;
        Closed += StopWaitingForStartupFrame;
        if (HasMeasuredStartupFrame()) StopWaitingForStartupFrame(this, System.EventArgs.Empty);
        FitStartupWindowToWorkingArea();
    }

    private bool HasMeasuredStartupFrame() => FrameSize is Size frame &&
        (frame.Width > ClientSize.Width || frame.Height > ClientSize.Height);

    private void OnStartupFrameMeasured(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != FrameSizeProperty || !HasMeasuredStartupFrame()) return;
        StopWaitingForStartupFrame(this, System.EventArgs.Empty);
        Dispatcher.UIThread.Post(FitStartupWindowToWorkingArea, DispatcherPriority.Loaded);
    }

    private void StopWaitingForStartupFrame(object? sender, System.EventArgs e)
    {
        PropertyChanged -= OnStartupFrameMeasured;
        Closed -= StopWaitingForStartupFrame;
    }

    private void FitStartupWindowToWorkingArea()
    {
        if (_isClosing || WindowState != WindowState.Normal || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        Screen? screen = Screens.ScreenFromWindow(this);
        if (screen == null) return;
        WindowStartupFit fit = WindowStartupGeometry.Fit(ClientSize, FrameSize, WorkspaceMinimumSize,
            screen.WorkingArea, RenderScaling, Position, WindowStartupGeometry.NormalFrameInsetPixels);
        ApplyWorkspaceMinimum(fit.Minimum);
        RequestNormalWindowSize(fit.Client);
        Position = fit.Position;
    }

    private void SetWorkspaceWindowSize(double width, double height)
    {
        // Switching away from Barebones can restore a larger remembered size.
        // Fit that explicit mode change, including native borders, to the current
        // monitor just as at startup.
        Screen? screen = Screens.ScreenFromWindow(this);
        if (screen == null)
        {
            RequestNormalWindowSize(new Size(width, height));
            return;
        }
        Size desired = new(width, height);
        Size? desiredFrame = FrameSize is Size frame
            ? new Size(width + System.Math.Max(0, frame.Width - ClientSize.Width),
                height + System.Math.Max(0, frame.Height - ClientSize.Height)) : null;
        WindowStartupFit fit = WindowStartupGeometry.Fit(desired, desiredFrame, WorkspaceMinimumSize,
            screen.WorkingArea, RenderScaling, Position, WindowStartupGeometry.NormalFrameInsetPixels);
        ApplyWorkspaceMinimum(fit.Minimum);
        RequestNormalWindowSize(fit.Client);
        Position = fit.Position;
    }
}
