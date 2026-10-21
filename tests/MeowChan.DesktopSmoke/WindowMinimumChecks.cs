using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static class WindowMinimumChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        string originalMode = Config.LoadConfig().WorkspaceMode;
        WindowState originalState = window.WindowState;
        Size originalSize = window.Bounds.Size;
        try
        {
            window.WindowState = WindowState.Normal;
            await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal);
            foreach ((string mode, Size minimum) in new[]
            {
                ("Barebones", new Size(640, 480)),
                ("Simple", new Size(760, 500)),
                ("Advanced", new Size(760, 500))
            })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window)
                    ?? throw new InvalidOperationException("Minimum resize probe has no native screen.");
                Size client = window.ClientSize;
                Size frame = window.FrameSize ?? client;
                Size decoration = new(Math.Max(0, frame.Width - client.Width),
                    Math.Max(0, frame.Height - client.Height));
                WindowStartupFit fit = WindowStartupGeometry.Fit(minimum,
                    new Size(minimum.Width + decoration.Width, minimum.Height + decoration.Height),
                    minimum, screen.WorkingArea, window.RenderScaling, window.Position,
                    WindowStartupGeometry.NormalFrameInsetPixels);
                if (fit.Minimum.Width < minimum.Width - 1 || fit.Minimum.Height < minimum.Height - 1)
                {
                    Console.WriteLine($"SMOKE minimum {mode}: skipped native floor because working-area={screen.WorkingArea}, scale={window.RenderScaling}, frame-inset={decoration}.");
                    continue;
                }

                await Program.WaitUntilAsync(() => Near(window.MinWidth, minimum.Width)
                    && Near(window.MinHeight, minimum.Height));
                window.Width = 200;
                window.Height = 150;
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Delay(120);
                Require(window.WindowState == WindowState.Normal
                    && window.ClientSize.Width >= minimum.Width - 1
                    && window.ClientSize.Height >= minimum.Height - 1,
                    $"{mode} allowed a normal native client below {minimum}: actual={window.ClientSize}, requested={window.Width}×{window.Height}, minimum={window.MinWidth}×{window.MinHeight}.");
                await WindowChromeChecks.CheckReachableAsync(window);

                WindowStartupFit expanded = WindowStartupGeometry.Fit(
                    new Size(minimum.Width + 20, minimum.Height + 20),
                    new Size(minimum.Width + 20 + decoration.Width, minimum.Height + 20 + decoration.Height),
                    minimum, screen.WorkingArea, window.RenderScaling, window.Position,
                    WindowStartupGeometry.NormalFrameInsetPixels);
                if (expanded.Client.Width < minimum.Width + 19 || expanded.Client.Height < minimum.Height + 19)
                    continue;
                window.Width = expanded.Client.Width;
                window.Height = expanded.Client.Height;
                await Program.WaitUntilAsync(() => window.ClientSize.Width >= minimum.Width + 18
                    && window.ClientSize.Height >= minimum.Height + 18);
                // Simulate a minimum left behind by an earlier small-screen fit.
                window.MinWidth = 320;
                window.MinHeight = 240;
                window.Width = 200;
                window.Height = 150;
                await Program.WaitUntilAsync(() => Near(window.MinWidth, minimum.Width)
                    && Near(window.MinHeight, minimum.Height)
                    && window.ClientSize.Width >= minimum.Width - 1
                    && window.ClientSize.Height >= minimum.Height - 1);
                Console.WriteLine($"SMOKE minimum {mode}: native floor={minimum}, actual={window.ClientSize}, recovered after stale 320×240 floor.");
            }
        }
        finally
        {
            await PresentationChecks.SwitchModeAsync(window, originalMode);
            window.WindowState = WindowState.Normal;
            window.Width = originalSize.Width;
            window.Height = originalSize.Height;
            window.WindowState = originalState;
            await Program.WaitUntilAsync(() => window.WindowState == originalState
                && (originalState != WindowState.Normal ||
                    (Near(window.Bounds.Width, originalSize.Width) && Near(window.Bounds.Height, originalSize.Height))));
        }
    }

    private static bool Near(double actual, double expected) => Math.Abs(actual - expected) <= 1;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
