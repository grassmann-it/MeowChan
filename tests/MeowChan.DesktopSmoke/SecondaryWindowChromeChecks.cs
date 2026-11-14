using System.Runtime.InteropServices;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
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
            await CheckRatingsManagerAsync(owner);
            await CheckRatingPromptAsync(owner);
            await CheckModelessBookmarksAsync(owner);
            await CheckOwnerlessStartupErrorAsync(owner);
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
            // Modals plan MD3: About is a modal dialog on the factory, so its caption has no minimize.
            await CheckCaptionAsync(about, input, "about", WindowChromeKind.Dialog);
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

    // MEOWCHAN_WINDOW_CHROME=system: the platform caption returns everywhere, the app caption
    // collapses without leaving a band, and closing still runs through each window's own path.
    internal static async Task RunSystemFallbackAsync(MainWindow owner)
    {
        Require(WindowChrome.Mode == WindowChromeMode.System, "System fallback check outside MEOWCHAN_WINDOW_CHROME=system.");
        Require(owner.WindowDecorations == WindowDecorations.Full && !owner.ExtendClientAreaToDecorationsHint
            && !owner.FindControl<StackPanel>("WindowCaptionButtons")!.IsVisible
            && owner.FindControl<Border>("WorkspaceTitleBarFrame")!.IsVisible,
            "Main window kept its own caption buttons or lost its header in the system fallback.");
        Require(Win32Properties.GetWindowCornerPreference(owner) == WindowChrome.CornerPreference(OperatingSystem.IsWindows()),
            "Main window has the wrong corner request for this platform in the system fallback.");
        var key = new MeowChan.Favorites.FavoriteAssetKey(new ResourceId("4chan", "g", "123"), "chrome", "original", new string('a', 64));
        foreach ((string name, Func<Window> create) in new (string, Func<Window>)[]
        {
            ("about", () => new AboutWindow()),
            ("ratings-manager", () => new MediaRatingsManagerDialog(owner.SessionHost)),
            ("rating", () => new MediaRatingDialog(key, null, _ => Task.CompletedTask))
        })
        {
            Window window = create();
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            try
            {
                Task shown = window.ShowDialog(owner);
                using MediaNativeInput input = await StageFallbackAsync(window);
                WindowChromeBar bar = window.GetVisualDescendants().OfType<WindowChromeBar>().Single();
                Control content = ((Grid)bar.Parent!).Children.First(child => Grid.GetRow(child) == 1);
                Require(window.WindowDecorations == WindowDecorations.Full && !window.ExtendClientAreaToDecorationsHint
                    && !bar.IsVisible && content.TranslatePoint(default, window) is Point origin && Math.Abs(origin.Y) <= 1,
                    name + " left the app caption or an empty band in the system fallback.");
                Require(Win32Properties.GetWindowCornerPreference(window) == WindowChrome.CornerPreference(OperatingSystem.IsWindows()),
                    name + " has the wrong corner request for this platform in the system fallback.");
                LogDrawnDecorations(window, name + "-system");
                Program.CaptureWindow(window, $"smoke-chrome-system-{name}.png");
                await input.KeyAsync(MediaNativeInput.Escape);
                await Program.WaitUntilAsync(() => closed);
                await shown;
                Console.WriteLine($"SMOKE {name} system fallback: platform caption, app caption collapsed, native Escape closed it.");
            }
            finally { if (!closed) window.Close(); }
        }
        owner.Activate();
    }

    private static async Task<MediaNativeInput> StageFallbackAsync(Window window)
    {
        using var native = new NativeFocusProbe();
        await Program.WaitUntilAsync(() =>
        {
            window.UpdateLayout();
            return window.IsVisible && window.ClientSize.Width > 0 && native.IsViewable(window);
        });
        var input = new MediaNativeInput(window);
        window.Activate();
        await Program.WaitUntilAsync(() => window.IsActive);
        return input;
    }

    // Modal list dialog on the dialog factory (modals plan MD2): Dialog variant, no minimize; Escape keeps closing it.
    private static async Task CheckRatingsManagerAsync(MainWindow owner)
    {
        var dialog = new MediaRatingsManagerDialog(owner.SessionHost);
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            Task shown = dialog.ShowDialog(owner);
            using MediaNativeInput input = await StageDialogAsync(dialog, "ratings-manager");
            await CheckCaptionAsync(dialog, input, "ratings-manager", WindowChromeKind.Dialog);
            await input.KeyAsync(MediaNativeInput.Escape);
            await Program.WaitUntilAsync(() => closed);
            await shown;
            await WaitActiveAsync(owner, "ratings-manager-owner-return");
            Console.WriteLine("SMOKE ratings-manager caption: Dialog variant (no minimize); native Escape closed the modal; owner active again.");
        }
        finally { if (!closed) dialog.Close(); }
    }

    // Small modal prompt, Dialog variant: no minimize, maximize/restore/double-click, Escape closes.
    private static async Task CheckRatingPromptAsync(MainWindow owner)
    {
        var key = new MeowChan.Favorites.FavoriteAssetKey(new ResourceId("4chan", "g", "123"), "chrome", "original", new string('a', 64));
        int commits = 0;
        var dialog = new MediaRatingDialog(key, null, _ => { commits++; return Task.CompletedTask; });
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            Task shown = dialog.ShowDialog(owner);
            using MediaNativeInput input = await StageDialogAsync(dialog, "rating");
            await CheckCaptionAsync(dialog, input, "rating", WindowChromeKind.Dialog);
            await input.KeyAsync(MediaNativeInput.Escape);
            await Program.WaitUntilAsync(() => closed);
            await shown;
            Require(commits == 0, "Closing the rating prompt committed a rating.");
            await WaitActiveAsync(owner, "rating-owner-return");
            Console.WriteLine("SMOKE rating caption: Dialog variant without minimize; native Escape closed it without a commit.");
        }
        finally { if (!closed) dialog.Close(); }
    }

    // Modeless owned window (Show, not ShowDialog): caption close ends it, the owner stays usable.
    private static async Task CheckModelessBookmarksAsync(MainWindow owner)
    {
        var dialog = new PostBookmarksDialog(owner.SessionHost, (_, _) => Task.FromResult(false));
        bool closed = false;
        dialog.Closed += (_, _) => closed = true;
        try
        {
            dialog.Show(owner);
            Require(owner.IsEnabled, "A modeless bookmark list disabled its owner.");
            using MediaNativeInput input = await StageDialogAsync(dialog, "bookmarks");
            await CheckCaptionAsync(dialog, input, "bookmarks");
            await input.ClickAsync(Caption(dialog, "WindowCloseButton"));
            await Program.WaitUntilAsync(() => closed);
            Console.WriteLine("SMOKE bookmarks caption: modeless Window variant; native caption click closed it.");
        }
        finally { if (!closed) dialog.Close(); }
    }

    // Ownerless window shown before MainWindow in production: minimize keeps a taskbar return path.
    private static async Task CheckOwnerlessStartupErrorAsync(MainWindow owner)
    {
        var window = new StartupErrorWindow("window chrome fixture");
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        try
        {
            window.Show();
            using MediaNativeInput input = await StageDialogAsync(window, "startup-error");
            await CheckCaptionAsync(window, input, "startup-error");
            await input.KeyAsync(MediaNativeInput.Escape); // the dialog Escape policy closes it, unchanged by the caption
            await Program.WaitUntilAsync(() => closed);
            Console.WriteLine("SMOKE startup-error caption: ownerless Window variant; native Escape closed it through the dialog policy.");
        }
        finally { if (!closed) window.Close(); }
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
        Exception? failureBeforeCleanup = null;
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
        catch (Exception thrown) { failureBeforeCleanup = thrown; throw; }
        finally
        {
            await SmokeCleanup.RunAsync(failureBeforeCleanup, async () =>
            {
                pending.TrySetResult(Array.Empty<IStorageFolder>());
                await Program.WaitUntilAsync(() => !settings.Editor.IsBusy);
                if (!closed) settings.Close();
            }, exception => Console.Error.WriteLine("SMOKE cleanup failed in SecondaryWindowChromeChecks: " + exception));
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
        // Native X11 geometry of the window as the server holds it, for timeout diagnostics only.
        internal static string Geometry(Window window)
        {
            using var native = new NativeFocusProbe();
            nint handle = window.TryGetPlatformHandle()?.Handle ?? 0;
            if (native._display == 0 || handle == 0 || XGetWindowAttributes(native._display, handle, out XWindowAttributes a) == 0)
                return "X11 geometry unavailable";
            return $"X11 geometry={a.Width}x{a.Height} at {a.X},{a.Y}, mapState={a.MapState}";
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

    private static async Task CheckCaptionAsync(Window window, MediaNativeInput input, string name,
        WindowChromeKind kind = WindowChromeKind.Window)
    {
        Require(window.WindowDecorations == WindowDecorations.BorderOnly && window.ExtendClientAreaToDecorationsHint
            && Math.Abs(window.ExtendClientAreaTitleBarHeightHint - WindowChrome.TitleBarHeight) < 0.01,
            name + " still requests an OS titlebar.");
        Require(Win32Properties.GetWindowCornerPreference(window) == WindowChrome.CornerPreference(OperatingSystem.IsWindows()),
            name + " has the wrong corner request for this platform.");
        WindowChromeBar caption = window.GetVisualDescendants().OfType<WindowChromeBar>().Single();
        Require(caption.Kind == kind && caption.IsEffectivelyVisible && Math.Abs(caption.Bounds.Height - WindowChrome.TitleBarHeight) <= 1
            && caption.TranslatePoint(default, window) is Point origin && Math.Abs(origin.Y) <= 1 && Math.Abs(origin.X) <= 1
            && Math.Abs(caption.Bounds.Width - window.ClientSize.Width) <= 1, name + " caption is not the full-width first row.");
        Grid dragRegion = caption.GetVisualDescendants().OfType<Grid>()
            .Single(grid => WindowDecorationProperties.GetElementRole(grid) == WindowDecorationsElementRole.User);
        Require(dragRegion.Background != null && dragRegion.IsHitTestVisible, name + " caption has no hit-testable drag region.");
        Button minimize = Caption(window, "WindowMinimizeButton");
        Button maximize = Caption(window, "WindowMaximizeButton");
        Button close = Caption(window, "WindowCloseButton");
        bool offersMinimize = kind == WindowChromeKind.Window;
        Require(minimize.IsVisible == offersMinimize && maximize.IsVisible && close.IsVisible,
            name + $" caption variant {kind} shows the wrong buttons.");
        foreach (Button button in offersMinimize ? new[] { minimize, maximize, close } : new[] { maximize, close })
        {
            await ReachableAsync(window, button);
            Require(WindowDecorationProperties.GetElementRole(button) != WindowDecorationsElementRole.None,
                name + " caption button lost its native hit-test role: " + button.Name);
        }
        LogDrawnDecorations(window, name);
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
        window.CanResize = false;
        Require(!maximize.IsVisible, name + " offers maximize on a fixed-size window.");
        window.CanResize = true;
        Require(maximize.IsVisible, name + " did not restore maximize with resizing.");
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
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Maximized, () => WindowObservation(window));
        await Task.Delay(doubleTap + TimeSpan.FromMilliseconds(100));
        await input.ClickAsync(title, new Point(12, 10));
        await input.ClickAsync(title, new Point(12, 10));
        await WaitNormalAsync(window, normal);
        if (!offersMinimize)
        {
            await ReachableAsync(window, close);
            Console.WriteLine($"SMOKE {name} caption backend: {input.Backend}; Dialog variant, minimize not offered; drag/snap/Alt+F4 not exercised.");
            return;
        }
        await input.ClickAsync(minimize);
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Minimized, () => WindowObservation(window));
        Require(window.IsVisible && window.ShowInTaskbar, "Secondary minimize lost its taskbar return path.");
        // Like existing main-caption smoke: programmatic unminimize is NOT a
        // native taskbar click, and is deliberately described as such.
        window.WindowState = WindowState.Normal;
        window.Activate();
        await Program.WaitUntilAsync(() => window.IsActive && window.WindowState == WindowState.Normal);
        await ReachableAsync(window, close);
        Console.WriteLine($"SMOKE {name} caption backend: {input.Backend}; minimize return requested programmatically; drag/snap/Alt+F4 not exercised.");
    }

    // Linux evidence only: Avalonia draws frame/shadow/resize grips around BorderOnly windows
    // on X11. Record their corner radii; the owner decides on Windows/macOS natively.
    private static void LogDrawnDecorations(Window window, string name)
    {
        Visual? host = window.GetVisualParent();
        string[] corners = host == null ? Array.Empty<string>() : host.GetVisualDescendants()
            .Where(visual => !ReferenceEquals(visual, window) && !visual.GetVisualAncestors().Contains(window))
            .Select(visual => visual switch
            {
                Border border => (visual.GetType().Name, border.CornerRadius),
                Avalonia.Controls.Primitives.TemplatedControl control => (visual.GetType().Name, control.CornerRadius),
                _ => (visual.GetType().Name, default(CornerRadius))
            })
            .Where(entry => entry.Item2 != default)
            .Select(entry => entry.Item1 + "=" + entry.Item2).Distinct().ToArray();
        Console.WriteLine($"SMOKE {name} frame: host={host?.GetType().Name ?? "none"}; margin={window.WindowDecorationMargin}; "
            + $"corner={Win32Properties.GetWindowCornerPreference(window)}; drawnCornerRadii=[{string.Join(", ", corners)}]");
    }

    private static Task WaitNormalAsync(Window window, Size size) => Program.WaitUntilAsync(() =>
        window.WindowState == WindowState.Normal && Math.Abs(window.ClientSize.Width - size.Width) <= 1
        && Math.Abs(window.ClientSize.Height - size.Height) <= 1,
        () => WindowObservation(window) + $", expected={size}");

    private static string WindowObservation(Window window) =>
        $"{window.GetType().Name} state={window.WindowState}, client={window.ClientSize}, position={window.Position}, active={window.IsActive}, {NativeFocusProbe.Geometry(window)}";

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
