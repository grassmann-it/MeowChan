using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    private void BeginStartupWindowFit()
    {
        // X11 can publish the native decorations only after Opened/Loaded.
        // Observe that initial measurement once; later user resizes stay unrestricted.
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
        WindowStartupFit fit = WindowStartupGeometry.Fit(ClientSize, FrameSize, new Size(MinWidth, MinHeight),
            screen.WorkingArea, RenderScaling, Position, WindowStartupGeometry.NormalFrameInsetPixels);
        MinWidth = fit.Minimum.Width;
        MinHeight = fit.Minimum.Height;
        Width = fit.Client.Width;
        Height = fit.Client.Height;
        Position = fit.Position;
    }

    private void SetWorkspaceWindowSize(double width, double height)
    {
        // Switching away from Barebones can restore a larger remembered size.
        // Fit that explicit mode change, including native borders, to the current
        // monitor just as at startup. Later user resizes remain unrestricted.
        Screen? screen = Screens.ScreenFromWindow(this);
        if (screen == null)
        {
            Width = width;
            Height = height;
            return;
        }
        Size desired = new(width, height);
        Size? desiredFrame = FrameSize is Size frame
            ? new Size(width + System.Math.Max(0, frame.Width - ClientSize.Width),
                height + System.Math.Max(0, frame.Height - ClientSize.Height)) : null;
        WindowStartupFit fit = WindowStartupGeometry.Fit(desired, desiredFrame, new Size(MinWidth, MinHeight),
            screen.WorkingArea, RenderScaling, Position, WindowStartupGeometry.NormalFrameInsetPixels);
        MinWidth = fit.Minimum.Width;
        MinHeight = fit.Minimum.Height;
        Width = fit.Client.Width;
        Height = fit.Client.Height;
        Position = fit.Position;
    }
}
