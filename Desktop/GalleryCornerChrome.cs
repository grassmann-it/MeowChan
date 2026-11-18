using Avalonia.Controls;

namespace MeowChan.Desktop;

/// <summary>
/// What the way back and the favourite badge (the pair at the top left of the picture) look like and answer to. While the overlay controls
/// are auto-hidden the pair is gone completely, not dimmed (Tom, 15.11.): fully transparent, out of hit testing so clicks reach the media,
/// and out of the tab order. Mouse motion, a favourite or rating change, the pointer over a control and the keyboard focus inside the
/// pair bring it back; it hides again after "Hide controls after".
/// </summary>
internal readonly record struct GalleryCornerChrome(double Opacity, bool HitTestVisible, bool Focusable)
{
    internal static GalleryCornerChrome Shown => new(1, true, true);
    internal static GalleryCornerChrome Hidden => new(0, false, false);

    /// <summary>
    /// <paramref name="autoHidden"/>: the overlay controls are auto-hidden now. <paramref name="held"/>: the pointer is over a control of the
    /// pair or the keyboard focus is inside it (neither may be hidden away). <paramref name="revealed"/>: recent motion or a change keeps it
    /// shown for the idle delay. Without auto-hide the pair is always shown (docked controls keep the way back on the picture).
    /// </summary>
    internal static GalleryCornerChrome Resolve(bool autoHidden, bool held, bool revealed) =>
        !autoHidden || held || revealed ? Shown : Hidden;

    internal void ApplyTo(Control control)
    {
        control.Opacity = Opacity;
        control.IsHitTestVisible = HitTestVisible;
        control.Focusable = Focusable;
    }
}
