using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MeowChan.Desktop;

/// <summary>Compensates Avalonia 12.1.4's X11 minimum hints for client-drawn decorations.
/// Window.Resize adds the forced decoration inset, but Window's min/max binding does not.
/// Keep the public content minimum unchanged and correct only the native X11 hint.</summary>
internal static class X11WindowMinimum
{
    internal static void Apply(Window window, Size minimum)
    {
        if (!OperatingSystem.IsLinux() || !window.IsVisible || window.WindowState != WindowState.Normal
            || window.WindowDecorations != WindowDecorations.BorderOnly
            || window.IsExtendedIntoWindowDecorations || window.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle)
            return;
        nint display = XOpenDisplay(null);
        if (display == 0) return;
        try
        {
            if (XGetGeometry(display, handle.Handle, out _, out _, out _, out uint width, out uint height, out _, out _) == 0)
                return;
            PixelSize? corrected = NativeMinimum(minimum, window.ClientSize, window.Bounds,
                new PixelSize((int)width, (int)height), window.RenderScaling);
            if (Environment.GetEnvironmentVariable("MEOWCHAN_NATIVE_GEOMETRY_TRACE") == "1")
                Console.WriteLine($"SMOKE native minimum compensation: client={window.ClientSize}; bounds={window.Bounds}; native={width}x{height}; contentMinimum={minimum}; corrected={corrected}; scale={window.RenderScaling}");
            if (corrected is not PixelSize pixels || XGetWMNormalHints(display, handle.Handle, out SizeHints hints, out _) == 0)
                return;
            const int minimumSizeFlag = 1 << 4;
            if ((hints.Flags & minimumSizeFlag) != 0 && hints.MinWidth == pixels.Width && hints.MinHeight == pixels.Height)
                return;
            hints.Flags |= minimumSizeFlag;
            hints.MinWidth = pixels.Width;
            hints.MinHeight = pixels.Height;
            XSetWMNormalHints(display, handle.Handle, ref hints);
            XFlush(display);
        }
        finally { XCloseDisplay(display); }
    }

    internal static PixelSize? NativeMinimum(Size minimum, Size client, Rect contentBounds, PixelSize native, double scale)
    {
        // BorderOnly's forced frame/shadow surrounds the content symmetrically.
        // Check the actual native size against the laid-out inset first: native
        // ConfigureNotify and managed layout can briefly describe different sizes.
        // Never mistake an unacknowledged resize for a larger decoration.
        if (!double.IsFinite(scale) || scale <= 0 || client.Width <= 0 || client.Height <= 0
            || contentBounds.X < 0 || contentBounds.Y < 0 || (contentBounds.X == 0 && contentBounds.Y == 0)
            || Math.Abs(contentBounds.Width - client.Width) > 1 / scale
            || Math.Abs(contentBounds.Height - client.Height) > 1 / scale
            || Math.Abs(native.Width - (client.Width + contentBounds.X * 2) * scale) > 1
            || Math.Abs(native.Height - (client.Height + contentBounds.Y * 2) * scale) > 1)
            return null;
        int insetWidth = native.Width - (int)Math.Round(client.Width * scale);
        int insetHeight = native.Height - (int)Math.Round(client.Height * scale);
        return new PixelSize((int)Math.Ceiling(minimum.Width * scale - 1e-7) + insetWidth,
            (int)Math.Ceiling(minimum.Height * scale - 1e-7) + insetHeight);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SizeHints
    {
        public nint Flags;
        public int X, Y, Width, Height;
        public int MinWidth, MinHeight, MaxWidth, MaxHeight;
        public int WidthIncrement, HeightIncrement;
        public int MinAspectX, MinAspectY, MaxAspectX, MaxAspectY;
        public int BaseWidth, BaseHeight, Gravity;
    }
    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(string? name);
    [DllImport("libX11.so.6")] private static extern int XGetGeometry(nint display, nint window, out nint root, out int x, out int y,
        out uint width, out uint height, out uint borderWidth, out uint depth);
    [DllImport("libX11.so.6")] private static extern int XGetWMNormalHints(nint display, nint window, out SizeHints hints, out nint supplied);
    [DllImport("libX11.so.6")] private static extern void XSetWMNormalHints(nint display, nint window, ref SizeHints hints);
    [DllImport("libX11.so.6")] private static extern int XFlush(nint display);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
}
