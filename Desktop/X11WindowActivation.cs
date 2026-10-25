using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MeowChan.Desktop;

/// <summary>Identifies a stale managed deactivation only when both native X11
/// activation and keyboard focus still belong to the exact owner window.</summary>
internal static class X11WindowActivation
{
    internal static bool OwnsNativeFocus(Window? window)
    {
        if (!OperatingSystem.IsLinux() || window is not { IsVisible: true }
            || window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle) return false;
        nint display = XOpenDisplay(null);
        if (display == 0) return false;
        try
        {
            nint atom = XInternAtom(display, "_NET_ACTIVE_WINDOW", 1);
            if (atom == 0 || XGetInputFocus(display, out nint focused, out _) == 0 || focused != handle.Handle)
                return false;
            int result = XGetWindowProperty(display, XDefaultRootWindow(display), atom, 0, 1, 0, 33,
                out nint type, out int format, out nuint count, out _, out nint data);
            try
            {
                return result == 0 && type == 33 && format == 32 && count == 1 && data != 0
                    && Marshal.ReadIntPtr(data) == handle.Handle;
            }
            finally { if (data != 0) XFree(data); }
        }
        finally { XCloseDisplay(display); }
    }

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] private static extern nint XInternAtom(nint display, string name, int onlyIfExists);
    [DllImport("libX11.so.6")] private static extern int XGetInputFocus(nint display, out nint focused, out int revertTo);
    [DllImport("libX11.so.6")] private static extern int XGetWindowProperty(nint display, nint window, nint property,
        nint offset, nint length, int delete, nint requestedType, out nint actualType, out int actualFormat,
        out nuint itemCount, out nuint bytesAfter, out nint data);
    [DllImport("libX11.so.6")] private static extern int XFree(nint data);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
}
