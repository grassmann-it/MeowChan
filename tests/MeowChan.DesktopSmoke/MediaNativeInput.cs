using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.DesktopSmoke;

/// <summary>Test-only OS input. Never connects to a personal display or substitutes routed events.</summary>
internal sealed class MediaNativeInput : IDisposable
{
    private readonly nint _display;
    private readonly Window _owner;
    private int _x, _y;
    private readonly HashSet<uint> _heldKeys = new();
    private bool _buttonHeld;
    internal const ulong Left = 0xff51, Right = 0xff53, Tab = 0xff09, Escape = 0xff1b, Space = 0x20, ControlLeft = 0xffe3, ShiftLeft = 0xffe1, F6 = 0xffc3, Home = 0xff50, End = 0xff57, F7 = 0xffc4, F8 = 0xffc5;
    internal string Backend => OperatingSystem.IsWindows() ? "Windows Win32 SendInput; explicit disposable desktop"
        : OperatingSystem.IsMacOS() ? "macOS CGEvent; explicit disposable single-display desktop" : "X11 XTest (libXtst.so.6); private Xvfb; Linux software video";

    internal MediaNativeInput(Window owner)
    {
        _owner = owner;
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            if (Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_NATIVE_ALLOWED") != "1")
                throw new PlatformNotSupportedException("Native input requires explicit --allow-native-desktop on a disposable session.");
            if (OperatingSystem.IsMacOS() && (!CGPreflightPostEventAccess() || owner.Screens.All.Count != 1))
                throw new PlatformNotSupportedException("macOS CGEvent requires existing event-posting permission and one disposable display; no automatic permission grant or synthetic fallback.");
            owner.Activate();
            return;
        }
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("MEOWCHAN_DESKTOP_SMOKE_XVFB") != "1"
            || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new PlatformNotSupportedException("MEDIA INPUT UNSUPPORTED: requires run-desktop-smoke.py private Linux Xvfb; no Windows/macOS/Wayland or personal-display fallback.");
        _display = XOpenDisplay(0);
        if (_display == 0) throw new InvalidOperationException("MEDIA INPUT UNAVAILABLE: cannot open private X display.");
        try
        {
            if (XTestQueryExtension(_display, out _, out _, out _, out _) == 0)
                throw new PlatformNotSupportedException("MEDIA INPUT UNAVAILABLE: private X server lacks XTEST.");
            nint xid = owner.TryGetPlatformHandle()?.Handle ?? 0;
            if (xid == 0) throw new InvalidOperationException("Native smoke window has no XID.");
            XRaiseWindow(_display, xid);
            XSetInputFocus(_display, xid, 2, 0); // RevertToParent, CurrentTime; only this harness-owned window.
            Flush();
        }
        catch { XCloseDisplay(_display); throw; }
    }

    internal async Task MoveAsync(Control target, Point? point = null)
    {
        if (!target.IsEffectivelyVisible || target.Bounds.Width <= 0 || target.Bounds.Height <= 0)
            throw new InvalidOperationException("Native pointer target is not laid out/visible: " + target.Name);
        PixelPoint pixel = target.PointToScreen(point ?? new Point(target.Bounds.Width / 2, target.Bounds.Height / 2));
        if (_display == 0)
        {
            EnsureOwnActiveWindow();
            _x = pixel.X; _y = pixel.Y;
            if (OperatingSystem.IsWindows())
            { if (!SetCursorPos(_x, _y)) throw new InvalidOperationException("Win32 pointer movement rejected: " + Marshal.GetLastWin32Error()); }
            else
            {
                var screenInfo = _owner.Screens.All.Single();
                var bounds = CGDisplayBounds(CGMainDisplayID());
                if (Math.Abs(screenInfo.Bounds.Width - bounds.Width * screenInfo.Scaling) > 2
                    || Math.Abs(screenInfo.Bounds.Height - bounds.Height * screenInfo.Scaling) > 2)
                    throw new PlatformNotSupportedException("Ambiguous Quartz/Avalonia display coordinate mapping.");
                _macPoint = new(bounds.X + (pixel.X - screenInfo.Bounds.X) / screenInfo.Scaling,
                    bounds.Y + (pixel.Y - screenInfo.Bounds.Y) / screenInfo.Scaling);
                PostMouse(_buttonHeld ? 6u : 5u);
            }
            await Task.Delay(70); return;
        }
        int screen = XDefaultScreen(_display);
        if (pixel.X < 0 || pixel.Y < 0 || pixel.X >= XDisplayWidth(_display, screen) || pixel.Y >= XDisplayHeight(_display, screen))
            throw new InvalidOperationException("Native pointer target is outside private screen: " + target.Name);
        Require(XTestFakeMotionEvent(_display, screen, pixel.X, pixel.Y, 0));
        Flush(); await Task.Delay(70);
    }

