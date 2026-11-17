using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MeowChan.DesktopSmoke;

/// <summary>Test-only OS input. Never connects to a personal display or substitutes routed events.</summary>
internal sealed class MediaNativeInput : IDisposable
{
    private readonly nint _display;
    private readonly Window _owner;
    private readonly Action<string, object>? _trace;
    private int _x, _y;
    private TopLevel? _lastMacPointerRoot;
    private readonly HashSet<uint> _heldKeys = new();
    private uint _heldButton;
    internal const ulong Left = 0xff51, Right = 0xff53, Up = 0xff52, Down = 0xff54, Enter = 0xff0d, Tab = 0xff09, Escape = 0xff1b, Space = 0x20, ControlLeft = 0xffe3, ShiftLeft = 0xffe1, F6 = 0xffc3, Home = 0xff50, End = 0xff57, F7 = 0xffc4, F8 = 0xffc5;
    internal string Backend => OperatingSystem.IsWindows() ? "Windows Win32 SendInput; explicit disposable desktop"
        : OperatingSystem.IsMacOS() ? "macOS CGEvent; explicit disposable single-display desktop" : "X11 XTest (libXtst.so.6); private Xvfb; Linux software video";

    internal MediaNativeInput(Window owner, Action<string, object>? trace = null)
    {
        _owner = owner;
        _trace = trace;
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

    private const int MaxTargetAttempts = 24;
    private static readonly TimeSpan TargetRetryDelay = TimeSpan.FromMilliseconds(25);

    internal Task MoveAsync(Control target, Point? point = null, bool requireEnabled = true, bool requireHitTest = true) =>
        MoveAsync(() => Task.FromResult(target), _ => Task.FromResult(point), requireEnabled, requireHitTest);

    /// <summary>
    /// Moves to a current visual-tree target. The resolver, readiness checks and
    /// coordinate conversion are deliberately one bounded operation: a transfer can
    /// retire the returned control after a readiness probe, so no stale point is reused.
    /// </summary>
    internal async Task MoveAsync<T>(Func<Task<T>> resolveTarget, Func<T, Task<Point?>>? resolvePoint = null,
        bool requireEnabled = true, bool requireHitTest = true)
        where T : Control
    {
        for (int attempt = 1; attempt <= MaxTargetAttempts; attempt++)
        {
            T? target = null;
            Point? point = null;
            try
            {
                target = await resolveTarget();
                await PrepareTargetAsync(target, requireEnabled, requireHitTest);
                point = resolvePoint == null ? null : await resolvePoint(target);
                await MoveOnceAsync(target, point, requireEnabled, requireHitTest);
                return;
            }
            catch (ArgumentException) when (attempt < MaxTargetAttempts)
            {
                // Re-resolve the control on the next dispatcher turn. A cross-root
                // conversion failure means this instance is stale, not that a routed
                // or synthetic event should be substituted.
                await Task.Delay(TargetRetryDelay);
            }
            catch (ArgumentException)
            {
                TraceFinalArgument("move", attempt, target, point, requireEnabled, requireHitTest);
                throw;
            }
        }
    }

    private async Task PrepareTargetAsync(Control target, bool requireEnabled, bool requireHitTest)
    {
        // The gallery volume opens above the speaker when it is pointed at (plan V1):
        // point at the live host first, then re-check this same current control.
        if (!target.IsEffectivelyVisible && target.Name == "GalleryVideoVolume"
            && target.GetVisualAncestors().OfType<Control>().FirstOrDefault(control => control.Name == "GalleryVideoVolumeHost") is { } speaker)
        {
            await MoveAsync(speaker);
            await Program.WaitUntilAsync(
                () => IsNativeGeometryReady(target, requireEnabled),
                () => "The volume above the speaker did not open: " + target.Name);
        }
        if (!IsNativeGeometryReady(target, requireEnabled)
            || requireHitTest && !IsNativeHitTargetReady(target, requireEnabled))
            throw new ArgumentException("Native pointer target presentation is not ready.", nameof(target));
    }

    private static bool IsNativeGeometryReady(Control target, bool requireEnabled)
    {
        return target.IsAttachedToVisualTree() && target.IsArrangeValid && target.IsEffectivelyVisible
            && (!requireEnabled || target.IsEffectivelyEnabled)
            && target.Bounds.Width > 0 && target.Bounds.Height > 0
            && TopLevel.GetTopLevel(target) is { IsVisible: true };
    }

    private static bool IsNativeHitTargetReady(Control target, bool requireEnabled, Point? point = null)
    {
        if (!IsNativeGeometryReady(target, requireEnabled)) return false;
        try
        {
            TopLevel root = TopLevel.GetTopLevel(target)!;
            Point targetPoint = point ?? new Point(target.Bounds.Width / 2, target.Bounds.Height / 2);
            PixelPoint pixel = target.PointToScreen(targetPoint);
            Visual? rootHit = root.InputHitTest(root.PointToClient(pixel)) as Visual;
            if (rootHit != null && rootHit.GetSelfAndVisualAncestors().Contains(target)) return true;
            // Avalonia's top-level hit walk can temporarily return an adorner or
            // the window background while a media surface is transferring its
            // overlay. Validate the same coordinate in the current target root as
            // well; this still requires a live descendant of the requested target
            // and does not synthesize or retry delivery.
            Visual? targetHit = target.InputHitTest(targetPoint) as Visual;
            return targetHit != null && targetHit.GetSelfAndVisualAncestors().Contains(target);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task MoveOnceAsync(Control target, Point? point, bool requireEnabled, bool requireHitTest)
    {
        if (requireHitTest && !IsNativeHitTargetReady(target, requireEnabled, point))
            throw new ArgumentException("Native pointer hit target changed before delivery.", nameof(target));
        PixelPoint pixel = target.PointToScreen(point ?? new Point(target.Bounds.Width / 2, target.Bounds.Height / 2));
        if (_display == 0)
        {
            EnsureOwnActiveWindow();
            _x = pixel.X; _y = pixel.Y;
            if (OperatingSystem.IsWindows())
            { if (!SetCursorPos(_x, _y)) throw new InvalidOperationException("Win32 pointer movement rejected: " + Marshal.GetLastWin32Error()); }
            else
            {
                PostMacMotion(pixel);
                _lastMacPointerRoot = TopLevel.GetTopLevel(target);
            }
            await Task.Delay(70); return;
        }
        int screen = XDefaultScreen(_display);
        if (pixel.X < 0 || pixel.Y < 0 || pixel.X >= XDisplayWidth(_display, screen) || pixel.Y >= XDisplayHeight(_display, screen))
            throw new InvalidOperationException("Native pointer target is outside private screen: " + target.Name);
        Require(XTestFakeMotionEvent(_display, screen, pixel.X, pixel.Y, 0));
        Flush(); await Task.Delay(70);
    }

    private void PostMacMotion(PixelPoint pixel)
    {
        var screenInfo = _owner.Screens.All.Single();
        var bounds = CGDisplayBounds(CGMainDisplayID());
        if (Math.Abs(screenInfo.Bounds.Width - bounds.Width * screenInfo.Scaling) > 2
            || Math.Abs(screenInfo.Bounds.Height - bounds.Height * screenInfo.Scaling) > 2)
            throw new PlatformNotSupportedException("Ambiguous Quartz/Avalonia display coordinate mapping.");
        _x = pixel.X; _y = pixel.Y;
        _macPoint = new(bounds.X + (pixel.X - screenInfo.Bounds.X) / screenInfo.Scaling,
            bounds.Y + (pixel.Y - screenInfo.Bounds.Y) / screenInfo.Scaling);
        PostMouse(_heldButton != 0 ? 6u : 5u);
    }

    private async Task ReenterMacOwnerAfterPopupAsync(Control target)
    {
        if (!OperatingSystem.IsMacOS() || _heldButton != 0
            || _lastMacPointerRoot is not { IsVisible: false } retiredRoot
            || ReferenceEquals(retiredRoot, _owner)
            || !ReferenceEquals(TopLevel.GetTopLevel(target), _owner)) return;

        // ARM 43906632831 / Intel 43907560972: after Help retires its native
        // popup and its window closes, More is ready in the active main root,
        // but a direct CGEvent click produces no main-root pointer events and
        // leaves the flyout closed. Playing happened to cross back through the
        // seek bar first; Paused must not seek or add an activation/toggle click.
        // Cross into the unobstructed client header using motion ONLY, and require
        // an actual routed native movement there before submitting the one click.
        EnsureOwnActiveWindow();
        if (!_owner.IsActive || !_owner.IsVisible)
            throw new ArgumentException("Native owner has not regained activation after popup dismissal.", nameof(target));
        Point entry = new(_owner.ClientSize.Width / 2, Math.Min(16, _owner.ClientSize.Height / 4));
        if (!new Rect(_owner.ClientSize).Contains(entry) || _owner.ClientSize.Width <= 0 || _owner.ClientSize.Height <= 0)
            throw new ArgumentException("Native owner has no client entry geometry.", nameof(target));
        PixelPoint entryPixel = _owner.PointToScreen(entry);
        TaskCompletionSource<bool> arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        void Moved(object? sender, PointerEventArgs e)
        {
            Point actual = e.GetPosition(_owner);
            if (e.Source is Visual source && ReferenceEquals(TopLevel.GetTopLevel(source), _owner)
                && Math.Abs(actual.X - entry.X) <= 2 && Math.Abs(actual.Y - entry.Y) <= 2)
                arrived.TrySetResult(true);
        }
        _owner.AddHandler(InputElement.PointerMovedEvent, Moved, RoutingStrategies.Tunnel, handledEventsToo: true);
        try
        {
            PostMacMotion(entryPixel);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(2));
            _trace?.Invoke("native-owner-reentered", new
            {
                target.Name, retiredRoot = retiredRoot.GetType().Name,
                entryX = entryPixel.X, entryY = entryPixel.Y, _owner.IsActive
            });
            _lastMacPointerRoot = _owner;
        }
        catch (TimeoutException)
        {
            _trace?.Invoke("native-owner-reentry-timeout", new
            {
                target.Name, retiredRoot = retiredRoot.GetType().Name,
                entryX = entryPixel.X, entryY = entryPixel.Y, _owner.IsActive
            });
            throw new TimeoutException("Native pointer did not re-enter the main client after popup dismissal; no click was submitted.");
        }
        finally { _owner.RemoveHandler(InputElement.PointerMovedEvent, Moved); }
    }

    internal Task ClickAsync(Control target, Point? point = null, bool requireHitTest = true) =>
        ClickAsync(() => Task.FromResult(target), _ => Task.FromResult(point), requireHitTest);

    /// <summary>
    /// Clicks a freshly resolved target; readiness, coordinate conversion and native delivery are one bounded operation.
    /// A popup may opt out of the framework hit-test probe when its current native root
    /// has valid geometry but Avalonia reports a stale hit-test result during reparenting.
    /// The click is still delivered only through the real OS input backend.
    /// </summary>
    internal async Task ClickAsync<T>(Func<Task<T>> resolveTarget, Func<T, Task<Point?>>? resolvePoint = null,
        bool requireHitTest = true)
        where T : Control
    {
        for (int attempt = 1; attempt <= MaxTargetAttempts; attempt++)
        {
            T? target = null;
            Point? point = null;
            try
            {
                target = await resolveTarget();
                await ReenterMacOwnerAfterPopupAsync(target);
                await PrepareTargetAsync(target, requireEnabled: true, requireHitTest);
                point = resolvePoint == null ? null : await resolvePoint(target);
                await MoveOnceAsync(target, point, true, requireHitTest);
                await ButtonAsync(true); await ButtonAsync(false);
                // This is OS submission evidence, not proof that Avalonia handled
                // the click. The caller still asserts the actual menu/panel effect.
                _trace?.Invoke("native-click-submitted", new
                {
                    target = target.GetType().Name, target.Name, attempt, requireHitTest,
                    root = TopLevel.GetTopLevel(target)?.GetType().Name,
                    _owner.IsActive
                });
                return;
            }
            catch (ArgumentException) when (attempt < MaxTargetAttempts)
            {
                // Retry the complete current-target transaction; never reuse its stale
                // coordinate or synthesize input.
                await Task.Delay(TargetRetryDelay);
            }
            catch (ArgumentException)
            {
                TraceFinalArgument("click", attempt, target, point, requireEnabled: true, requireHitTest);
                throw;
            }
        }
    }

    internal Task RightClickAsync(Control target, Point? point = null) =>
        RightClickAsync(() => Task.FromResult(target), _ => Task.FromResult(point));

    private async Task RightClickAsync<T>(Func<Task<T>> resolveTarget, Func<T, Task<Point?>>? resolvePoint = null)
        where T : Control
    {
        for (int attempt = 1; attempt <= MaxTargetAttempts; attempt++)
        {
            T? target = null;
            Point? point = null;
            try
            {
                target = await resolveTarget();
                await PrepareTargetAsync(target, requireEnabled: true, requireHitTest: true);
                point = resolvePoint == null ? null : await resolvePoint(target);
                await MoveOnceAsync(target, point, true, true);
                try { await ButtonAsync(3, true); }
                finally { if (_heldButton == 3) await ButtonAsync(3, false); }
                return;
            }
            catch (ArgumentException) when (attempt < MaxTargetAttempts)
            {
                await Task.Delay(TargetRetryDelay);
            }
            catch (ArgumentException)
            {
                TraceFinalArgument("right-click", attempt, target, point, requireEnabled: true, requireHitTest: true);
                throw;
            }
        }
    }

    private void TraceFinalArgument(string operation, int attempts, Control? target, Point? point,
        bool requireEnabled, bool requireHitTest)
    {
        try
        {
            object? Hit(Point at)
            {
                if (target == null || TopLevel.GetTopLevel(target) is not { } root) return null;
                try
                {
                    PixelPoint pixel = target.PointToScreen(at);
                    Visual? rootHit = root.InputHitTest(root.PointToClient(pixel)) as Visual;
                    Visual? localHit = target.InputHitTest(at) as Visual;
                    object? Describe(Visual? visual) => visual == null ? null : new
                    {
                        type = visual.GetType().Name, name = (visual as Control)?.Name,
                        withinTarget = visual.GetSelfAndVisualAncestors().Contains(target)
                    };
                    return new { at = at.ToString(), pixel = pixel.ToString(), rootHit = Describe(rootHit), localHit = Describe(localHit) };
                }
                catch (ArgumentException) { return new { conversionFailed = true }; }
            }
            var evidence = new
            {
                operation, attempts, requireEnabled, requireHitTest,
                point = point?.ToString(),
                target = target == null ? null : new
                {
                    type = target.GetType().Name, name = target.Name,
                    attached = target.IsAttachedToVisualTree(), arrangeValid = target.IsArrangeValid,
                    visible = target.IsEffectivelyVisible, enabled = target.IsEffectivelyEnabled,
                    target.IsHitTestVisible, bounds = target.Bounds.ToString(),
                    root = TopLevel.GetTopLevel(target)?.GetType().Name,
                    rootVisible = TopLevel.GetTopLevel(target)?.IsVisible,
                    geometryReady = IsNativeGeometryReady(target, requireEnabled),
                    centerReady = IsNativeHitTargetReady(target, requireEnabled),
                    center = Hit(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2)),
                    requested = point is { } at ? Hit(at) : null
                },
                ownerActive = _owner.IsActive,
                ownedActive = _owner.OwnedWindows.Any(window => window.IsActive && window.IsVisible)
            };
            // Closure checks do not all register a trace sink. Keep their terminal predicate
            // evidence in output.log too; no text contents, media paths or profile values.
            Console.Error.WriteLine("SMOKE native-input-final-argument " + System.Text.Json.JsonSerializer.Serialize(evidence));
            _trace?.Invoke("native-input-final-argument", evidence);
        }
        catch
        {
            // Trace evidence must never replace the final native ArgumentException.
        }
    }

    internal Task ButtonAsync(bool down) => ButtonAsync(1, down);

    private async Task ButtonAsync(uint button, bool down)
    {
        if (down)
        {
            EnsureOwnActiveWindow();
            if (_heldButton != 0) throw new InvalidOperationException("Native input already holds mouse button " + _heldButton + ".");
            // Mark before dispatch so Dispose can release even if the native call throws
            // after the OS accepted the down event.
            _heldButton = button;
        }
        if (_display != 0) Require(XTestFakeButtonEvent(_display, button, down ? 1 : 0, 0));
        else if (OperatingSystem.IsWindows()) Send(new WinInput { Type = 0, Data = new WinInputUnion { Mouse = new WinMouse { Flags = button == 3 ? (down ? 0x0008u : 0x0010u) : (down ? 0x0002u : 0x0004u) } } });
        else PostMouse(down ? (button == 3 ? 3u : 1u) : (button == 3 ? 4u : 2u), button == 3 ? 1u : 0u);
        if (!down) _heldButton = 0;
        Flush(); await Task.Delay(down ? 40 : 90);
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

    internal const ulong AltLeft = 0xffe9, BackSpace = 0xff08;

    /// <summary>
    /// Turns the private X server's key autorepeat off or on. Under host load a 40 ms XTest press can outlast the server's repeat delay and a typed
    /// letter arrives many times; a typing check opts out of that (Linux XTest only; the server is the harness's own Xvfb).
    /// </summary>
    internal void SetAutoRepeat(bool on)
    {
        if (_display == 0) return;
        if (on) XAutoRepeatOn(_display); else XAutoRepeatOff(_display);
        Flush();
    }

    /// <summary>One key with the given modifier keys held (X11 keysyms; Linux XTest only).</summary>
    internal async Task ChordAsync(ulong key, params ulong[] modifiers)
    {
        if (_display == 0) throw new PlatformNotSupportedException("Chords are only implemented for the private X11 server.");
        try
        {
            foreach (ulong modifier in modifiers) Key(modifier, true);
            Key(key, true); Flush(); await Task.Delay(40);
        }
        finally
        {
            Key(key, false);
            foreach (ulong modifier in modifiers.Reverse()) Key(modifier, false);
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
        if (_heldButton != 0)
        {
            uint button = _heldButton;
            if (_display != 0) XTestFakeButtonEvent(_display, button, 0, 0);
            else if (OperatingSystem.IsWindows()) Send(new WinInput { Type = 0, Data = new WinInputUnion { Mouse = new WinMouse { Flags = button == 3 ? 0x0010u : 0x0004u } } });
            else PostMouse(button == 3 ? 4u : 2u, button == 3 ? 1u : 0u);
            _heldButton = 0;
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
    { Left => 0x25, Right => 0x27, Up => 0x26, Down => 0x28, Enter => 0x0d, Tab => 0x09, Escape => 0x1b, Space => 0x20, ControlLeft => 0x11, ShiftLeft => 0x10, F6 => 0x75, Home => 0x24, End => 0x23, F7 => 0x76, F8 => 0x77, _ => throw new ArgumentOutOfRangeException(nameof(key)) }
        : key switch { Left => 123, Right => 124, Up => 126, Down => 125, Enter => 36, Tab => 48, Escape => 53, Space => 49, ControlLeft => 55, ShiftLeft => 56, F6 => 97, Home => 115, End => 119, F7 => 98, F8 => 100, _ => throw new ArgumentOutOfRangeException(nameof(key)) });
    private static bool IsExtendedWindowsKey(uint key) => key is 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28;
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
    private void PostMouse(uint type, uint button = 0) => PostMac(CGEventCreateMouseEvent(0, type, _macPoint, button));
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
    [DllImport("libX11.so.6")] private static extern int XAutoRepeatOff(nint display);
    [DllImport("libX11.so.6")] private static extern int XAutoRepeatOn(nint display);
    [DllImport("libX11.so.6")] private static extern int XSetInputFocus(nint display, nint window, int revertTo, nuint time);
    [DllImport("libXtst.so.6")] private static extern int XTestQueryExtension(nint display, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeButtonEvent(nint display, uint button, int down, nuint delay);
    [DllImport("libXtst.so.6")] private static extern int XTestFakeKeyEvent(nint display, uint key, int down, nuint delay);
}
