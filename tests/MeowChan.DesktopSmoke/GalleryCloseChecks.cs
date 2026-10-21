using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal static class GalleryCloseChecks
{
    internal static async Task RunAsync(MainWindow window, bool videoFixture, Action markChecksPassed, Func<bool> isClosed)
    {
        ResourceId id = new("4chan", "g", videoFixture ? "2000" : "1000");
        await window.OpenGalleryAsync(id);
        ContentControl host = window.FindControl<ContentControl>("GalleryHost")!;
        await Program.WaitUntilAsync(() => host.IsEffectivelyVisible && host.Content is GalleryViewer);
        GalleryViewer viewer = (GalleryViewer)host.Content!;
        if (videoFixture)
        {
            await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<Button>()
                .Any(button => button.Name == "GalleryVideoPause" && button.IsEffectivelyEnabled));
            await RequireNativePlaybackAsync(viewer);
        }
        else
            await Program.WaitUntilAsync(() => viewer.GetGalleryVisualDescendants().OfType<Image>()
                .Any(image => image.Name == "GalleryImage" && image.Source is Bitmap));

        bool maximizedOrigin = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_CLOSE_MAXIMIZED") == "1";
        if (maximizedOrigin)
        {
            window.WindowState = WindowState.Maximized;
            await Program.WaitUntilAsync(() => IsNativeMaximized(window));
        }
        Size normalOrigin = window.Bounds.Size;
        RevealGalleryChrome(viewer);
        if (videoFixture) RequireUncoveredVideoTransport(viewer);
        await CaptureNativeAsync(videoFixture ? "video-open" : "image-open");
        RevealGalleryChrome(viewer);

        Button fullscreen = viewer.GetGalleryVisualDescendants().OfType<Button>()
            .Single(button => button.Name == "GalleryFullscreen");
        Require(fullscreen.IsEffectivelyVisible && fullscreen.IsEffectivelyEnabled,
            "Gallery fullscreen control is not reachable.");
        WindowState expectedExit = maximizedOrigin ? WindowState.Maximized : WindowState.Normal;
        Exception? exitFailure = null;
        if (Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_CLOSE_SKIP_FULLSCREEN") != "1")
        {
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Program.WaitUntilAsync(() => IsNativeFullscreen(window) && host.IsEffectivelyVisible);
            Console.WriteLine($"SMOKE gallery close: before fullscreen exit state={window.WindowState}, client={window.ClientSize}, frame={window.FrameSize}, position={window.Position}.");
            fullscreen.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            try
            {
                await Program.WaitUntilAsync(() => host.IsEffectivelyVisible
                    && (maximizedOrigin ? IsNativeMaximized(window)
                        : window.WindowState == expectedExit
                            && Math.Abs(window.Bounds.Width - normalOrigin.Width) <= 2
                            && Math.Abs(window.Bounds.Height - normalOrigin.Height) <= 2));
            }
            catch (Exception error)
            {
                exitFailure = error;
                var screen = window.Screens.ScreenFromWindow(window);
                Console.WriteLine($"SMOKE gallery fullscreen exit failure: expected={expectedExit}, original-normal={normalOrigin}, "
                    + $"state={window.WindowState}, requested={window.Width}x{window.Height}, bounds={window.Bounds}, "
                    + $"client={window.ClientSize}, frame={window.FrameSize}, position={window.Position}, "
                    + $"screen={screen?.Bounds}, working-area={screen?.WorkingArea}, screens={window.Screens.All.Count}, "
                    + $"scale={window.RenderScaling}, gallery-visible={host.IsEffectivelyVisible}.");
                try { Program.CaptureWindow(window, "smoke-gallery-fullscreen-exit-failure.png"); }
                catch (Exception captureError) { Console.WriteLine("SMOKE gallery fullscreen exit capture failed: " + captureError); }
            }
            await Task.Delay(250);
            if (!(window.WindowState == expectedExit && host.IsEffectivelyVisible
                && (maximizedOrigin ? IsNativeMaximized(window)
                    : Math.Abs(window.Bounds.Width - normalOrigin.Width) <= 2
                        && Math.Abs(window.Bounds.Height - normalOrigin.Height) <= 2)))
                exitFailure ??= new InvalidOperationException("Gallery or window state did not survive fullscreen exit.");
        }
        else Console.WriteLine("SMOKE gallery close: fullscreen toggles skipped for native caption input control.");

        Button close = window.FindControl<Button>("WindowCloseButton")!;
        RevealGalleryChrome(viewer);
        await CaptureNativeAsync(videoFixture ? "video-exit" : "image-exit");
        if (videoFixture)
        {
            GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
            if ((await player.InspectPlaybackAsync()).State != "Playing")
                viewer.GetGalleryVisualDescendants().OfType<Button>().Single(button => button.Name == "GalleryVideoPlay")
                    .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await RequireNativePlaybackAsync(viewer);
            RequireUncoveredVideoTransport(viewer);
        }
        window.UpdateLayout();
        Point center = close.TranslatePoint(new Point(close.Bounds.Width / 2, close.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("Close caption has no window position.");
        Require(close.IsEffectivelyVisible && close.IsEffectivelyEnabled && close.Bounds.Width >= 20
            && close.Bounds.Height >= 20 && center.X >= 0 && center.Y >= 0
            && center.X < window.Bounds.Width && center.Y < window.Bounds.Height,
            "Close caption is not reachable after gallery fullscreen exit.");
        close.Click += (_, _) => Console.WriteLine("SMOKE gallery close: close-caption Button.Click observed.");
        Console.WriteLine($"SMOKE gallery close: media={(videoFixture ? "video" : "image")}, origin={(maximizedOrigin ? "maximized" : "normal")}, state={window.WindowState}, bounds={window.Bounds}, caption={center}, hit={window.InputHitTest(center)?.GetType().Name}.");
        if (exitFailure == null) markChecksPassed();
        if (OperatingSystem.IsLinux() && (Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1"
            || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_KWIN") == "1"))
            await ClickNativeX11Async(window, center);
        else
            ClickHitTest(window, center);
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (!isClosed())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Gallery fullscreen exit left the window visible after close-caption click.");
            await Task.Delay(50);
        }
        Console.WriteLine("SMOKE gallery close: native close-caption shutdown observed.");
        if (exitFailure != null)
            throw new InvalidOperationException("Close caption did close the window, but gallery fullscreen exit failed.", exitFailure);
        Console.WriteLine("SMOKE gallery fullscreen exit and close-caption shutdown passed.");
    }

    private static bool IsNativeFullscreen(MainWindow window) => window.WindowState == WindowState.FullScreen
        && window.Screens.ScreenFromWindow(window) is { } screen
        && Math.Abs(window.Bounds.Width * window.RenderScaling - screen.Bounds.Width) <= 2
        && Math.Abs(window.Bounds.Height * window.RenderScaling - screen.Bounds.Height) <= 2;

    private static void RevealGalleryChrome(GalleryViewer viewer) => viewer.RaiseEvent(
        new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.LeftShift });

    private static void RequireUncoveredVideoTransport(GalleryViewer viewer)
    {
        viewer.UpdateLayout();
        Border info = viewer.GetGalleryVisualDescendants().OfType<Border>()
            .Single(control => control.Name == "GalleryOverlayInfo");
        PixelPoint infoStart = info.PointToScreen(default);
        double infoScale = TopLevel.GetTopLevel(info)!.RenderScaling;
        foreach (Control transport in viewer.GetGalleryVisualDescendants().OfType<Control>()
            .Where(control => control.Name is "GalleryVideoSeek" or "GalleryVideoPlay" or "GalleryVideoPause" or "GalleryVideoStop"))
        {
            Require(transport.IsEffectivelyVisible && transport.Bounds.Width > 0 && transport.Bounds.Height > 0,
                "Video transport is not visible: " + transport.Name);
            PixelPoint start = transport.PointToScreen(default);
            double scale = TopLevel.GetTopLevel(transport)!.RenderScaling;
            bool overlaps = infoStart.X < start.X + transport.Bounds.Width * scale
                && infoStart.X + info.Bounds.Width * infoScale > start.X
                && infoStart.Y < start.Y + transport.Bounds.Height * scale
                && infoStart.Y + info.Bounds.Height * infoScale > start.Y;
            Require(!overlaps, "Gallery info covers video transport: " + transport.Name);
        }
    }

    private static async Task RequireNativePlaybackAsync(GalleryViewer viewer)
    {
        GalleryVideoPlayer player = viewer.GetGalleryVisualDescendants().OfType<GalleryVideoPlayer>().Single();
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        GalleryVideoPlayer.PlaybackEvidence evidence;
        do
        {
            evidence = await player.InspectPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));
            bool visibleSoftwareFrames = OperatingSystem.IsWindows()
                || evidence.SurfaceFrames >= 3 && evidence.SurfaceNonUniform;
            if (evidence.DecodedFrames >= 3 && evidence.DisplayedFrames >= 3
                && evidence.Time >= 250 && visibleSoftwareFrames) break;
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("Native gallery video did not decode/display frames and advance its clock: " + evidence);
            await Task.Delay(100);
        } while (true);
        long firstTime = evidence.Time;
        await Task.Delay(300);
        evidence = await player.InspectPlaybackAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Require(evidence.Time > firstTime, "Native gallery video clock did not keep advancing: " + evidence);
        Console.WriteLine("SMOKE gallery close: real native playback evidence=" + evidence);
    }

    private static async Task CaptureNativeAsync(string stage)
    {
        string? directory = Environment.GetEnvironmentVariable("MEOWCHAN_GALLERY_NATIVE_CAPTURE_DIR");
        if (string.IsNullOrEmpty(directory)) return;
        Require(OperatingSystem.IsLinux() && (Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") == "1"
            || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_KWIN") == "1"),
            "Native gallery capture requires a private test desktop.");
        Directory.CreateDirectory(directory);
        System.Diagnostics.ProcessStartInfo info = new("import") { UseShellExecute = false };
        info.ArgumentList.Add("-window"); info.ArgumentList.Add("root");
        info.ArgumentList.Add(Path.Combine(directory, "gallery-native-" + stage + ".png"));
        using System.Diagnostics.Process capture = System.Diagnostics.Process.Start(info)!;
        await capture.WaitForExitAsync();
        Require(capture.ExitCode == 0, "Private desktop native gallery capture failed.");
    }

    private static bool IsNativeMaximized(MainWindow window) => window.WindowState == WindowState.Maximized
        && window.Screens.ScreenFromWindow(window) is { } screen
        && Math.Abs(window.Bounds.Width * window.RenderScaling - screen.WorkingArea.Width) <= 2
        && Math.Abs(window.Bounds.Height * window.RenderScaling - screen.WorkingArea.Height) <= 2;

    private static async Task ClickNativeX11Async(MainWindow window, Point clientPoint)
    {
        // Only the smoke runner's private Xvfb or virtual KWin XWayland display
        // may receive synthetic server input. This crosses Avalonia's native hit-test path.
        PixelPoint root = window.PointToScreen(clientPoint);
        IntPtr display = XOpenDisplay(IntPtr.Zero);
        if (display == IntPtr.Zero) throw new InvalidOperationException("Private Xvfb display is unavailable.");
        try
        {
            Require(XTestFakeMotionEvent(display, -1, root.X, root.Y, 0) != 0,
                "XTest could not move to the native close caption.");
            XSync(display, false);
            // XWayland delivers XTest through an asynchronous libei session.
            // Keep its connection alive and allow authorization/motion to settle.
            await Task.Delay(300);
            Require(XTestFakeButtonEvent(display, 1, true, 0) != 0,
                "XTest could not press the native close caption.");
            XSync(display, false);
            await Task.Delay(80);
            Require(XTestFakeButtonEvent(display, 1, false, 0) != 0,
                "XTest could not release the native close caption.");
            XSync(display, false);
            await Task.Delay(150);
        }
        finally { XCloseDisplay(display); }
    }

    private static void ClickHitTest(MainWindow window, Point point)
    {
        InputElement hit = window.InputHitTest(point) as InputElement
            ?? throw new InvalidOperationException("Close caption has no pointer target.");
        using Pointer pointer = new(Pointer.GetNextFreeId(), PointerType.Mouse, true);
        hit.RaiseEvent(new PointerPressedEventArgs(hit, pointer, window, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        hit.RaiseEvent(new PointerReleasedEventArgs(hit, pointer, window, point, 1,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        pointer.Capture(null);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [DllImport("libX11.so.6")]
    private static extern IntPtr XOpenDisplay(IntPtr displayName);

    [DllImport("libX11.so.6")]
    private static extern int XCloseDisplay(IntPtr display);

    [DllImport("libX11.so.6")]
    private static extern int XSync(IntPtr display, [MarshalAs(UnmanagedType.Bool)] bool discard);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeMotionEvent(IntPtr display, int screen, int x, int y, ulong delay);

    [DllImport("libXtst.so.6")]
    private static extern int XTestFakeButtonEvent(IntPtr display, uint button, [MarshalAs(UnmanagedType.Bool)] bool press, ulong delay);
}
