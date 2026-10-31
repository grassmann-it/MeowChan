using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static class ShellPresentationChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Size target = new(1000, 668);
        Size stale = new(850, 530);
        Size[] staleFrames = [stale];
        Require(MainWindow.ShouldRepairKnownMaximizedExitFrame(stale, target, staleFrames),
            "A known native exit frame was not marked for repair after unmaximize.");
        Require(!MainWindow.ShouldRepairKnownMaximizedExitFrame(target, target, staleFrames)
            && !MainWindow.ShouldRepairKnownMaximizedExitFrame(new Size(1280, 684), target, staleFrames)
            && !MainWindow.ShouldRepairKnownMaximizedExitFrame(new Size(640, 480), target, staleFrames),
            "Normal or later deliberate window sizes were marked as stale exit frames.");
        window.WindowState = WindowState.Normal;
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal);
        Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window)
            ?? throw new InvalidOperationException("Fullscreen resize probe has no native screen.");
        Size currentClient = window.ClientSize;
        Size currentFrame = window.FrameSize ?? currentClient;
        Size desired = new(1000, 700);
        PixelRect reservedArea = new(screen.WorkingArea.X, screen.WorkingArea.Y,
            screen.WorkingArea.Width - (int)Math.Ceiling(17 * window.RenderScaling),
            screen.WorkingArea.Height - (int)Math.Ceiling(13 * window.RenderScaling));
        WindowStartupFit baseline = WindowStartupGeometry.Fit(desired,
            new Size(desired.Width + Math.Max(0, currentFrame.Width - currentClient.Width),
                desired.Height + Math.Max(0, currentFrame.Height - currentClient.Height)),
            new Size(window.MinWidth, window.MinHeight), reservedArea, window.RenderScaling, window.Position,
            WindowStartupGeometry.NormalFrameInsetPixels);
        window.Width = baseline.Client.Width; window.Height = baseline.Client.Height;
        window.Position = baseline.Position;
        await WaitForSettledGeometryAsync(window, () => window.WindowState == WindowState.Normal
            && Math.Abs(window.ClientSize.Width - baseline.Client.Width) <= 2
            && Math.Abs(window.ClientSize.Height - baseline.Client.Height) <= 2
            && window.FrameSize is Size frame
            && (frame.Width + 17) * window.RenderScaling <= screen.WorkingArea.Width + 1
            && (frame.Height + 13) * window.RenderScaling <= screen.WorkingArea.Height + 1,
            "normal baseline settled");
        Size size = window.ClientSize;
        TraceGeometry(window, "normal baseline");
        PixelPoint position = window.Position;
        window.FindControl<Button>("FullscreenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitForSettledGeometryAsync(window, () => IsNativeFullscreen(window), "native fullscreen");
        Press(window, Key.F1);
        Require(window.FindControl<Border>("ShellHelpOverlay")!.IsVisible, "F1 did not open fullscreen shortcut help.");
        Press(window, Key.Escape);
        Require(!window.FindControl<Border>("ShellHelpOverlay")!.IsVisible && window.WindowState == WindowState.FullScreen,
            "Escape dismissed more than the topmost fullscreen help layer.");
        Press(window, Key.Escape);
        try
        {
            await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal && Math.Abs(window.ClientSize.Width - size.Width) <= 2);
        }
        catch { TraceGeometry(window, "normal restore failure"); throw; }
        TraceGeometry(window, "normal restored");
        Require(Math.Abs(window.ClientSize.Height - size.Height) <= 2 && Math.Abs(window.Position.X - position.X) <= 2
            && Math.Abs(window.Position.Y - position.Y) <= 2, "Fullscreen did not restore normal window geometry.");
        // A completed transition must also release ordinary resize behavior.
        window.RequestNormalWindowSize(new Size(size.Width + 17, size.Height + 13));
        try
        {
            await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - size.Width - 17) <= 2
                && Math.Abs(window.ClientSize.Height - size.Height - 13) <= 2);
        }
        catch { TraceGeometry(window, "ordinary resize growth failure"); throw; }
        TraceGeometry(window, "ordinary resize grown");
        window.RequestNormalWindowSize(size);
        try
        {
            await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - size.Width) <= 2
                && Math.Abs(window.ClientSize.Height - size.Height) <= 2);
        }
        catch { TraceGeometry(window, "ordinary resize return failure"); throw; }
        TraceGeometry(window, "ordinary resize returned");

        // Two user commands may arrive before Cocoa acknowledges native entry.
        // A delayed native FullScreen report must not undo the requested exit.
        if (OperatingSystem.IsMacOS())
        {
            Button fullscreen = window.FindControl<Button>("FullscreenButton")!;
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            // Let the queued managed restore run, then resize before Cocoa's
            // asynchronous entry acknowledgement has necessarily arrived.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            window.RequestNormalWindowSize(new Size(size.Width + 11, size.Height + 9));
            await WaitForSettledGeometryAsync(window, () => window.WindowState == WindowState.Normal
                && Math.Abs(window.ClientSize.Width - size.Width - 11) <= 2
                && Math.Abs(window.ClientSize.Height - size.Height - 9) <= 2, "rapid enter-exit ordinary resize");
            window.RequestNormalWindowSize(size);
            await WaitForSettledGeometryAsync(window, () => window.WindowState == WindowState.Normal
                && Math.Abs(window.ClientSize.Width - size.Width) <= 2, "rapid enter-exit resize returned");
            await CheckMacTransitionFeedbackReplayAsync(window, size);
        }

        window.WindowState = WindowState.Maximized;
        // The requested managed state precedes the Cocoa zoom animation. Do not
        // send the next command while native resize/state acknowledgements from
        // the preceding command are still arriving.
        await WaitForSettledGeometryAsync(window, () => IsNativeMaximized(window), "maximized baseline");
        TextBox input = window.FindControl<TextBox>("UrlInput")!;
        input.Focus();
        Press(input, Key.F11);
        await WaitForSettledGeometryAsync(window, () => IsNativeFullscreen(window), "maximized to fullscreen");
        Press(input, Key.Escape);
        await WaitForSettledGeometryAsync(window, () => IsNativeMaximized(window), "maximized restored");
        window.WindowState = WindowState.Normal;
        await WaitForSettledGeometryAsync(window, () => window.WindowState == WindowState.Normal
            && Math.Abs(window.ClientSize.Width - size.Width) <= 2
            && Math.Abs(window.ClientSize.Height - size.Height) <= 2, "final normal restored");
    }

    private static async Task CheckMacTransitionFeedbackReplayAsync(MainWindow window, Size normal)
    {
        // Deterministic callback replay supplements, never replaces, the real
        // native rapid-entry/exit check above. This is not an OS-input claim.
        Action<Size, Avalonia.Controls.WindowResizeReason> nativeResize = window.PlatformImpl?.Resized
            ?? throw new InvalidOperationException("Native resize callback is unavailable for the private Mac replay.");
        Size intermediate = new(normal.Width + 73, normal.Height + 57);
        Size requested = new(normal.Width + 11, normal.Height + 9);
        Size deliberate = new(normal.Width + 37, normal.Height + 23);
        window.ToggleFullscreen();
        await WaitForSettledGeometryAsync(window, () => IsNativeFullscreen(window), "feedback replay native entry");
        nativeResize(intermediate, Avalonia.Controls.WindowResizeReason.User);
        Require(FullscreenField<List<Size>>(window, "_fullscreenExitStaleNativeClients").Contains(intermediate),
            "The synthetic entry resize callback was not recorded as transition feedback.");
        Press(window, Key.Escape);
        await WaitForSettledGeometryAsync(window, () => window.WindowState == WindowState.Normal
            && Math.Abs(window.ClientSize.Width - normal.Width) <= 2
            && Math.Abs(window.ClientSize.Height - normal.Height) <= 2, "feedback replay native exit");
        Require(FullscreenField<List<Size>>(window, "_fullscreenExitStaleNativeClients").Contains(intermediate),
            "Leaving fullscreen erased a frame already observed during this entry.");
        window.RequestNormalWindowSize(requested);
        await WaitForSettledGeometryAsync(window, () => window.WindowState == WindowState.Normal
            && Math.Abs(window.ClientSize.Width - requested.Width) <= 2
            && Math.Abs(window.ClientSize.Height - requested.Height) <= 2, "feedback replay requested resize");
        nativeResize(intermediate, Avalonia.Controls.WindowResizeReason.User);
        Require(FullscreenField<MainWindow.FullscreenOrigin>(window, "_fullscreenExitIntent").Client == requested,
            "Late known native entry feedback overwrote the newer normal resize intent.");
        await WaitForSettledGeometryAsync(window, () => Math.Abs(window.ClientSize.Width - requested.Width) <= 2
            && Math.Abs(window.ClientSize.Height - requested.Height) <= 2, "feedback replay duplicate repaired");
        // Supersede a queued correction before it can dispatch. A genuinely
        // unknown normal User size must win instead of pinning the old target.
        nativeResize(intermediate, Avalonia.Controls.WindowResizeReason.User);
        Require(FullscreenField<bool>(window, "_fullscreenExitSizeCorrectionQueued"),
            "Known native feedback did not queue the correction that the next User resize must supersede.");
        nativeResize(deliberate, Avalonia.Controls.WindowResizeReason.User);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(FullscreenField<MainWindow.FullscreenOrigin>(window, "_fullscreenExitIntent").Client == deliberate
            && Math.Abs(window.Width - deliberate.Width) <= 2 && Math.Abs(window.Height - deliberate.Height) <= 2,
            "An obsolete queued correction overrode a newer unknown User resize.");
        window.RequestNormalWindowSize(normal);
        await WaitForSettledGeometryAsync(window, () => window.WindowState == WindowState.Normal
            && Math.Abs(window.ClientSize.Width - normal.Width) <= 2
            && Math.Abs(window.ClientSize.Height - normal.Height) <= 2, "feedback replay ordinary resize restored");
        Console.WriteLine("SMOKE fullscreen callback replay PASS: recorded entry retained across exit, duplicate repaired, unknown normal User resize supersedes queued correction; synthetic callback replay, not OS input");
    }

    private static T FullscreenField<T>(MainWindow window, string name) =>
        (T)typeof(MainWindow).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(window)!;

    private static bool IsNativeFullscreen(MainWindow window) => window.WindowState == WindowState.FullScreen
        && window.Screens.ScreenFromWindow(window) is { } screen
        && Math.Abs(window.Bounds.Width * window.RenderScaling - screen.Bounds.Width) <= 2
        && Math.Abs(window.Bounds.Height * window.RenderScaling - screen.Bounds.Height) <= 2;

    private static bool IsNativeMaximized(MainWindow window)
    {
        Size frame = window.FrameSize ?? window.ClientSize;
        if (window.WindowState != WindowState.Maximized || window.Screens.ScreenFromWindow(window) is not { } screen)
            return false;
        if (OperatingSystem.IsMacOS()) return WindowChromeChecks.MacMaximizedBoundsMatch(window);
        if (OperatingSystem.IsWindows())
        {
            // Windows maximizes the client to the work area while its invisible
            // resize borders extend equally beyond each edge of that area.
            double scale = window.RenderScaling;
            double insetX = (frame.Width - window.ClientSize.Width) * scale / 2;
            double insetY = (frame.Height - window.ClientSize.Height) * scale / 2;
            return insetX >= 0 && insetY >= 0
                && Math.Abs(window.ClientSize.Width * scale - screen.WorkingArea.Width) <= 2
                && Math.Abs(window.ClientSize.Height * scale - screen.WorkingArea.Height) <= 2
                && Math.Abs(window.Bounds.Width * scale - screen.WorkingArea.Width) <= 2
                && Math.Abs(window.Bounds.Height * scale - screen.WorkingArea.Height) <= 2
                && Math.Abs(window.Position.X + insetX - screen.WorkingArea.X) <= 2
                && Math.Abs(window.Position.Y + insetY - screen.WorkingArea.Y) <= 2;
        }
        return Math.Abs(frame.Width * window.RenderScaling - screen.WorkingArea.Width) <= 2
            && Math.Abs(frame.Height * window.RenderScaling - screen.WorkingArea.Height) <= 2;
    }

    private static async Task WaitForSettledGeometryAsync(MainWindow window, Func<bool> ready, string stage)
    {
        (Size Client, Size? Frame, PixelPoint Position)? previous = null;
        DateTime stableSince = DateTime.UtcNow;
        try
        {
            await Program.WaitUntilAsync(() =>
            {
                var current = (window.ClientSize, window.FrameSize, window.Position);
                if (!ready() || previous != current)
                {
                    previous = current;
                    stableSince = DateTime.UtcNow;
                    return false;
                }
                return DateTime.UtcNow - stableSince >= TimeSpan.FromMilliseconds(350);
            });
        }
        catch { TraceGeometry(window, stage + " failure"); throw; }
        TraceGeometry(window, stage);
    }

    private static void TraceGeometry(MainWindow window, string stage) => Console.WriteLine(
        $"SMOKE fullscreen {stage}: state={window.WindowState}; requested={window.Width}x{window.Height}; bounds={window.Bounds}; client={window.ClientSize}; frame={window.FrameSize}; position={window.Position}; screen={window.Screens.ScreenFromWindow(window)?.Bounds}; working-area={window.Screens.ScreenFromWindow(window)?.WorkingArea}; scale={window.RenderScaling}.");

    private static void Press(Control target, Key key) => target.RaiseEvent(new KeyEventArgs
        { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.None });
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
