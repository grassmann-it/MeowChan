using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MeowChan.Desktop;

internal readonly record struct GalleryEdgeHit(bool Controls, bool Post)
{
    // An already-visible panel owns its whole rectangle, not just the 24px strip.
    // Indicators own their own hover/activation path, not an underlying strip.
    // Their targets stay present through reveal until the pointer leaves.
    internal GalleryEdgeHit WithoutPanelOverlap(bool inControls, bool inPost, bool inIndicator) =>
        inControls || inPost || inIndicator ? default : this;

    internal static GalleryEdgeHit At(Point point, Size size, double band = 24)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || size.Width <= 0 || size.Height <= 0
            || !double.IsFinite(size.Width) || !double.IsFinite(size.Height) || !double.IsFinite(band) || band <= 0
            || point.X < 0 || point.Y < 0 || point.X >= size.Width || point.Y >= size.Height) return default;
        bool bottom = point.Y >= Math.Max(0, size.Height - band);
        // The bottom-right corner belongs to transport, not an unexpected reading pause.
        return new(bottom, !bottom && point.X >= Math.Max(0, size.Width - band));
    }
}

internal static class GalleryEdgeKeyboard
{
    // No mutation of Handled and no key-up latch: routing owns all commands,
    // including Escape and repeated arrows. Tab alone requests keyboard access.
    internal static bool ShouldReveal(KeyEventArgs e) => !e.Handled && e.Key == Key.Tab
        && (e.KeyModifiers & ~KeyModifiers.Shift) == KeyModifiers.None;
}

internal static class GalleryEdgeHover
{
    internal static bool HasSelection(Control root) => root.IsEffectivelyVisible
        && root.GetVisualDescendants().Prepend(root).OfType<Control>().Any(control => control.IsEffectivelyVisible && control switch
        {
            SelectableTextBlock text => text.SelectionStart != text.SelectionEnd,
            TextBox text => text.SelectionStart != text.SelectionEnd,
            _ => false
        });

    internal static bool CanReveal(bool captured, bool selectedText, bool keyboardPanelFocus, bool helpOpen) =>
        !captured && !selectedText && !keyboardPanelFocus && !helpOpen;
}

// Only actual mouse motion renews artwork; hover, presses and timer polling do not.
// Transparent 44px targets remain available for touch/keyboard and pending clicks.
internal static class GalleryEdgeMotion
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(700);
    internal static bool IsVisible(TimeSpan? elapsed, bool keyboardFocused = false) => keyboardFocused
        || elapsed is TimeSpan age && age >= TimeSpan.Zero && age < Lifetime;
}
