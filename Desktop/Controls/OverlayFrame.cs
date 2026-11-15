using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The dialog language for in-window scrim overlays (modals plan MD4): a header bar with the title and the cross at the right edge,
/// like <see cref="DialogWindow"/> (Tom's decision 15.11.2026: an overlay card looks like a dialog, so its × stands where a dialog's
/// stands; the cross is the <see cref="HeaderBar"/> end slot, <see cref="HeaderBar.MakeClose"/>), one central Escape policy
/// and the keyboard trap with focus return. Only the genuine way back of a PAGE ("← Back to …" of the reader, the rule assistant, the
/// plugin and source details) stays at the far left (<see cref="HeaderBar.IsBackProperty"/>); an overlay never has a back slot, so it has no
/// back gap and no separator either. The window dialogs get the same from <see cref="DialogWindow"/> and the caption's ×;
/// this is the counterpart for what opens inside a window: the reader's link confirmation, the HTML export, the archive filter
/// surface, the statistics reset question and the shell help.
/// </summary>
/// <remarks>
/// <para>The cross always cancels or closes and never confirms. An overlay whose footer Close doubles as Cancel while work runs (the
/// HTML export) hands the same close action to its cross, so cross, footer button and Escape do exactly one thing.</para>
/// <para>Escape is fixed and does not go through the shortcut service (like <see cref="DialogEscape"/>, whose decision it reuses):
/// first cancel what is in progress locally, then close, and never close while busy. The owners keep their own shell-level
/// dismissal (<c>TryDismissOverlay</c>) for the case that focus is elsewhere; both end in the same close action.</para>
/// <para>Frames are built from an overlay's own parts (XAML or code): <see cref="Header"/> for a header that is built in code,
/// <see cref="MakeClose"/> for one that stands in XAML, <see cref="Attach"/> for Escape, Tab cycle and focus return.</para>
/// </remarks>
internal static class OverlayFrame
{
    internal const string HeaderName = "OverlayFrameHeader";

    /// <summary>Header inset of an overlay card (16 left and right, 16 above, 12 below), the same room the shell help overlay uses.</summary>
    internal static readonly Thickness HeaderInset = new(16, 16, 16, 12);

    /// <summary>Tooltip of a close cross: its name and the key that does the same, e.g. "Close · Esc". Esc is a key name and stays as it is.</summary>
    internal static string CloseTip(string name) => name + " · Esc";

    /// <summary>
    /// Turns <paramref name="close"/> into the overlay's cross: the drawn × on an input-height square with <paramref name="name"/>
    /// as accessible name and "name · Esc" as tooltip. Calling it again only renames it (language change, "Cancel" while busy).
    /// </summary>
    internal static void MakeClose(Button close, string name) => HeaderBar.MakeClose(close, name, CloseTip(name));

    /// <summary>
    /// The header row built in code: <paramref name="title"/>, which wraps, then <paramref name="close"/> alone at the right edge (end slot,
    /// last in the row's tab order, the bar's 16 dip gap between them), like the × of a dialog's caption.
    /// </summary>
    internal static HeaderBar Header(TextBlock title, Button close, string closeName)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(close);
        title.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        title.VerticalAlignment = VerticalAlignment.Center;
        if (!title.Classes.Contains("page-title") && !title.Classes.Contains("section-title")) title.Classes.Add("section-title");
        MakeClose(close, closeName);
        HeaderBar bar = HeaderBar.Create(null, title, close);
        bar.Name = HeaderName;
        return bar;
    }

    /// <summary>The decision of one key press on an overlay; <see cref="DialogEscape"/> is the one policy for windows and overlays.</summary>
    internal static DialogEscapeOutcome DecideEscape(Key key, KeyModifiers modifiers, bool handled, Func<bool>? tryCancelLocal = null, Func<bool>? isBusy = null) =>
        DialogEscape.Decide(key, modifiers, handled, tryCancelLocal, isBusy);

    /// <summary>
    /// Applies <see cref="DecideEscape"/> to <paramref name="e"/>: a plain Escape is handled here, and <paramref name="close"/> runs
    /// only when nothing local took it and the overlay is not busy. Returns whether the key was an Escape this overlay consumed.
    /// </summary>
    internal static bool TryHandleEscape(KeyEventArgs e, Action close, Func<bool>? tryCancelLocal = null, Func<bool>? isBusy = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        ArgumentNullException.ThrowIfNull(close);
        DialogEscapeOutcome outcome = DecideEscape(e.Key, e.KeyModifiers, e.Handled, tryCancelLocal, isBusy);
        if (outcome == DialogEscapeOutcome.Ignored) return false;
        e.Handled = true;
        if (outcome == DialogEscapeOutcome.Close) close();
        return true;
    }

    /// <summary>
    /// Whether closing an overlay returns focus to where it came from: only when the focus still sits inside the closed overlay (or
    /// nowhere), and the place it came from can take it. Focus that moved elsewhere on its own is never pulled back.
    /// </summary>
    internal static bool ShouldReturnFocus(bool focusInsideOverlay, bool focusIsNowhere, bool originCanTakeFocus) =>
        originCanTakeFocus && (focusInsideOverlay || focusIsNowhere);

    /// <summary>
    /// Makes <paramref name="overlay"/> (the scrim border) a keyboard modal: Tab cycles inside it, Escape closes it through
    /// <paramref name="close"/> (<see cref="TryHandleEscape"/>), and focus returns to where it came from when it hides. The place it came
    /// from is the element that held focus when the overlay opened, or <see cref="Scope.ReturnTo"/> when the owner names it.
    /// </summary>
    internal static Scope Attach(Control overlay, Action close, Func<bool>? tryCancelLocal = null, Func<bool>? isBusy = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        ArgumentNullException.ThrowIfNull(close);
        Scope scope = new(overlay);
        KeyboardNavigation.SetTabNavigation(overlay, KeyboardNavigationMode.Cycle);
        overlay.AddHandler(InputElement.KeyDownEvent, (_, e) => TryHandleEscape(e, close, tryCancelLocal, isBusy), RoutingStrategies.Bubble);
        overlay.PropertyChanged += (_, e) =>
        {
            if (e.Property != Visual.IsVisibleProperty) return;
            if (e.GetNewValue<bool>()) scope.Opened(); else scope.Closed();
        };
        return scope;
    }

    /// <summary>The focus bookkeeping of one attached overlay.</summary>
    internal sealed class Scope
    {
        private readonly Control _overlay;
        private Control? _origin;

        internal Scope(Control overlay) => _overlay = overlay;

        /// <summary>Where focus goes on closing, when the owner knows better than "whatever was focused" (e.g. the link that was activated). Set before opening.</summary>
        internal Control? ReturnTo { get; set; }

        internal Control? Origin => _origin;

        internal void Opened() => _origin = ReturnTo ?? (TopLevel.GetTopLevel(_overlay)?.FocusManager?.GetFocusedElement() as Control);

        internal void Closed()
        {
            Control? origin = _origin;
            _origin = null;
            ReturnTo = null;
            if (origin == null) return;
            object? focused = TopLevel.GetTopLevel(origin)?.FocusManager?.GetFocusedElement();
            bool inside = focused is Visual visual && visual.GetSelfAndVisualAncestors().Contains(_overlay);
            if (ShouldReturnFocus(inside, focused == null, origin.IsEffectivelyVisible)) origin.Focus();
        }
    }
}
