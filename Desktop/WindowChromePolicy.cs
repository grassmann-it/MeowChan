using Avalonia.Controls;

namespace MeowChan.Desktop;

// Presentation only: no monitor sizing, restore snapshots or native frame corrections.
internal static class WindowChromePolicy
{
    internal const string MaximizePath = "M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z";
    internal const string RestorePath = "M 2.5,2.5 L 2.5,0.5 L 9.5,0.5 L 9.5,7.5 L 7.5,7.5 M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z";

    internal static bool CanToggleMaximize(WindowState state, bool canMaximize, bool canResize) =>
        state != WindowState.FullScreen && (state == WindowState.Maximized ? canResize : canMaximize);

    internal static WindowState ToggleMaximize(WindowState state, bool canMaximize, bool canResize) =>
        !CanToggleMaximize(state, canMaximize, canResize) ? state :
        state == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    internal static string MaximizeLabel(WindowState state) =>
        state == WindowState.Maximized ? "window.restore" : "window.maximize";

    // Small modal prompts drop minimize (a minimized modal strands its blocked owner);
    // the window's own CanMinimize and the window manager's shortcuts stay as they are.
    internal static bool ShowsMinimize(WindowChromeKind kind) => kind == WindowChromeKind.Window;

    // Fixed-size windows offer neither the button nor the double-click.
    internal static bool ShowsMaximize(bool canResize) => canResize;
}
