using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

internal static class WindowChromeChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Require(window.WindowDecorations == WindowDecorations.BorderOnly,
            "The shell still requests an OS title bar.");
        await CheckReachableAsync(window);
        WindowState original = window.WindowState;
        Size originalBounds = window.Bounds.Size;
        Button maximize = window.FindControl<Button>("WindowMaximizeButton")!;
        try
        {
            window.WindowState = WindowState.Normal;
            await Program.WaitUntilAsync(() => AutomationProperties.GetName(maximize) == Texts.T("window.maximize"));
            Click(maximize);
            await Program.WaitUntilAsync(() => window.WindowState == WindowState.Maximized
                && AutomationProperties.GetName(maximize) == Texts.T("window.restore"));
            await WaitForMaximizedBoundsAsync(window);
            await CheckReachableAsync(window);
            Click(maximize);
            await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal
                && AutomationProperties.GetName(maximize) == Texts.T("window.maximize"));
            await WaitForNormalBoundsAsync(window, originalBounds);
            await CheckReachableAsync(window);
            DoublePressHeader(window);
            await Program.WaitUntilAsync(() => window.WindowState == WindowState.Maximized);
            await WaitForMaximizedBoundsAsync(window);
            await CheckReachableAsync(window);
            DoublePressHeader(window);
            await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal);
            await WaitForNormalBoundsAsync(window, originalBounds);
            await CheckReachableAsync(window);
            // A completed custom-caption restore must not constrain later normal
            // resizes. Exercise actual native geometry before minimizing.
            Size resizedBounds = new(originalBounds.Width + 17, originalBounds.Height + 13);
            window.Width = resizedBounds.Width;
            window.Height = resizedBounds.Height;
            await WaitForNormalBoundsAsync(window, resizedBounds);
            window.Width = originalBounds.Width;
            window.Height = originalBounds.Height;
            await WaitForNormalBoundsAsync(window, originalBounds);
            await CheckReachableAsync(window);
            Click(window.FindControl<Button>("WindowMinimizeButton")!);
            await Program.WaitUntilAsync(() => window.WindowState == WindowState.Minimized);
            Require(window.IsVisible && window.ShowInTaskbar, "The minimize caption stranded the window.");
        }
        catch
        {
            Console.WriteLine($"SMOKE caption sequence failure: original={original}/{originalBounds}; actual={window.WindowState}/{window.Bounds.Size}; requested={window.Width}x{window.Height}; frame={window.FrameSize}; area={window.Screens.ScreenFromWindow(window)?.WorkingArea}.");
            Program.CaptureWindow(window, "smoke-caption-sequence-failure.png");
            throw;
        }
        finally
        {
            window.WindowState = original;
            // Preserve this fixture's input geometry even when a check fails.
            if (original == WindowState.Normal)
            {
                window.Width = originalBounds.Width;
                window.Height = originalBounds.Height;
            }
            window.Activate();
            try
            {
                await Program.WaitUntilAsync(() => window.WindowState == original
                    && (original != WindowState.Normal || (Math.Abs(window.Bounds.Width - originalBounds.Width) <= 1
                        && Math.Abs(window.Bounds.Height - originalBounds.Height) <= 1)));
            }
            catch
            {
                Console.WriteLine($"SMOKE caption restore failure: original={original}/{originalBounds}; actual={window.WindowState}/{window.Bounds.Size}; requested={window.Width}x{window.Height}.");
                Program.CaptureWindow(window, "smoke-caption-restore-failure.png");
                throw;
            }
        }
        Console.WriteLine("SMOKE custom caption: no OS title bar; maximize/restore labels, header double-click and minimize/taskbar roundtrip passed.");
    }

    private static Task WaitForNormalBoundsAsync(MainWindow window, Size expected) => Program.WaitUntilAsync(() =>
        Math.Abs(window.Bounds.Width - expected.Width) <= 1 && Math.Abs(window.Bounds.Height - expected.Height) <= 1);

    private static Task WaitForMaximizedBoundsAsync(MainWindow window) => Program.WaitUntilAsync(() =>
    {
        Avalonia.Platform.Screen? screen = window.Screens.ScreenFromWindow(window);
        return screen != null && Math.Abs(window.Bounds.Width * window.RenderScaling - screen.WorkingArea.Width) <= 2
            && Math.Abs(window.Bounds.Height * window.RenderScaling - screen.WorkingArea.Height) <= 2;
    });

    private static void DoublePressHeader(MainWindow window)
    {
        Control logo = window.FindControl<Image>("WorkspaceLogo")!;
        Point point = logo.TranslatePoint(new Point(15, 15), window)!.Value;
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        logo.RaiseEvent(new PointerPressedEventArgs(logo, pointer, window, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, clickCount: 2));
    }

    internal static async Task CheckReachableAsync(MainWindow window)
    {
        window.UpdateLayout();
        foreach (string name in new[] { "WindowMinimizeButton", "WindowMaximizeButton", "WindowCloseButton", "PauseButton", "ThemeToggleButton" })
        {
            Button button = window.FindControl<Button>(name)!;
            Point? origin = button.TranslatePoint(default, window);
            Require(button.IsEffectivelyVisible && button.IsEffectivelyEnabled && button.Bounds.Width >= 30
                && button.Bounds.Height >= 30 && origin is Point p && p.X >= 0 && p.Y >= 0
                && p.X + button.Bounds.Width <= window.Bounds.Width + 1
                && p.Y + button.Bounds.Height <= window.Bounds.Height + 1,
                "Unreachable window caption: " + name);
            // The renderer publishes its hit-test tree after native resizes have
            // already updated layout; wait for that tree rather than testing it stale.
            await Program.WaitUntilAsync(() =>
            {
                Point? center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
                return center.HasValue && window.InputHitTest(center.Value) is Visual hit
                    && (ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button));
            });
        }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
