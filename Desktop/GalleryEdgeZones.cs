using System;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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

    /// <summary>
    /// Real mouse motion anywhere over the gallery brings the auto-hidden controls back (Tom, 16.11.), under the same guards as the edge reveal
    /// and only while the gallery is the active, usable surface without a dialog or popup over it.
    /// </summary>
    internal static bool MotionMayReveal(bool windowActive, bool available, bool dialogOrPopup, bool captured, bool selectedText,
        bool keyboardPanelFocus, bool helpOpen) =>
        windowActive && available && !dialogOrPopup && CanReveal(captured, selectedText, keyboardPanelFocus, helpOpen);

    /// <summary>What real mouse motion does to the controls (pure): nothing while <see cref="MotionMayReveal"/> says no, otherwise it renews the clock
    /// of the shown controls or shows the hidden ones. The clock is renewed only when motion may reveal, so motion over an inactive gallery never keeps the
    /// controls from hiding.</summary>
    internal static GalleryMotionEffect MotionEffect(bool mayReveal, bool controlsHidden) =>
        !mayReveal ? GalleryMotionEffect.None : controlsHidden ? GalleryMotionEffect.Reveal : GalleryMotionEffect.RenewClock;
}

internal enum GalleryMotionEffect { None, RenewClock, Reveal }

// Only actual mouse motion renews artwork; hover, presses and timer polling do not.
// Transparent 44px targets remain available for touch/keyboard and pending clicks.
internal static class GalleryEdgeMotion
{
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMilliseconds(700);
    internal static bool IsVisible(TimeSpan? elapsed, bool keyboardFocused = false) => keyboardFocused
        || elapsed is TimeSpan age && age >= TimeSpan.Zero && age < Lifetime;
}

/// <summary>
/// AX-03: what the two gallery edge handles (<c>GalleryControlsEdgeIndicator</c>, <c>GalleryPostEdgeIndicator</c>) look like at rest and while the
/// keyboard has focus. At rest a handle is only a faint chevron (a hint for the pointer). Keyboard focus adds a round, nearly opaque dark disc with a
/// white ring and a white chevron, as the favourite badge does over the picture. The standard accent ring of <c>Workspace.FocusAdorner</c> cannot be
/// used here: it sits on an arbitrary picture, and one accent colour is under 3:1 on a white or a black one. The disc and the ring are the two tones of
/// the indicator: on a light picture the dark disc is the contrast, on a dark picture the white ring is; the white chevron reads on the disc in both.
/// The look is drawn from the Avalonia <c>:focus-visible</c> pseudo-class (keyboard or directional focus, never a pointer press), so it needs no style.
/// The 44 dip target (the transparent root) and the hit surface stay as they were.
/// </summary>
internal static class GalleryEdgeHandleVisual
{
    internal const string FocusVisiblePseudoClass = ":focus-visible";
    internal const double RingThickness = 2;
    // Half of the 44 dip handle: the focus face is a circle that fills the whole target.
    internal const double DiscRadius = 22;

    /// <summary>The faint hint chevron at rest (39 % grey); not a focus indicator.</summary>
    internal static readonly Color RestGlyph = Color.FromArgb(100, 150, 150, 150);
    /// <summary>The disc behind the focused handle: the favourite badge's dark scrim at 90 % opacity.</summary>
    internal static readonly Color FocusDisc = Color.FromArgb(0xE6, 0x18, 0x1A, 0x1F);
    /// <summary>The ring at the rim of the disc and the chevron while focused.</summary>
    internal static readonly Color FocusRing = Colors.White;

    private static readonly IBrush RestBrush = new ImmutableSolidColorBrush(RestGlyph);
    private static readonly IBrush DiscBrush = new ImmutableSolidColorBrush(FocusDisc);
    private static readonly IBrush RingBrush = new ImmutableSolidColorBrush(FocusRing);

    /// <summary>
    /// The template content of a handle: a transparent square (the native hit surface) that holds the focus face, a circle that is empty at rest and
    /// draws the disc and ring while the button has <c>:focus-visible</c>, with the chevron on it. Exactly one <see cref="Avalonia.Controls.Shapes.Path"/>.
    /// </summary>
    internal static Border Compose(Button button, Avalonia.Controls.Shapes.Path chevron)
    {
        ArgumentNullException.ThrowIfNull(button);
        ArgumentNullException.ThrowIfNull(chevron);
        chevron.Stroke = RestBrush;
        var face = new Border
        {
            CornerRadius = new CornerRadius(DiscRadius), BorderThickness = new Thickness(RingThickness), IsHitTestVisible = false, Child = chevron
        };
        // Keep the complete transparent handle in the native hit-test map (a bare Path owns only its stroked pixels).
        var root = new Border { Background = Brushes.Transparent, Child = face };
        void Apply()
        {
            bool focused = button.Classes.Contains(FocusVisiblePseudoClass);
            face.Background = focused ? DiscBrush : null;
            face.BorderBrush = focused ? RingBrush : null;
            chevron.Stroke = focused ? RingBrush : RestBrush;
        }
        void OnClasses(object? sender, NotifyCollectionChangedEventArgs e) => Apply();
        // Listen only while the handle is in the tree, so a rebuilt template never leaves a listener on the button.
        void Listen(bool on)
        {
            button.Classes.CollectionChanged -= OnClasses;
            if (on) button.Classes.CollectionChanged += OnClasses;
        }
        root.AttachedToVisualTree += (_, _) => { Listen(true); Apply(); };
        root.DetachedFromVisualTree += (_, _) => Listen(false);
        Listen(true);
        Apply();
        return root;
    }
}
