using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The vertical wheel on a horizontal strip of thumbnails (audit V6): it scrolls the strip sideways, as it does on the gallery filmstrip. The notch,
/// how far one wheel step moves and in which direction, is the filmstrip's own (<see cref="GalleryFilmstrip"/> calls <see cref="Notches"/> and
/// <see cref="NotchTiles"/> too): wheel up (and a pad swipe to the right) look back along the strip, wheel down forward.
/// </summary>
internal static class StripWheel
{
    /// <summary>How many tiles one wheel notch moves a strip.</summary>
    internal const double NotchTiles = 2;

    /// <summary>The notches of a wheel event: the vertical delta, or the horizontal one of a pad swipe when there is no vertical one; 0 for none.</summary>
    internal static double Notches(Vector delta) => delta.Y != 0 ? delta.Y : delta.X;

    /// <summary>The strip's new offset after <paramref name="delta"/>, not clamped (the scroll viewer clamps it); <paramref name="tilePitch"/> is a tile plus its gap.</summary>
    internal static double NextOffset(double offset, Vector delta, double tilePitch) => offset - Notches(delta) * NotchTiles * tilePitch;

    /// <summary>
    /// Makes the wheel scroll <paramref name="strip"/> sideways. A strip whose tiles all fit leaves the wheel to what is behind it (the reader's posts),
    /// and a wheel with Ctrl or another modifier is not a scroll at all (Ctrl+wheel is the reader's font size).
    /// </summary>
    /// <param name="strip">The list that owns the scroll viewer (its template's scroll viewer is found when the wheel turns).</param>
    /// <param name="tilePitch">A tile plus the gap between two tiles, in device-independent pixels.</param>
    internal static void Attach(Control strip, double tilePitch)
    {
        ArgumentNullException.ThrowIfNull(strip);
        strip.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) => OnWheel(strip, e, tilePitch), RoutingStrategies.Tunnel);
    }

    private static void OnWheel(Control strip, PointerWheelEventArgs e, double tilePitch)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None) return;
        ScrollViewer? viewer = FindViewer(strip);
        if (viewer == null || !TryScroll(viewer, e.Delta, tilePitch)) return;
        e.Handled = true;
    }

    private static ScrollViewer? FindViewer(Control strip) =>
        strip as ScrollViewer ?? strip.FindDescendantOfType<ScrollViewer>();

    /// <summary>Scrolls <paramref name="viewer"/> by <paramref name="delta"/>; false when it has nothing to scroll sideways or the event has no delta.</summary>
    internal static bool TryScroll(ScrollViewer viewer, Vector delta, double tilePitch)
    {
        double notches = Notches(delta);
        if (notches == 0 || !double.IsFinite(notches) || viewer.Extent.Width <= viewer.Viewport.Width + 0.5) return false;
        double maximum = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
        double next = Math.Clamp(NextOffset(viewer.Offset.X, delta, tilePitch), 0, maximum);
        viewer.Offset = new Vector(next, viewer.Offset.Y);
        return true;
    }
}
