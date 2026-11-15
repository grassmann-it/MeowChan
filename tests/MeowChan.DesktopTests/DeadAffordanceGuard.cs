using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using MeowChan.Desktop.Controls;

namespace MeowChan.DesktopTests;

/// <summary>
/// The rule of the dead-affordance audit (15.11.2026) as code: a control that is dressed as something to press (a chip, a link, an icon square, a
/// tag pill, or the hand cursor) must act. In the tree of a page it is acceptable when it is
/// an enabled <see cref="Button"/> or <see cref="ToggleButton"/>, or sits inside an enabled one or inside a list item, a menu item or a closable
/// tag (the item acts), or is a static <c>chip-badge</c>, or carries the explicit mark <see cref="WorkspaceChips.ReadOnly"/>
/// (<c>read-only</c>, an intentional decorative chip). Anything else is reported with a path, so a star, a "+N" or a count that looks
/// clickable and is not shows up here instead of in a review.
/// </summary>
internal static class DeadAffordanceGuard
{
    /// <summary>Classes that dress a control as something to press.</summary>
    internal static readonly string[] ClickableLooks =
    [
        "chip", "chip-own", "chip-available", "chip-filter", "chip-exclude", "chip-source-button", "chip-selected",
        "tag-pill", "tag-pill-assigned", "tag-pill-partial", "link", "icon-square"
    ];

    /// <summary>
    /// The offenders of the logical tree below (and including) <paramref name="root"/>, as readable lines. <paramref name="requireWiring"/> is false for a
    /// view that is built without its owner (the owner connects its buttons), so a disabled button with nothing wired yet is not an offender there.
    /// </summary>
    internal static IReadOnlyList<string> Offenders(Control root, bool requireWiring = true)
    {
        List<string> found = [];
        foreach (ILogical node in new[] { (ILogical)root }.Concat(root.GetLogicalDescendants()))
            if (node is Control control && Why(control, requireWiring) is { } reason) found.Add(Describe(control) + " — " + reason);
        return found;
    }

    private static string? Why(Control control, bool requireWiring)
    {
        string? look = ClickableLooks.FirstOrDefault(control.Classes.Contains);
        bool hand = control.Cursor is { } cursor && IsHand(cursor);
        if (look == null && !hand) return null;
        if (control.Classes.Contains(WorkspaceChips.ReadOnly) || control.Classes.Contains(WorkspaceChips.Badge)) return null;
        string what = look != null ? "class '" + look + "'" : "hand cursor";
        // A button that is enabled acts. One that is disabled (an empty page disables most of them) has to be wired to something, so that it
        // can act once its data is there.
        if (control is Button or ToggleButton)
            return control.IsEnabled || !requireWiring || Wired(control) ? null : what + " on a disabled " + control.GetType().Name + " nothing is wired to";
        if (Handles(control, PointerHandlers)) return null;
        if (control.GetLogicalAncestors().Any(Acts)) return null;
        return what + " on a " + control.GetType().Name + " that does not act";
    }

    // The events through which a control that is not a button acts on a click or a key.
    private static readonly Avalonia.Interactivity.RoutedEvent[] PointerHandlers =
    [
        InputElement.PointerPressedEvent, InputElement.PointerReleasedEvent, InputElement.TappedEvent, InputElement.DoubleTappedEvent,
        InputElement.KeyDownEvent
    ];

    private static readonly System.Reflection.FieldInfo? EventHandlers =
        typeof(Avalonia.Interactivity.Interactive).GetField("_eventHandlers", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

    /// <summary>Whether something is subscribed to one of the routed events on this very control.</summary>
    internal static bool Handles(Control control, params Avalonia.Interactivity.RoutedEvent[] events)
    {
        if (EventHandlers?.GetValue(control) is not System.Collections.IDictionary handlers) return false;
        foreach (Avalonia.Interactivity.RoutedEvent routed in events)
            if (handlers.Contains(routed) && handlers[routed] is System.Collections.ICollection { Count: > 0 }) return true;
        return false;
    }

    // A button something is wired to: a command, a click handler, a flyout, or a button class of its own (it answers the click itself).
    private static bool Wired(Control control)
    {
        if (control.GetType() != typeof(Button) && control.GetType() != typeof(ToggleButton)) return true;
        if (control is Button { Command: not null } or Button { Flyout: not null }) return true;
        return Handles(control, Button.ClickEvent, ToggleButton.IsCheckedChangedEvent);
    }

    // An ancestor that acts for what is inside it: a button that acts, an item of a list, menu or tab strip, a closable tag, or a
    // clickable-looking control with a pointer handler of its own (the frame around a picture).
    private static bool Acts(ILogical ancestor) => ancestor switch
    {
        Button or ToggleButton => ((Control)ancestor).IsEnabled || Wired((Control)ancestor),
        ListBoxItem or ComboBoxItem or MenuItem or TabItem or Ursa.Controls.ClosableTag => true,
        Control control => (control.Cursor is { } cursor && IsHand(cursor) || ClickableLooks.Any(control.Classes.Contains)) && Handles(control, PointerHandlers),
        _ => false
    };

    // Cursor keeps the platform cursor it was made from in an internal member; the factory below is what makes it readable here.
    private static readonly System.Reflection.PropertyInfo? PlatformImpl =
        typeof(Cursor).GetProperty("PlatformImpl", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

    private static bool IsHand(Cursor cursor) => PlatformImpl?.GetValue(cursor) is RecordingCursorFactory.Impl { Type: StandardCursorType.Hand };

    /// <summary>
    /// A platform cursor factory for tests without a window: it hands out cursors that remember which standard cursor they are, so the guard
    /// can tell a hand from an arrow. Bind it with <see cref="AvaloniaLocator"/> before the page is built.
    /// </summary>
    internal sealed class RecordingCursorFactory : ICursorFactory
    {
        public ICursorImpl GetCursor(StandardCursorType cursorType) => new Impl(cursorType);
        public ICursorImpl CreateCursor(Avalonia.Media.Imaging.Bitmap cursor, PixelPoint hotSpot) => new Impl(null);
        internal sealed class Impl(StandardCursorType? type) : ICursorImpl
        {
            internal StandardCursorType? Type { get; } = type;
            public void Dispose() { }
        }
    }

    private static string Describe(Control control)
    {
        List<string> path = [];
        for (ILogical? node = control; node != null; node = node.LogicalParent)
        {
            if (node is not Control c) continue;
            string name = !string.IsNullOrEmpty(c.Name) ? c.GetType().Name + "#" + c.Name : c.GetType().Name;
            path.Add(name);
            if (path.Count >= 5) break;
        }
        path.Reverse();
        return string.Join(" > ", path) + (control.Classes.Count > 0 ? " [" + string.Join(" ", control.Classes) + "]" : "");
    }
}