    internal async Task ClickAsync(Control target, Point? point = null)
    {
        if (!target.IsEffectivelyEnabled) throw new InvalidOperationException("Native click target is disabled: " + target.Name);
        await MoveAsync(target, point);
        await ButtonAsync(true); await ButtonAsync(false);
    }

    internal async Task ButtonAsync(bool down)
    {
        if (down) EnsureOwnActiveWindow();
        if (_display != 0) Require(XTestFakeButtonEvent(_display, 1, down ? 1 : 0, 0));
        else if (OperatingSystem.IsWindows()) Send(new WinInput { Type = 0, Data = new WinInputUnion { Mouse = new WinMouse { Flags = down ? 0x0002u : 0x0004u } } });
        else PostMouse(down ? 1u : 2u);
        _buttonHeld = down; Flush(); await Task.Delay(down ? 40 : 90);
    }

    internal async Task KeyAsync(ulong key, bool modified = false)
    {
        try
        {
            if (modified) { Key(ControlLeft, true); Key(ShiftLeft, true); }
            Key(key, true); Flush(); await Task.Delay(40);
        }
        finally
        {
            Key(key, false);
            if (modified) { Key(ShiftLeft, false); Key(ControlLeft, false); }
            Flush();
        }
        await Task.Delay(100);
    }

    private void Key(ulong symbol, bool down)
    {
        if (down) EnsureOwnActiveWindow();
        uint code = _display != 0 ? XKeysymToKeycode(_display, (nuint)symbol) : NativeKey(symbol);
        if (code == 0) throw new InvalidOperationException("Native test key is not mapped by private X server.");
        if (down) _heldKeys.Add(code); else _heldKeys.Remove(code);
        if (_display != 0) Require(XTestFakeKeyEvent(_display, code, down ? 1 : 0, 0));
        else if (OperatingSystem.IsWindows()) Send(new WinInput { Type = 1, Data = new WinInputUnion {
            Keyboard = new WinKeyboard { VirtualKey = (ushort)code, Flags = (down ? 0u : 2u) | (IsExtendedWindowsKey(code) ? 1u : 0u) } } });
        else
        {
            nint e = CGEventCreateKeyboardEvent(0, (ushort)code, down);
            PostMac(e);
        }
    }
    private void Flush() { if (_display != 0) XSync(_display, 0); }
    private static void Require(int success)
    { if (success == 0) throw new InvalidOperationException("XTEST rejected native input; no synthetic fallback."); }
    public void Dispose()
    {
        foreach (uint code in _heldKeys.ToArray())
        {
            _heldKeys.Remove(code);
            if (_display != 0) XTestFakeKeyEvent(_display, code, 0, 0);
            else if (OperatingSystem.IsWindows()) Send(new WinInput { Type = 1, Data = new WinInputUnion { Keyboard = new WinKeyboard { VirtualKey = (ushort)code, Flags = 2 | (IsExtendedWindowsKey(code) ? 1u : 0u) } } });
            else PostMac(CGEventCreateKeyboardEvent(0, (ushort)code, false));
        }
        if (_buttonHeld)
        {
            if (_display != 0) XTestFakeButtonEvent(_display, 1, 0, 0);
            else if (OperatingSystem.IsWindows()) Send(new WinInput { Type = 0, Data = new WinInputUnion { Mouse = new WinMouse { Flags = 4 } } });
            else PostMouse(2);
        }
        Flush(); if (_display != 0) XCloseDisplay(_display);
    }

