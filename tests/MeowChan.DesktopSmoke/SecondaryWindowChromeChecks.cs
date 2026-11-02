using System.Runtime.InteropServices;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopSmoke;

// ROOT HOOK: await SecondaryWindowChromeChecks.RunAsync(mainWindow);
// Run on UI thread, serially, on the existing isolated/native-allowed runner.
// Uses real product windows and MediaNativeInput (no routed-click fallback).
internal static class SecondaryWindowChromeChecks
{
    internal static async Task RunAsync(MainWindow owner)
    {
        string language = Texts.Language;
        try
        {
            await CheckAboutAsync(owner);
            await CheckSettingsAsync(owner);
        }
        finally { Texts.SetLanguage(language); owner.Activate(); }
    }

    private static async Task CheckAboutAsync(MainWindow owner)
    {
        var about = new AboutWindow(); // no UpdateSession: no browser or update requests
        bool closed = false;
        about.Closed += (_, _) => closed = true;
        try
        {
            Task dialog = about.ShowDialog(owner);
            using MediaNativeInput input = await StageDialogAsync(about, "about");
            await CheckCaptionAsync(about, input, "about");
            Button close = Caption(about, "WindowCloseButton");
            Require(close.Focus(), "About caption close cannot receive keyboard focus.");
            await input.KeyAsync(MediaNativeInput.Enter);
            await Program.WaitUntilAsync(() => closed);
            await dialog;
            await WaitActiveAsync(owner, "unforced-owner-return");
            Console.WriteLine("SMOKE About caption: native pointer min/max/restore/double-click; native keyboard Enter-close; Closed observed.");
        }
        finally { if (!closed) about.Close(); }
    }

