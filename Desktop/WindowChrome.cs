using System;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Serilog;

namespace MeowChan.Desktop;

/// <summary>Whether MeowChan draws its own title bar or leaves it to the platform.</summary>
internal enum WindowChromeMode
{
    /// <summary>Windows and Linux: native border and resizing, no OS title bar; <see cref="WindowChromeBar"/>
    /// is the caption with its own minimize/maximize/close buttons.</summary>
    Custom,
    /// <summary>macOS: the app caption fills the native title bar area, but the native traffic lights,
    /// full screen, rounded corners and window behaviour stay with the system (owner decision 13.11.2026).</summary>
    NativeCaptionButtons,
    /// <summary>Escape hatch: the platform's own title bar; the app caption hides itself.</summary>
    System
}

/// <summary>Which caption commands a window offers. Maximize additionally needs <see cref="Window.CanResize"/>.</summary>
internal enum WindowChromeKind
{
    /// <summary>Minimize, maximize/restore, close: tool windows, modeless windows and windows without an owner.</summary>
    Window,
    /// <summary>Maximize/restore and close: small modal prompts, where a minimized modal would strand its blocked owner.</summary>
    Dialog
}

/// <summary>
/// One place for the window frame policy of every MeowChan window: the app caption, square
/// outer corners on Windows (DWM) and Linux, native corners and traffic lights on macOS, and
/// the system fallback.
/// </summary>
/// <remarks>
/// <c>MEOWCHAN_WINDOW_CHROME=system</c> restores the platform title bar in every window for a
/// platform or window manager that misbehaves with client-drawn captions; <c>custom</c> forces the
/// own caption buttons (also on macOS); no or any other value uses the platform default. It is read
/// once per process.
/// </remarks>
internal static class WindowChrome
{
    internal const string EnvironmentVariable = "MEOWCHAN_WINDOW_CHROME";
    internal const double TitleBarHeight = WorkspaceMetrics.TitleBarHeight;
    internal const string RootName = "WindowChromeRoot";
    // Room for the three native macOS traffic lights at the leading edge of the caption.
    internal const double TrafficLightInset = 78;

    internal static WindowChromeMode Mode { get; } =
        Resolve(Environment.GetEnvironmentVariable(EnvironmentVariable), OperatingSystem.IsMacOS());

    internal static WindowChromeMode Resolve(string? value, bool macOS) => value?.Trim().ToLowerInvariant() switch
    {
        "system" => WindowChromeMode.System,
        "custom" => WindowChromeMode.Custom,
        _ => macOS ? WindowChromeMode.NativeCaptionButtons : WindowChromeMode.Custom
    };

    /// <summary>True for the two values the switch documents (case-insensitive); anything else is ignored.</summary>
    internal static bool IsRecognised(string? value) => value?.Trim().ToLowerInvariant() is "system" or "custom";

    /// <summary>One line for the log and for bug reports: the active mode and where it came from.</summary>
    internal static string Describe(string? value, bool macOS)
    {
        WindowChromeMode mode = Resolve(value, macOS);
        string text = value?.Trim() ?? string.Empty;
        string origin = text.Length == 0 ? EnvironmentVariable + " not set, platform default"
            : IsRecognised(text) ? "set by " + EnvironmentVariable
            : EnvironmentVariable + "='" + Printable(text) + "' is neither system nor custom and is ignored, platform default";
        return "Window chrome mode " + mode + " (" + origin + ")";
    }

    // The value comes from the environment: keep it to a short printable fragment.
    private static string Printable(string text)
    {
        string shown = text.Length <= 24 ? text : text[..24] + "...";
        return string.Create(shown.Length, shown, static (span, source) =>
        {
            for (int index = 0; index < source.Length; index++) span[index] = char.IsControl(source[index]) ? '?' : source[index];
        });
    }

    private static int _startupLogged;

    /// <summary>Logs the active mode once per process, at startup (a warning when the variable holds an unknown value).</summary>
    internal static void LogStartup() => LogStartup(Log.Logger, Environment.GetEnvironmentVariable(EnvironmentVariable), OperatingSystem.IsMacOS());

    internal static void LogStartup(ILogger logger, string? value, bool macOS)
    {
        if (Interlocked.Exchange(ref _startupLogged, 1) != 0) return;
        string line = Describe(value, macOS);
        if (!string.IsNullOrWhiteSpace(value) && !IsRecognised(value)) logger.Warning("{WindowChromeStartup:l}", line);
        else logger.Information("{WindowChromeStartup:l}", line);
    }

