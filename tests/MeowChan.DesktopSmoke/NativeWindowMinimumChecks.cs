using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

/// <summary>Native WM interactive resize through EWMH and real XTest pointer events.
/// This is deliberately separate from programmatic Width/Height checks.</summary>
internal static class NativeWindowMinimumChecks
{
    internal static async Task RunAsync(MainWindow window)
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1")
            throw new InvalidOperationException("Native resize probes require the runner's isolated Linux session.");
        IPlatformHandle handle = window.TryGetPlatformHandle() ?? throw new InvalidOperationException("No native handle.");
        if (handle.HandleDescriptor != "XID") throw new InvalidOperationException("This probe requires a proven X11/XWayland XID, got " + handle.HandleDescriptor);
        nint display = XOpenDisplay(null);
        if (display == 0) throw new InvalidOperationException("Cannot open the isolated X display.");
        string? nestedDisplay = Environment.GetEnvironmentVariable("MEOWCHAN_KWIN_INPUT_DISPLAY");
        string? previousAuthority = Environment.GetEnvironmentVariable("XAUTHORITY");
        nint inputDisplay;
        try
        {
            if (nestedDisplay != null) Environment.SetEnvironmentVariable("XAUTHORITY", Environment.GetEnvironmentVariable("MEOWCHAN_KWIN_INPUT_XAUTHORITY"));
            inputDisplay = nestedDisplay == null ? display : XOpenDisplay(nestedDisplay);
        }
        finally { Environment.SetEnvironmentVariable("XAUTHORITY", previousAuthority); }
        if (inputDisplay == 0) throw new InvalidOperationException("Cannot open the private nested input display.");
        try
        {
            nint root = XDefaultRootWindow(display);
            nint inputRoot = XDefaultRootWindow(inputDisplay);
            Console.WriteLine("SMOKE native input route: " + (nestedDisplay == null ? "same isolated X display" : "private nested Xvfb " + nestedDisplay));
            Console.WriteLine($"SMOKE native resize backend: handle={handle.HandleDescriptor}; xid={handle.Handle}; platform={window.PlatformImpl?.GetType().FullName}; app={typeof(MainWindow).Assembly.FullName}; runtime={Environment.Version};");
            window.SizeChanged += (_, _) => Log(window, "SizeChanged");
            window.PropertyChanged += (_, args) =>
            {
                if (args.Property == Window.ClientSizeProperty || args.Property == Window.FrameSizeProperty
                    || args.Property == Window.MinWidthProperty || args.Property == Window.MinHeightProperty)
                    Log(window, args.Property.Name);
            };
            foreach (string mode in new[] { "Barebones", "Simple", "Advanced" })
            {
                await PresentationChecks.SwitchModeAsync(window, mode);
                foreach (int steps in new[] { 1, 12 })
                foreach (int direction in Enumerable.Range(0, 8))
                {
                    window.WindowState = WindowState.Normal;
                    window.Width = window.MinWidth + 160;
                    window.Height = window.MinHeight + 160;
                    window.Position = new PixelPoint(120, 120);
                    await Program.WaitUntilAsync(() => window.ClientSize.Width >= window.MinWidth + 150 && window.ClientSize.Height >= window.MinHeight + 150);
                    Size initial = window.ClientSize;
                    await NativeHintsAsync(handle);
                    XTranslateCoordinates(display, handle.Handle, root, 0, 0, out int left, out int top, out _);
                    XGetGeometry(display, handle.Handle, out _, out _, out _, out uint nativeWidth, out uint nativeHeight, out _, out _);
                    int width = (int)nativeWidth;
                    int height = (int)nativeHeight;
                    bool west = direction is 0 or 6 or 7;
                    bool east = direction is 2 or 3 or 4;
                    bool north = direction is 0 or 1 or 2;
                    bool south = direction is 4 or 5 or 6;
                    // Start just outside the client surface. Otherwise Avalonia's
                    // resize grip can start a different edge gesture before this
                    // explicit EWMH request (notably a top-right corner on X11).
                    int startX = left + (west ? -1 : east ? width : width / 2);
                    int startY = top + (north ? -1 : south ? height : height / 2);
                    int moveX = (int)Math.Round((initial.Width - 300) * window.RenderScaling) * (west ? 1 : east ? -1 : 0);
                    int moveY = (int)Math.Round((initial.Height - 200) * window.RenderScaling) * (north ? 1 : south ? -1 : 0);
                    XTestFakeMotionEvent(inputDisplay, -1, startX, startY, 0);
                    XTestFakeButtonEvent(inputDisplay, 1, true, 0);
                    XFlush(inputDisplay);
                    // XWayland routes emulated input through the compositor. Wait
                    // for the actual button state before sending EWMH resize.
                    try { await Program.WaitUntilAsync(() =>
                    {
                        XQueryPointer(inputDisplay, inputRoot, out _, out _, out int x, out int y, out _, out _, out uint mask);
                        return Math.Abs(x - startX) <= 1 && Math.Abs(y - startY) <= 1 && (mask & 256) != 0;
                    }); }
                    catch
                    {
                        XQueryPointer(inputDisplay, inputRoot, out _, out _, out int x, out int y, out _, out _, out uint mask);
                        Console.Error.WriteLine($"SMOKE native input unavailable: actual=({x},{y}), mask={mask}; expected=({startX},{startY}), Button1Mask=256.");
                        Program.CaptureWindow(window, "smoke-native-input-unavailable.png");
                        throw;
                    }
                    XClientMessage message = new() { Type = 33, Display = display, Window = handle.Handle,
                        MessageType = XInternAtom(display, "_NET_WM_MOVERESIZE", false), Format = 32,
                        Data0 = startX, Data1 = startY, Data2 = direction, Data3 = 1, Data4 = 1 };
                    if (XSendEvent(display, root, false, (1 << 20) | (1 << 19), ref message) == 0)
                        throw new InvalidOperationException("WM rejected the interactive resize request.");
                    XFlush(display);
                    await Task.Delay(100);
                    for (int step = 1; step <= steps; step++)
                    {
                        XTestFakeMotionEvent(inputDisplay, -1, startX + moveX * step / steps, startY + moveY * step / steps, 0);
                        XFlush(inputDisplay);
                        await Task.Delay(steps == 1 ? 40 : 25);
                    }
                    XTestFakeButtonEvent(inputDisplay, 1, false, 0);
                    XFlush(inputDisplay);
                    await Program.WaitUntilAsync(() =>
                    {
                        XQueryPointer(inputDisplay, inputRoot, out _, out _, out _, out _, out _, out _, out uint mask);
                        return (mask & 256) == 0;
                    });
                    Log(window, $"released mode={mode} direction={direction} steps={steps}");
                    await Task.Delay(300);
                    window.UpdateLayout();
                    Log(window, $"settled mode={mode} direction={direction} steps={steps}");
                    Size nominal = mode == "Barebones" ? new Size(640, 480) : new Size(760, 500);
                    Screen screen = window.Screens.ScreenFromWindow(window)!;
                    Size minimum = WindowStartupGeometry.Fit(window.ClientSize, window.FrameSize, nominal,
                        screen.WorkingArea, window.RenderScaling, window.Position).Minimum;
                    bool changed = (west || east ? initial.Width - window.ClientSize.Width >= 40 : true)
                        && (north || south ? initial.Height - window.ClientSize.Height >= 40 : true);
                    bool floor = window.ClientSize.Width >= minimum.Width - 1 && window.ClientSize.Height >= minimum.Height - 1;
                    if (!changed || !floor)
                    {
                        Program.CaptureWindow(window, $"smoke-native-minimum-failure-{mode}-{direction}-{steps}.png");
                        throw new InvalidOperationException($"Native resize changed={changed}, floor={floor}, initial={initial}, actual={window.ClientSize}, expected={minimum}; mode={mode}, direction={direction}, steps={steps}.");
                    }
                }
            }
        }
        finally
        {
            XTestFakeButtonEvent(inputDisplay, 1, false, 0); XFlush(inputDisplay);
            if (inputDisplay != display) XCloseDisplay(inputDisplay);
            XCloseDisplay(display);
        }
    }

    private static void Log(MainWindow window, string phase) => Console.WriteLine("SMOKE native geometry: " + JsonSerializer.Serialize(new
    {
        phase, time = DateTime.UtcNow.ToString("O"), requested = new[] { window.Width, window.Height },
        client = window.ClientSize.ToString(), frame = window.FrameSize?.ToString(), position = window.Position.ToString(),
        minimum = new[] { window.MinWidth, window.MinHeight }, window.RenderScaling,
        workArea = window.Screens.ScreenFromWindow(window)?.WorkingArea.ToString(), state = window.WindowState.ToString()
    }));

    private static async Task NativeHintsAsync(IPlatformHandle handle)
    {
        ProcessStartInfo info = new("xprop") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-id", handle.Handle.ToString(), "WM_NORMAL_HINTS", "_NET_FRAME_EXTENTS" }) info.ArgumentList.Add(arg);
        using Process process = Process.Start(info)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Console.WriteLine("SMOKE native size hints: " + output.Replace('\n', ';'));
    }

    [StructLayout(LayoutKind.Sequential, Size = 192)]
    private struct XClientMessage
    {
        public int Type;
        public nuint Serial;
        public int SendEvent;
        public nint Display;
        public nint Window;
        public nint MessageType;
        public int Format;
        public nint Data0, Data1, Data2, Data3, Data4;
    }
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern nint XInternAtom(nint display, string name, bool onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XSendEvent(nint display, nint window, bool propagate, nint mask, ref XClientMessage message);
    [DllImport("libX11.so.6")] private static extern int XTranslateCoordinates(nint display, nint source, nint destination, int x, int y, out int targetX, out int targetY, out nint child);
    [DllImport("libX11.so.6")] private static extern int XGetGeometry(nint display, nint window, out nint root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);
    [DllImport("libX11.so.6")] private static extern bool XQueryPointer(nint display, nint window, out nint root, out nint child,
        out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeButtonEvent(nint display, uint button, bool pressed, nuint delay);
}