    private static async Task CheckSettingsAsync(MainWindow owner)
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        var pending = new TaskCompletionSource<IReadOnlyList<IStorageFolder>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SettingsChecks.FixtureStorageProvider { Result = pending.Task };
        AvaloniaLocator.CurrentMutable.Bind<IStorageProviderFactory>()
            .ToConstant(new SettingsChecks.FixtureStorageProviderFactory(provider));
        // New in-memory settings only. The pending picker is the real busy path;
        // it returns cancellation, never reads/saves a user profile or destination.
        var settings = new SettingsWindow(new Config());
        bool closed = false;
        int closeAttempts = 0;
        settings.Closed += (_, _) => closed = true;
        settings.Closing += (_, _) => closeAttempts++;
        try
        {
            Task dialog = settings.ShowDialog(owner);
            using MediaNativeInput input = await StageDialogAsync(settings, "settings");
            await CheckCaptionAsync(settings, input, "settings");
            await SettingsSectionChecks.OpenAsync(settings.Editor, "settings.downloads");
            await Program.WaitUntilAsync(() => settings.Editor.FindControl<Control>("SettingsDownloadsPage")!.IsVisible);
            Button browse = settings.Editor.FindControl<Button>("BrowseButton")!;
            browse.BringIntoView();
            await ReachableAsync(settings, browse);
            await input.ClickAsync(browse);
            await Program.WaitUntilAsync(() => settings.Editor.IsBusy && provider.PickerCalls == 1);
            int previous = closeAttempts;
            await input.ClickAsync(Caption(settings, "WindowCloseButton"));
            await Program.WaitUntilAsync(() => closeAttempts > previous);
            Require(!closed && settings.IsVisible && settings.Editor.IsBusy,
                "Settings caption bypassed pending-picker OnClosing guard.");
            pending.SetResult(Array.Empty<IStorageFolder>());
            await Program.WaitUntilAsync(() => !settings.Editor.IsBusy);
            await input.ClickAsync(Caption(settings, "WindowCloseButton"));
            await Program.WaitUntilAsync(() => closed);
            await dialog;
            await WaitActiveAsync(owner, "unforced-owner-return");
            Console.WriteLine("SMOKE Settings caption: native pointer min/max/restore/double-click/close; pending-picker close cancelled; later close and Closed observed. Save-in-flight not exercised.");
        }
        finally
        {
            pending.TrySetResult(Array.Empty<IStorageFolder>());
            await Program.WaitUntilAsync(() => !settings.Editor.IsBusy);
            if (!closed) settings.Close();
        }
    }

    // Harness staging only: visibility/handle creation is not X11 viewability or
    // completion of Loaded/layout work. Never use this for the owner-return check.
    private static async Task<MediaNativeInput> StageDialogAsync(Window window, string name)
    {
        using var native = new NativeFocusProbe();
        MediaNativeInput? input = null;
        string phase = "visible-handle";
        try
        {
            await Program.WaitUntilAsync(() => window.IsVisible && window.TryGetPlatformHandle()?.Handle is nint handle && handle != 0);
            phase = "dispatcher-layout-mapped";
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                return window.IsVisible && window.ClientSize.Width > 0 && window.ClientSize.Height > 0
                    && native.IsViewable(window);
            });
            await ReachableAsync(window, Caption(window, "WindowCloseButton"));
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            native.Log(window, name + " mapped-ready (before test activation)");
            phase = "explicit-test-activation";
            input = new MediaNativeInput(window);
            // One WM/platform activation request after the real map/layout barrier,
            // not repeated direct X focus writes racing initial WM map processing.
            window.Activate();
            await Program.WaitUntilAsync(() => window.IsActive);
            native.Log(window, name + " staged (explicit test activation; not product initial focus)");
            return input;
        }
        catch
        {
            native.Log(window, name + " staging-failed phase=" + phase);
            input?.Dispose();
            throw;
        }
    }

    private static async Task WaitActiveAsync(Window window, string phase)
    {
        try { await Program.WaitUntilAsync(() => window.IsActive); }
        catch
        {
            using var native = new NativeFocusProbe();
            native.Log(window, phase + " failed (no activation requested)");
            throw;
        }
    }

    // Read-only observations on the same private Xvfb allowed by MediaNativeInput.
    // No personal-display connection, event injection, or activation in this probe.
    private sealed class NativeFocusProbe : IDisposable
    {
        private readonly nint _display;
        internal NativeFocusProbe()
        {
            if (OperatingSystem.IsLinux()
                && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1"
                && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            {
                _display = XOpenDisplay(0);
                if (_display == 0) throw new InvalidOperationException("Cannot observe private X11 dialog mapping/focus.");
            }
        }
        internal bool IsViewable(Window window)
        {
            if (_display == 0) return true; // Other platforms retain native IsActive gate.
            nint handle = window.TryGetPlatformHandle()?.Handle ?? 0;
            return handle != 0 && XGetWindowAttributes(_display, handle, out var attributes) != 0
                && attributes.MapState == 2; // IsViewable: mapped, including every ancestor.
        }
        internal void Log(Window window, string phase)
        {
            nint handle = window.TryGetPlatformHandle()?.Handle ?? 0;
            string observation = "unavailable (not private X11)";
            if (_display != 0)
            {
                XWindowAttributes attributes = default;
                int status = handle == 0 ? 0 : XGetWindowAttributes(_display, handle, out attributes);
                int mapState = status == 0 ? -1 : attributes.MapState;
                XGetInputFocus(_display, out nint focus, out int revert);
                observation = $"attributesStatus={status}, mapState={mapState}, nativeFocus=0x{focus:x}, focusEqualsClient={focus == handle && handle != 0}, revertTo={revert}";
            }
            Console.WriteLine($"SMOKE secondary focus: {phase}; IsVisible={window.IsVisible}; Handle=0x{handle:x}; IsActive={window.IsActive}; ClientSize={window.ClientSize}; X11={observation}");
        }
        public void Dispose() { if (_display != 0) XCloseDisplay(_display); }

        // Xlib XWindowAttributes ABI: Bool/int stay 32-bit, XIDs/long/pointers native-sized.
        [StructLayout(LayoutKind.Sequential)]
        private struct XWindowAttributes
        {
            internal int X, Y, Width, Height, BorderWidth, Depth;
            internal nint Visual, Root;
            internal int Class, BitGravity, WinGravity, BackingStore;
            internal nuint BackingPlanes, BackingPixel;
            internal int SaveUnder;
            internal nint Colormap;
            internal int MapInstalled, MapState;
            internal nint AllEventMasks, YourEventMask, DoNotPropagateMask;
            internal int OverrideRedirect;
            internal nint Screen;
        }
        [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
        [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
        [DllImport("libX11.so.6")] private static extern int XGetWindowAttributes(nint display, nint window, out XWindowAttributes attributes);
        [DllImport("libX11.so.6")] private static extern int XGetInputFocus(nint display, out nint focus, out int revertTo);
    }

    private static async Task CheckCaptionAsync(Window window, MediaNativeInput input, string name)
    {
        Require(window.WindowDecorations == WindowDecorations.BorderOnly, name + " still requests an OS titlebar.");
        Button minimize = Caption(window, "WindowMinimizeButton");
        Button maximize = Caption(window, "WindowMaximizeButton");
        Button close = Caption(window, "WindowCloseButton");
        foreach (Button button in new[] { minimize, maximize, close }) await ReachableAsync(window, button);
        foreach (string language in new[] { "en", "de" })
        {
            Texts.SetLanguage(language);
            await Program.WaitUntilAsync(() => AutomationProperties.GetName(close) == Texts.T("action.close"));
            Require(AutomationProperties.GetName(minimize) == Texts.T("window.minimize")
                && AutomationProperties.GetName(maximize) == Texts.T("window.maximize"), "Caption labels did not refresh.");
            await Program.WaitUntilAsync(() => !string.IsNullOrWhiteSpace(window.Title)
                && window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "WindowCaptionTitle").Text == window.Title);
        }
        foreach (ThemeVariant theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
        {
            window.RequestedThemeVariant = theme;
            await Program.WaitUntilAsync(() => window.ActualThemeVariant == theme);
            Border bar = window.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("shell-bar"));
            Require(bar.Background is ISolidColorBrush { Color.A: 255 }, "Caption theme surface is not opaque.");
            Program.CaptureWindow(window, $"smoke-chrome-{name}-{theme.ToString().ToLowerInvariant()}.png");
        }
        window.CanMinimize = false;
        window.CanMaximize = false;
        Require(!minimize.IsEnabled && !maximize.IsEnabled, "Caption ignored disabled window capabilities.");
        window.CanMinimize = true;
        window.CanMaximize = true;
        Size normal = window.ClientSize;
        await input.ClickAsync(maximize);
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Maximized
            && AutomationProperties.GetName(maximize) == Texts.T("window.restore"));
        Control root = window.Content as Control ?? throw new InvalidOperationException("Secondary client root is missing.");
        await WindowChromeChecks.CheckMaximizedClientEdgesAsync(window, root, name);
        await ReachableAsync(window, close);
        await input.ClickAsync(maximize);
        await WaitNormalAsync(window, normal);
        Control title = window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "WindowCaptionTitle");
        TimeSpan doubleTap = Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse)
            ?? throw new InvalidOperationException("Caption probe has no platform double-tap settings.");
        await Task.Delay(doubleTap + TimeSpan.FromMilliseconds(100));
        await input.ClickAsync(title, new Point(12, 10));
        await input.ClickAsync(title, new Point(12, 10));
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Maximized);
        await Task.Delay(doubleTap + TimeSpan.FromMilliseconds(100));
        await input.ClickAsync(title, new Point(12, 10));
        await input.ClickAsync(title, new Point(12, 10));
        await WaitNormalAsync(window, normal);
        await input.ClickAsync(minimize);
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Minimized);
        Require(window.IsVisible && window.ShowInTaskbar, "Secondary minimize lost its taskbar return path.");
        // Like existing main-caption smoke: programmatic unminimize is NOT a
        // native taskbar click, and is deliberately described as such.
        window.WindowState = WindowState.Normal;
        window.Activate();
        await Program.WaitUntilAsync(() => window.IsActive && window.WindowState == WindowState.Normal);
        await ReachableAsync(window, close);
        Console.WriteLine($"SMOKE {name} caption backend: {input.Backend}; minimize return requested programmatically; drag/snap/Alt+F4 not exercised.");
    }

    private static Task WaitNormalAsync(Window window, Size size) => Program.WaitUntilAsync(() =>
        window.WindowState == WindowState.Normal && Math.Abs(window.ClientSize.Width - size.Width) <= 1
        && Math.Abs(window.ClientSize.Height - size.Height) <= 1);

    private static Button Caption(Window window, string name) => window.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Name == name && button.GetVisualAncestors().Any(a => a is WindowChromeBar));

    private static Task ReachableAsync(Window window, Control control) => Program.WaitUntilAsync(() =>
    {
        window.UpdateLayout();
        Point? center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window);
        return control.IsEffectivelyVisible && control.IsEffectivelyEnabled && control.Bounds.Width >= 30 && control.Bounds.Height >= 30
            && center is Point point && point.X >= 0 && point.Y >= 0 && point.X < window.ClientSize.Width && point.Y < window.ClientSize.Height
            && window.InputHitTest(point) is Visual hit && (ReferenceEquals(hit, control) || hit.GetVisualAncestors().Contains(control));
    });

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