    internal static void ResetStartupLogForTests() => Volatile.Write(ref _startupLogged, 0);

    /// <summary>The app caption is shown (own or native caption buttons).</summary>
    internal static bool ShowsCaption(WindowChromeMode mode) => mode != WindowChromeMode.System;

    /// <summary>The app draws minimize/maximize/close itself.</summary>
    internal static bool ShowsOwnCaptionButtons(WindowChromeMode mode) => mode == WindowChromeMode.Custom;

    /// <summary>Leading caption inset that keeps app content clear of native caption buttons.</summary>
    internal static double LeadingInset(WindowChromeMode mode) =>
        mode == WindowChromeMode.NativeCaptionButtons ? TrafficLightInset : 0;

    /// <summary>Square corners are requested from Windows 11 DWM only; macOS keeps its native rounding,
    /// and on Linux the content is square while any compositor rounding is the window manager's.</summary>
    internal static Win32Properties.WindowCornerPreference CornerPreference(bool windows) =>
        windows ? Win32Properties.WindowCornerPreference.DoNotRound : Win32Properties.WindowCornerPreference.Default;

    /// <summary>Applies the frame policy before the window is shown. Safe to repeat.</summary>
    internal static void Apply(Window window) => Apply(window, Mode);

    internal static void Apply(Window window, WindowChromeMode mode)
    {
        ArgumentNullException.ThrowIfNull(window);
        // Windows 11 DWM; ignored before build 22000. Maximized and snapped windows are
        // square anyway, so this only changes the floating normal state.
        if (OperatingSystem.IsWindows())
            Win32Properties.SetWindowCornerPreference(window, CornerPreference(windows: true));
        if (mode == WindowChromeMode.Custom)
        {
            // BorderOnly keeps the native frame: resizing, Snap, shadow and Alt+F4 stay with the platform.
            window.WindowDecorations = WindowDecorations.BorderOnly;
            window.ExtendClientAreaToDecorationsHint = true;
            window.ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        }
        else if (mode == WindowChromeMode.NativeCaptionButtons)
        {
            // Full keeps the titled NSWindow (traffic lights, full screen, zoom, rounded corners);
            // the extended client area lets the app caption sit under the transparent title bar.
            window.WindowDecorations = WindowDecorations.Full;
            window.ExtendClientAreaToDecorationsHint = true;
            window.ExtendClientAreaTitleBarHeightHint = TitleBarHeight;
        }
        else
        {
            window.WindowDecorations = WindowDecorations.Full;
            window.ExtendClientAreaToDecorationsHint = false;
            window.ExtendClientAreaTitleBarHeightHint = -1;
        }
    }

    /// <summary>
    /// Applies the frame policy and returns <paramref name="content"/> below an app caption,
    /// ready to be the window's Content.
    /// </summary>
    internal static Grid Frame(Window window, Control content, WindowChromeKind kind = WindowChromeKind.Window, string? captionName = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        Apply(window);
        WindowChromeBar caption = new() { Kind = kind };
        if (captionName != null) caption.Name = captionName;
        // Auto, not 48: in the system fallback the hidden caption must not leave an empty band.
        Grid root = new() { Name = RootName, RowDefinitions = new RowDefinitions("Auto,*") };
        // Paint the whole client root (as the XAML caption windows do), so a maximized or
        // snapped window never shows the desktop through an unpainted edge.
        root[!Panel.BackgroundProperty] = new DynamicResourceExtension("Workspace.Background");
        root.Children.Add(caption);
        Grid.SetRow(content, 1);
        root.Children.Add(content);
        // The caption attaches itself when the window shows it, so a window that is never shown leaks nothing.
        return root;
    }

    /// <summary>Moves a XAML window's existing content below an app caption (name scope and bindings stay with the window).</summary>
    internal static WindowChromeBar WrapContent(Window window, WindowChromeKind kind = WindowChromeKind.Window, string? captionName = null)
    {
        ArgumentNullException.ThrowIfNull(window);
        Control content = window.Content as Control
            ?? throw new InvalidOperationException(window.GetType().Name + " has no control content to frame.");
        window.Content = null;
        Grid root = Frame(window, content, kind, captionName);
        window.Content = root;
        return (WindowChromeBar)root.Children[0];
    }
}
