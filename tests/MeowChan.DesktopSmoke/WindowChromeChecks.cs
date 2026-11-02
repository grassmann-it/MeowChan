using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using System.Text.Json;
using MeowChan.Desktop.Localization;
using System.Runtime.InteropServices;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static class WindowChromeChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        Require(window.WindowDecorations == WindowDecorations.BorderOnly,
            "The shell still requests an OS title bar.");
        PrepareIsolatedBackdrop();
        Size entryBounds = window.Bounds.Size;
        // Reader fixtures can request a canvas larger than a hosted native
        // desktop. Start the caption resize probe from a size with room for its
        // subsequent +17/+13 request rather than testing native desktop clamps.
        window.WindowState = WindowState.Normal;
        await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal);
        Avalonia.Platform.Screen screen = window.Screens.ScreenFromWindow(window)
            ?? throw new InvalidOperationException("Caption resize probe has no native screen.");
        Size client = window.Bounds.Size;
        Size frame = window.FrameSize ?? client;
        Size desired = new(850, 530);
        PixelRect reservedArea = new(screen.WorkingArea.X, screen.WorkingArea.Y,
            screen.WorkingArea.Width - (int)Math.Ceiling(17 * window.RenderScaling),
            screen.WorkingArea.Height - (int)Math.Ceiling(13 * window.RenderScaling));
        WindowStartupFit baseline = WindowStartupGeometry.Fit(desired,
            new Size(desired.Width + Math.Max(0, frame.Width - client.Width),
                desired.Height + Math.Max(0, frame.Height - client.Height)),
            new Size(window.MinWidth, window.MinHeight), reservedArea, window.RenderScaling, window.Position);
        window.Width = baseline.Client.Width;
        window.Height = baseline.Client.Height;
        window.Position = baseline.Position;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, baseline.Client.Width, baseline.Client.Height));
        await Program.WaitUntilAsync(() => window.FrameSize is Size fittedFrame
            && (fittedFrame.Width + 17) * window.RenderScaling <= screen.WorkingArea.Width + 1
            && (fittedFrame.Height + 13) * window.RenderScaling <= screen.WorkingArea.Height + 1);
        Console.WriteLine($"SMOKE caption baseline: requested={baseline.Client}; actual={window.Bounds.Size}; frame={window.FrameSize}; area={screen.WorkingArea}; growth-reserve=17x13.");
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
            await CheckMaximizedClientEdgesAsync(window, window.FindControl<Grid>("WorkspaceRoot")!, "main");
            await CheckReachableAsync(window);
            Click(maximize);
            await Program.WaitUntilAsync(() => window.WindowState == WindowState.Normal
                && AutomationProperties.GetName(maximize) == Texts.T("window.maximize"));
            await WaitForNormalBoundsAsync(window, originalBounds);
            await CheckReachableAsync(window);
            DoublePressHeader(window);
            await Program.WaitUntilAsync(() => window.WindowState == WindowState.Maximized);
            await WaitForMaximizedBoundsAsync(window);
            await CheckMaximizedClientEdgesAsync(window, window.FindControl<Grid>("WorkspaceRoot")!, "main");
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
        window.Width = entryBounds.Width;
        window.Height = entryBounds.Height;
        await Program.WaitUntilAsync(() => PresentationChecks.MatchesRequestedSize(window, entryBounds.Width, entryBounds.Height));
        Console.WriteLine("SMOKE custom caption: no OS title bar; maximize/restore labels, header double-click and minimize/taskbar roundtrip passed.");
    }

    private static Task WaitForNormalBoundsAsync(MainWindow window, Size expected) => Program.WaitUntilAsync(() =>
        Math.Abs(window.Bounds.Width - expected.Width) <= 1 && Math.Abs(window.Bounds.Height - expected.Height) <= 1);

    private static Task WaitForMaximizedBoundsAsync(MainWindow window) => Program.WaitUntilAsync(() =>
    {
        if (OperatingSystem.IsMacOS()) return MacMaximizedBoundsMatch(window);
        Avalonia.Platform.Screen? screen = window.Screens.ScreenFromWindow(window);
        return screen != null && Math.Abs(window.Bounds.Width * window.RenderScaling - screen.WorkingArea.Width) <= 2
            && Math.Abs(window.Bounds.Height * window.RenderScaling - screen.WorkingArea.Height) <= 2;
    });

    // Native rectangle reads + a separately labelled renderer observation. No resizing
    // is performed here; a stable 1px uncovered right edge is a failure, not tolerance.
    internal static async Task CheckMaximizedClientEdgesAsync(Window window, Control root, string evidenceName)
    {
        if (OperatingSystem.IsMacOS())
        {
            Console.WriteLine("SMOKE maximized client-edge probe NOT COVERED on macOS; existing AppKit maximize probe is separate.");
            return;
        }
        Require(OperatingSystem.IsWindows() || OperatingSystem.IsLinux(), "Unsupported native client-edge backend.");
        ChromeSnapshot? previous = null;
        ChromeSnapshot? stable = null;
        int equalSamples = 0;
        try
        {
            await Program.WaitUntilAsync(() =>
            {
                window.UpdateLayout();
                if (window.WindowState != WindowState.Maximized) { equalSamples = 0; return false; }
                ChromeSnapshot current = ObserveClient(window, root);
                equalSamples = current == previous ? equalSamples + 1 : 0;
                previous = current;
                if (equalSamples < 10) return false; // >=500ms unchanged, not one state acknowledgement.
                stable = current;
                return true;
            });
            ChromeSnapshot sample = stable!;
            PixelRect nativeVisible = sample.NativeClient.Intersect(sample.WorkingArea);
            PixelRect rootVisible = sample.RootScreen.Intersect(sample.WorkingArea);
            Console.WriteLine("SMOKE maximized native/client-root evidence: " + JsonSerializer.Serialize(sample));
            // Windows may retain off-screen resize borders. Intersect measured
            // rectangles, never guess their width or impose monitor-sized requests.
            Require(nativeVisible == sample.WorkingArea,
                $"Stable native client does not cover WorkingArea: client={sample.NativeClient}; area={sample.WorkingArea}; rightGap={sample.WorkingArea.Right - sample.NativeClient.Right}px.");
            Require(rootVisible == sample.WorkingArea,
                $"Stable root does not cover WorkingArea: root={sample.RootScreen}; area={sample.WorkingArea}; rightGap={sample.WorkingArea.Right - sample.RootScreen.Right}px; decorations={sample.DecorationMargin}.");
            Require(root is Panel { Background: ISolidColorBrush brush } && brush.Color.A == 255 && brush.Opacity == 1,
                "Maximized client root has no opaque theme background.");
            CheckRendererRightEdge(window, evidenceName);
            await CheckIsolatedDesktopRightEdgeAsync(sample.WorkingArea, evidenceName);
        }
        finally
        {
            // Diagnostic metadata is retained even when geometry/pixel assertions fail.
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"smoke-chrome-{evidenceName}-maximized.json"),
                JsonSerializer.Serialize(new { observer = "owned native client rectangle + live Avalonia root geometry",
                    last = previous, stable, rendererCapture = "RenderTargetBitmap, NOT compositor/OS screenshot",
                    compositorRightEdge = "not observed by this probe" }, new JsonSerializerOptions { WriteIndented = true }));
            Program.CaptureWindow(window, $"smoke-chrome-{evidenceName}-maximized.png");
        }
    }

    private sealed record ChromeSnapshot(PixelRect NativeClient, PixelRect WorkingArea, PixelRect RootScreen,
        Rect RootBounds, Size ClientDip, Size WindowDip, double Scaling, Thickness DecorationMargin, Thickness OffScreenMargin);

    private static ChromeSnapshot ObserveClient(Window window, Control root)
    {
        var screen = window.Screens.ScreenFromWindow(window)
            ?? throw new InvalidOperationException("Maximized probe has no screen.");
        PixelPoint start = root.PointToScreen(default);
        PixelPoint end = root.PointToScreen(new Point(root.Bounds.Width, root.Bounds.Height));
        return new(ReadNativeClient(window), screen.WorkingArea,
            new PixelRect(start, new PixelSize(Math.Max(0, end.X - start.X), Math.Max(0, end.Y - start.Y))),
            root.Bounds, window.ClientSize, window.Bounds.Size, window.RenderScaling,
            window.WindowDecorationMargin, window.OffScreenMargin);
    }

    private static PixelRect ReadNativeClient(Window window)
    {
        var handle = window.TryGetPlatformHandle() ?? throw new InvalidOperationException("Window has no native handle.");
        if (OperatingSystem.IsWindows())
        {
            Require(handle.HandleDescriptor == "HWND", "Expected Win32 HWND for native client read.");
            Require(GetClientRect(handle.Handle, out WinRect rect), "GetClientRect failed.");
            WinPoint origin = new() { X = rect.Left, Y = rect.Top };
            Require(ClientToScreen(handle.Handle, ref origin), "ClientToScreen failed.");
            return new PixelRect(origin.X, origin.Y, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }
        Require(handle.HandleDescriptor == "XID"
            && Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1",
            "Native Linux client read requires the isolated X11/XWayland XID; no Wayland/personal-display fallback.");
        nint display = XOpenDisplay(null);
        Require(display != 0, "Cannot open isolated X11 display.");
        try
        {
            Require(XGetGeometry(display, handle.Handle, out nint root, out _, out _, out uint width, out uint height, out _, out _) != 0,
                "XGetGeometry failed.");
            Require(XTranslateCoordinates(display, handle.Handle, root, 0, 0, out int x, out int y, out _) != 0,
                "XTranslateCoordinates failed.");
            return new PixelRect(x, y, checked((int)width), checked((int)height));
        }
        finally { XCloseDisplay(display); }
    }

    private static void CheckRendererRightEdge(Window window, string evidenceName)
    {
        double scale = window.RenderScaling;
        PixelSize size = PixelSize.FromSize(window.Bounds.Size, scale);
        Require(size.Width is > 0 and <= 16384 && size.Height is > 0 and <= 16384, "Client-edge raster is unbounded.");
        using RenderTargetBitmap render = new(size, new Vector(96 * scale, 96 * scale));
        render.Render(window);
        using MemoryStream png = new();
        render.Save(png, PngBitmapEncoderOptions.Default);
        using SKBitmap pixels = SKBitmap.Decode(png.ToArray()) ?? throw new InvalidDataException("Client-edge raster unavailable.");
        for (int y = 0; y < pixels.Height; y++)
            Require(pixels.GetPixel(pixels.Width - 1, y).Alpha == 255,
                $"{evidenceName}: renderer right edge is not opaque at {pixels.Width - 1},{y}; this is not OS compositor evidence.");
        Console.WriteLine($"SMOKE renderer-only right edge opaque: {evidenceName}, {pixels.Width}x{pixels.Height}; native geometry observed separately; compositor pixels NOT checked.");
    }

    // This affects only the root of our private Xvfb display, never a user desktop.
    private static void PrepareIsolatedBackdrop()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1") return;
        nint display = XOpenDisplay(null);
        Require(display != 0, "Private backdrop display unavailable.");
        try
        {
            nint root = XDefaultRootWindow(display);
            XSetWindowBackground(display, root, 0xff00ff);
            XClearWindow(display, root);
            XSync(display, false);
        }
        finally { XCloseDisplay(display); }
    }

    private static async Task CheckIsolatedDesktopRightEdgeAsync(PixelRect area, string name)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1") return;
        // import reads X11 root pixels (not RenderTargetBitmap). Let the real
        // software renderer publish before capturing this isolated desktop.
        await Task.Delay(250);
        string path = Path.Combine(AppContext.BaseDirectory, "smoke-chrome-" + name + "-desktop.png");
        var start = new ProcessStartInfo("/usr/bin/import") { UseShellExecute = false };
        foreach (string arg in new[] { "-window", "root", path }) start.ArgumentList.Add(arg);
        using Process capture = Process.Start(start) ?? throw new InvalidOperationException("Private desktop capture unavailable.");
        await capture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Require(capture.ExitCode == 0, "Private desktop capture failed.");
        using SKBitmap pixels = SKBitmap.Decode(path) ?? throw new InvalidDataException("Private X11 desktop image invalid.");
        Require(area.X >= 0 && area.Y >= 0 && area.Right <= pixels.Width && area.Bottom <= pixels.Height,
            "Measured WorkingArea is outside captured private desktop.");
        for (int y = area.Y; y < area.Bottom; y++)
        {
            SKColor pixel = pixels.GetPixel(area.Right - 1, y);
            Require(pixel.Red != 255 || pixel.Green != 0 || pixel.Blue != 255,
                $"{name}: private desktop backdrop visible at maximized right edge {area.Right - 1},{y}.");
        }
        Console.WriteLine($"SMOKE isolated X11 desktop capture: {name}, WorkingArea right column contains no magenta backdrop; actual Xvfb pixels, not native Windows/Wayland evidence.");
    }
    [DllImport("libX11.so.6")] private static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern int XSetWindowBackground(nint display, nint window, nuint color);
    [DllImport("libX11.so.6")] private static extern int XClearWindow(nint display, nint window);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);

    [StructLayout(LayoutKind.Sequential)] private struct WinRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct WinPoint { public int X, Y; }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(nint window, out WinRect rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref WinPoint point);
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern int XGetGeometry(nint display, nint window, out nint root,
        out int x, out int y, out uint width, out uint height, out uint borderWidth, out uint depth);
    [DllImport("libX11.so.6")] private static extern int XTranslateCoordinates(nint display, nint source, nint destination,
        int x, int y, out int targetX, out int targetY, out nint child);

    internal static bool MacMaximizedBoundsMatch(MainWindow window)
    {
        // Avalonia.Native 12.1.4 DoZoom sets NSWindow.frame to its NSScreen's
        // current visibleFrame. Screens.WorkingArea is a separate cached snapshot
        // and can lag menu/Dock changes on the hosted desktop. Compare those same
        // live AppKit rectangles, including position, rather than widening the
        // geometry tolerance to accommodate a stale screen snapshot.
        var handle = window.TryGetPlatformHandle();
        Require(handle?.HandleDescriptor == "NSWindow" && handle.Handle != IntPtr.Zero,
            "The macOS caption fixture has no NSWindow handle.");
        IntPtr nativeWindow = handle!.Handle;
        IntPtr screen = SendObject(nativeWindow, Selector("screen"));
        if (screen == IntPtr.Zero) return false;
        NativeRect frame = ReadRect(nativeWindow, "frame");
        NativeRect available = ReadRect(screen, "visibleFrame");
        bool matches = Math.Abs(frame.X - available.X) <= 1
            && Math.Abs(frame.Y - available.Y) <= 1
            && Math.Abs(frame.Width - available.Width) <= 1
            && Math.Abs(frame.Height - available.Height) <= 1
            && Math.Abs(window.Bounds.Width - frame.Width) <= 1
            && Math.Abs(window.Bounds.Height - frame.Height) <= 1;
        if (matches)
            Console.WriteLine($"SMOKE native macOS maximize: frame={frame}; visibleFrame={available}; cachedWorkingArea={window.Screens.ScreenFromWindow(window)?.WorkingArea}.");
        return matches;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public double X, Y, Width, Height;
        public override readonly string ToString() => $"{X},{Y},{Width},{Height}";
    }

    private static NativeRect ReadRect(IntPtr receiver, string selector)
    {
        // NSRect is a 32-byte return value: Intel uses objc_msgSend_stret,
        // while Apple Silicon returns it through the ordinary message ABI.
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            SendRectIntel(out NativeRect result, receiver, Selector(selector));
            return result;
        }
        return SendRectArm(receiver, Selector(selector));
    }

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern IntPtr Selector(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendObject(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern NativeRect SendRectArm(IntPtr receiver, IntPtr selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend_stret")]
    private static extern void SendRectIntel(out NativeRect result, IntPtr receiver, IntPtr selector);

    private static void DoublePressHeader(MainWindow window)
    {
        // The logo is now an About button. Exercise caption double-click on the
        // noninteractive title text, as a user would without activating About.
        Control title = window.FindControl<Grid>("WorkspaceTitleBar")!.GetVisualDescendants()
            .OfType<TextBlock>().Single(text => text.Classes.Contains("brand"));
        Point point = title.TranslatePoint(new Point(15, 10), window)!.Value;
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        title.RaiseEvent(new PointerPressedEventArgs(title, pointer, window, point, 0,
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
