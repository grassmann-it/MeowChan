using System;
using Avalonia;

namespace MeowChan.Desktop;

internal readonly record struct WindowStartupFit(Size Client, Size Minimum, PixelPoint Position);

internal static class WindowStartupGeometry
{
    // WorkingArea and Position use physical pixels; client/frame sizes use DIPs.
    // Reserve the measured native decorations instead of assuming that the OS
    // clamps the complete frame to the usable desktop.
    internal static WindowStartupFit Fit(Size client, Size? frame, Size minimum,
        PixelRect workingArea, double scaling, PixelPoint position)
    {
        if (!double.IsFinite(scaling) || scaling <= 0) throw new ArgumentOutOfRangeException(nameof(scaling));
        if (workingArea.Width <= 0 || workingArea.Height <= 0) return new(client, minimum, position);
        double borderWidth = Math.Max(0, (frame?.Width ?? client.Width) - client.Width);
        double borderHeight = Math.Max(0, (frame?.Height ?? client.Height) - client.Height);
        double availableWidth = Math.Max(1, Math.Floor(workingArea.Width / scaling - borderWidth));
        double availableHeight = Math.Max(1, Math.Floor(workingArea.Height / scaling - borderHeight));
        Size fittedMinimum = new(Math.Min(minimum.Width, availableWidth), Math.Min(minimum.Height, availableHeight));
        Size fittedClient = new(Math.Clamp(client.Width, fittedMinimum.Width, availableWidth),
            Math.Clamp(client.Height, fittedMinimum.Height, availableHeight));
        int frameWidth = Math.Min(workingArea.Width, (int)Math.Ceiling((fittedClient.Width + borderWidth) * scaling));
        int frameHeight = Math.Min(workingArea.Height, (int)Math.Ceiling((fittedClient.Height + borderHeight) * scaling));
        PixelPoint fittedPosition = new(Math.Clamp(position.X, workingArea.X, workingArea.Right - frameWidth),
            Math.Clamp(position.Y, workingArea.Y, workingArea.Bottom - frameHeight));
        return new(fittedClient, fittedMinimum, fittedPosition);
    }
}
