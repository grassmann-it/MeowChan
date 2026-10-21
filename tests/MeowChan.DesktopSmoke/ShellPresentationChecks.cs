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
        PixelPoint position = window.Position;
        window.FindControl<Button>("FullscreenButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.FullScreen);
        Press(window, Key.F1);
        Require(window.FindControl<Border>("ShellHelpOverlay")!.IsVisible, "F1 did not open fullscreen shortcut help.");
        Press(window, Key.Escape);
        Require(!window.FindControl<Border>("ShellHelpOverlay")!.IsVisible && window.WindowState == WindowState.FullScreen,
            "Escape dismissed more than the topmost fullscreen help layer.");
        Press(window, Key.Escape);
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal && Math.Abs(window.ClientSize.Width - size.Width) <= 2);
        Require(Math.Abs(window.ClientSize.Height - size.Height) <= 2 && Math.Abs(window.Position.X - position.X) <= 2
            && Math.Abs(window.Position.Y - position.Y) <= 2, "Fullscreen did not restore normal window geometry.");
        window.WindowState = WindowState.Maximized;
        await Task.Delay(150);
        TextBox input = window.FindControl<TextBox>("UrlInput")!;
        input.Focus();
        Press(input, Key.F11);
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.FullScreen);
        Press(input, Key.Escape);
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Maximized);
        window.WindowState = WindowState.Normal;
        await Task.Delay(150);
    }

    private static void Press(Control target, Key key) => target.RaiseEvent(new KeyEventArgs
        { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = KeyModifiers.None });
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
