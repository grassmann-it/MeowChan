using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    private bool _minimumSizeCorrectionQueued;
    private Size? _startupWindowClient;
    private bool _applyingStartupWindowSize;
    private bool _startupSizeCorrectionQueued;
    private Size? _startupFrameClient;

    private void InitializeStartupWindowSize()
    {
        // Avoid publishing the AXAML/mode default as an initial native resize.
        // Keep the requested client separate from ConfigureNotify acknowledgements.
        _startupWindowClient = new Size(_config.WindowWidth, _config.WindowHeight);
        _startupFrameClient = _startupWindowClient;
        Width = _config.WindowWidth;
        Height = _config.WindowHeight;
        _lastNormalClient = _startupWindowClient.Value;
    }

    private void ObserveStartupWindowResize(WindowResizedEventArgs e)
    {
        if (!_hasOpened || _isClosing) return;
        if (e.Reason == WindowResizeReason.User)
        {
            CancelStartupWindowFit();
            return;
        }
        if (_startupWindowClient is not Size target) return;
        if (System.Math.Abs(ClientSize.Width - target.Width) <= 1
            && System.Math.Abs(ClientSize.Height - target.Height) <= 1)
        {
            CompleteStartupWindowFit();
            return;
        }
        if (_startupSizeCorrectionQueued || WindowState != WindowState.Normal) return;
        _startupSizeCorrectionQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _startupSizeCorrectionQueued = false;
            if (_startupWindowClient == null || _isClosing || WindowState != WindowState.Normal) return;
            FitStartupWindowToWorkingArea();
        }, DispatcherPriority.Loaded);
    }

    private Size WorkspaceMinimumSize => _config.WorkspaceMode == "Barebones"
        ? new Size(640, 480) : new Size(760, 500);

    private Size FitWorkspaceMinimum(Screen screen) => WindowStartupGeometry.Fit(
        ClientSize, FrameSize, WorkspaceMinimumSize, screen.WorkingArea, RenderScaling,
        Position, WindowStartupGeometry.NormalFrameInsetPixels).Minimum;

    private void ApplyWorkspaceMinimum(Size minimum)
    {
        if (MinWidth != minimum.Width) MinWidth = minimum.Width;
        if (MinHeight != minimum.Height) MinHeight = minimum.Height;
        X11WindowMinimum.Apply(this, minimum);
    }

    private void RefreshWorkspaceMinimumAfterResize()
    {
        if (_isClosing || WindowState != WindowState.Normal || _fullscreenOrigin != null
            || _restoringFullscreen || _fullscreenMaximizeRestoreClient != null
            || _updatingWorkspacePreferences || _startupWindowClient != null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
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
                || _updatingWorkspacePreferences || _startupWindowClient != null) return;
            Screen? currentScreen = Screens.ScreenFromWindow(this);
            if (currentScreen == null) return;
            ApplyWorkspaceMinimum(FitWorkspaceMinimum(currentScreen));
            if (ClientSize.Width < MinWidth - 1 || ClientSize.Height < MinHeight - 1)
                RequestNormalWindowSize(new Size(System.Math.Max(ClientSize.Width, MinWidth),
                    System.Math.Max(ClientSize.Height, MinHeight)));
        }, DispatcherPriority.Loaded);
    }

    private void CompleteStartupWindowFit()
    {
        // A zero-decoration platform may never publish a larger FrameSize. A
        // confirmed client ends the normal geometry guard independently of the
        // optional later decoration measurement.
        if (_windowPreferencesReady && _startupWindowClient is Size target
            && System.Math.Abs(ClientSize.Width - target.Width) <= 1
            && System.Math.Abs(ClientSize.Height - target.Height) <= 1)
            _startupWindowClient = null;
    }

    private void CancelStartupWindowFit()
    {
        _startupWindowClient = null;
        _startupFrameClient = null;
        StopWaitingForStartupFrame(this, System.EventArgs.Empty);
    }

    private void BeginStartupWindowFit()
    {
        // X11 can publish the native decorations only after Opened/Loaded.
        // Observe that initial measurement once; later resizes retain the mode minimum.
        PropertyChanged += OnStartupFrameMeasured;
        Closed += StopWaitingForStartupFrame;
        bool measured = HasMeasuredStartupFrame();
        if (measured) StopWaitingForStartupFrame(this, System.EventArgs.Empty);
        FitStartupWindowToWorkingArea();
        if (measured) _startupFrameClient = null;
    }

    private bool HasMeasuredStartupFrame() => FrameSize is Size frame &&
        (frame.Width > ClientSize.Width || frame.Height > ClientSize.Height);

    private void OnStartupFrameMeasured(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != FrameSizeProperty || !HasMeasuredStartupFrame()) return;
        StopWaitingForStartupFrame(this, System.EventArgs.Empty);
        Dispatcher.UIThread.Post(() =>
        {
            FitStartupWindowToWorkingArea();
            _startupFrameClient = null;
            CompleteStartupWindowFit();
        }, DispatcherPriority.Loaded);
    }

    private void StopWaitingForStartupFrame(object? sender, System.EventArgs e)
    {
        PropertyChanged -= OnStartupFrameMeasured;
        Closed -= StopWaitingForStartupFrame;
    }

    private void FitStartupWindowToWorkingArea()
    {
        // A user resize/mode change cancels the startup intent, including callbacks
        // already queued before that action. Never fall back to a stale live client.
        if ((_startupWindowClient ?? _startupFrameClient) is not Size desired || _isClosing || WindowState != WindowState.Normal
            || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        Screen? screen = Screens.ScreenFromWindow(this);
        if (screen == null) return;
        // Frame metrics describe the live client; preserve only their decoration
        // delta when fitting an as-yet unacknowledged requested client.
        Size? desiredFrame = FrameSize is Size frame
            ? new Size(desired.Width + System.Math.Max(0, frame.Width - ClientSize.Width),
                desired.Height + System.Math.Max(0, frame.Height - ClientSize.Height)) : null;
        WindowStartupFit fit = WindowStartupGeometry.Fit(desired, desiredFrame, WorkspaceMinimumSize,
            screen.WorkingArea, RenderScaling, Position, WindowStartupGeometry.NormalFrameInsetPixels);
        ApplyWorkspaceMinimum(fit.Minimum);
        _startupWindowClient = fit.Client;
        if (_startupFrameClient != null) _startupFrameClient = fit.Client;
        _applyingStartupWindowSize = true;
        try { RequestNormalWindowSize(fit.Client); }
        finally { _applyingStartupWindowSize = false; }
        Position = fit.Position;
        if (_startupWindowClient != null)
        {
            _lastNormalClient = fit.Client;
            _lastNormalPosition = fit.Position;
        }
        CompleteStartupWindowFit();
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
