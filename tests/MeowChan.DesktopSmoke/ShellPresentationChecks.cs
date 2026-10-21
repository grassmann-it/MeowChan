using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MeowChan.DesktopSmoke;

internal static class ShellPresentationChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        window.WindowState = WindowState.Normal;
        window.Width = 1000; window.Height = 700;
        await Task.Delay(150);
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
        window.Width = size.Width + 17; window.Height = size.Height + 13;
        await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - size.Width - 17) <= 2
            && Math.Abs(window.ClientSize.Height - size.Height - 13) <= 2);
        window.Width = size.Width; window.Height = size.Height;
        await Program.WaitUntilAsync(() => Math.Abs(window.ClientSize.Width - size.Width) <= 2
            && Math.Abs(window.ClientSize.Height - size.Height) <= 2);
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

    private static bool IsNativeFullscreen(MainWindow window) => window.WindowState == WindowState.FullScreen
        && window.Screens.ScreenFromWindow(window) is { } screen
        && Math.Abs(window.Bounds.Width * window.RenderScaling - screen.Bounds.Width) <= 2
        && Math.Abs(window.Bounds.Height * window.RenderScaling - screen.Bounds.Height) <= 2;

    private static bool IsNativeMaximized(MainWindow window)
    {
        Size frame = window.FrameSize ?? window.ClientSize;
        return window.WindowState == WindowState.Maximized && window.Screens.ScreenFromWindow(window) is { } screen
            && Math.Abs(frame.Width * window.RenderScaling - screen.WorkingArea.Width) <= 2
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
