using System;
using Avalonia.Controls;
using Avalonia.Threading;

namespace MeowChan.Desktop.Motion;

/// <summary>
/// D3/D1: gives an in-window overlay (filter surface) its 180 ms fade in. The overlay keeps its own visibility logic; this only
/// mirrors it into the class <c>shown</c> that the <c>motion-overlay</c> style animates (Opacity 0 to 1). Entering fades in one
/// rendered frame after it became visible; leaving is immediate (an overlay that closed is gone at once, and focus has already
/// moved). Under motion-off the style does not exist, so the class is inert and the overlay is simply visible or not.
/// </summary>
internal static class OverlayReveal
{
    internal const string StyleClass = "motion-overlay", ShownClass = "shown";

    internal static void Follow(Border overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        overlay.Classes.Add(StyleClass);
        overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property == Control.IsVisibleProperty) Sync(overlay, animate: true);
        };
        Sync(overlay, animate: false);
    }

    private static void Sync(Border overlay, bool animate)
    {
        if (!overlay.IsVisible)
        {
            overlay.Classes.Set(ShownClass, false);
            return;
        }
        if (!animate || MotionPolicy.IsOff)
        {
            overlay.Classes.Set(ShownClass, true);
            return;
        }
        // The start value (Opacity 0) must be rendered once before the transition runs towards 1.
        overlay.Dispatcher.Post(() =>
        {
            if (overlay.IsVisible) overlay.Classes.Set(ShownClass, true);
        }, DispatcherPriority.Render);
    }
}
