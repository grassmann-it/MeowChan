using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;

namespace MeowChan.Desktop;

/// <summary>
/// Keeps a fixed-size secondary window on the screen it opens on (1.1.0 accessibility audit M5). At 150 % or 200 % scaling a
/// window of 760 dip height is taller than a 1080p screen, so its footer (Close, status) would lie out of reach of the mouse
/// and of a magnifier. The requested size stays the preferred one: only a window that does not fit is made smaller, together
/// with its minimum, and moved back inside the working area. A window that fits is not touched.
/// </summary>
internal static class DialogFit
{
    /// <summary>Space kept free around the window (dip): the title bar of a window manager and a little air.</summary>
    internal const double MarginDip = 32;

    /// <summary>Fits the window the first time it opens.</summary>
    internal static void OnOpen(Window window) => window.Opened += (_, _) => Apply(window);

    internal static void Apply(Window window)
    {
        if (window.WindowState != WindowState.Normal || window.ClientSize.Width <= 0 || window.ClientSize.Height <= 0) return;
        Screen? screen = window.Screens.ScreenFromWindow(window);
        if (screen == null) return;
        Size client = window.ClientSize;
        WindowStartupFit fit = Compute(client, window.FrameSize, new Size(window.MinWidth, window.MinHeight),
            screen.WorkingArea, window.RenderScaling, window.Position);
        if (fit.Minimum.Width < window.MinWidth) window.MinWidth = fit.Minimum.Width;
        if (fit.Minimum.Height < window.MinHeight) window.MinHeight = fit.Minimum.Height;
        if (fit.Client.Width < client.Width - 0.5) window.Width = fit.Client.Width;
        if (fit.Client.Height < client.Height - 0.5) window.Height = fit.Client.Height;
        if (fit.Position != window.Position) window.Position = fit.Position;
    }

    /// <summary>The fitted client size, minimum and position inside the working area less the margin; pure, for tests.</summary>
    internal static WindowStartupFit Compute(Size client, Size? frame, Size minimum, PixelRect workingArea, double scaling, PixelPoint position)
    {
        if (!double.IsFinite(scaling) || scaling <= 0) scaling = 1;
        int margin = (int)Math.Ceiling(MarginDip * scaling);
        PixelRect area = new(workingArea.X + margin / 2, workingArea.Y + margin / 2,
            Math.Max(1, workingArea.Width - margin), Math.Max(1, workingArea.Height - margin));
        return WindowStartupGeometry.Fit(client, frame, minimum, area, scaling, position);
    }
}