    private void EnsureOwnActiveWindow()
    {
        if (!_owner.IsActive && !_owner.OwnedWindows.Any(w => w.IsActive && w.IsVisible))
            throw new InvalidOperationException("Refusing input: the harness does not own the active window.");
        if (OperatingSystem.IsWindows())
        {
            nint foreground = GetForegroundWindow();
            if (foreground == 0 || GetWindowThreadProcessId(foreground, out uint process) == 0 || process != Environment.ProcessId)
                throw new InvalidOperationException("Refusing SendInput: foreground window belongs to another process.");
        }
    }
    private uint NativeKey(ulong key) => (uint)(OperatingSystem.IsWindows() ? key switch
    { Left => 0x25, Right => 0x27, Tab => 0x09, Escape => 0x1b, Space => 0x20, ControlLeft => 0x11, ShiftLeft => 0x10, F6 => 0x75, Home => 0x24, End => 0x23, F7 => 0x76, F8 => 0x77, _ => throw new ArgumentOutOfRangeException(nameof(key)) }
        : key switch { Left => 123, Right => 124, Tab => 48, Escape => 53, Space => 49, ControlLeft => 55, ShiftLeft => 56, F6 => 97, Home => 115, End => 119, F7 => 98, F8 => 100, _ => throw new ArgumentOutOfRangeException(nameof(key)) });
    private static bool IsExtendedWindowsKey(uint key) => key is 0x23 or 0x24 or 0x25 or 0x27;
    [StructLayout(LayoutKind.Sequential)] private struct WinMouse { internal int X, Y; internal uint Data, Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct WinKeyboard { internal ushort VirtualKey, Scan; internal uint Flags, Time; internal nuint Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct WinInputUnion { [FieldOffset(0)] internal WinMouse Mouse; [FieldOffset(0)] internal WinKeyboard Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct WinInput { internal uint Type; internal WinInputUnion Data; }
    private static void Send(WinInput input)
    { if (SendInput(1, new[] { input }, Marshal.SizeOf<WinInput>()) != 1) throw new InvalidOperationException("SendInput rejected input: " + Marshal.GetLastWin32Error()); }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, WinInput[] inputs, int size);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MacPoint(double X, double Y);
    [StructLayout(LayoutKind.Sequential)] private readonly record struct MacRect(double X, double Y, double Width, double Height);
    private MacPoint _macPoint;
    private void PostMouse(uint type) => PostMac(CGEventCreateMouseEvent(0, type, _macPoint, 0));
    private void PostMac(nint e)
    {
        if (e == 0) throw new InvalidOperationException("CGEvent creation failed.");
        try { CGEventSetFlags(e, (_heldKeys.Contains(55) ? 1UL << 20 : 0) | (_heldKeys.Contains(56) ? 1UL << 17 : 0)); CGEventPost(0, e); }
        finally { CFRelease(e); }
    }
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport(CG)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightPostEventAccess();
    [DllImport(CG)] private static extern uint CGMainDisplayID();
    [DllImport(CG)] private static extern MacRect CGDisplayBounds(uint display);
    [DllImport(CG)] private static extern nint CGEventCreateKeyboardEvent(nint source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
    [DllImport(CG)] private static extern nint CGEventCreateMouseEvent(nint source, uint type, MacPoint position, uint button);
    [DllImport(CG)] private static extern void CGEventSetFlags(nint e, ulong flags);
    [DllImport(CG)] private static extern void CGEventPost(uint tap, nint e);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint value);

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern int XDefaultScreen(nint display);
    [DllImport("libX11.so.6")] private static extern int XDisplayWidth(nint display, int screen);
    [DllImport("libX11.so.6")] private static extern int XDisplayHeight(nint display, int screen);
    [DllImport("libX11.so.6")] private static extern byte XKeysymToKeycode(nint display, nuint keysym);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, int discard);
    [DllImport("libX11.so.6")] private static extern int XRaiseWindow(nint display, nint window);
    [DllImport("libX11.so.6")] private static extern int XSetInputFocus(nint display, nint window, int revertTo, nuint time);
    [DllImport("libXtst.so.6")] private static extern int XTestQueryExtension(nint display, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeButtonEvent(nint display, uint button, int down, nuint delay);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeKeyEvent(nint display, uint key, int down, nuint delay);
}
