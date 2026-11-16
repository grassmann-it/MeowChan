using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Esc closes every light-dismiss popup of the app (Tom, 16.11.2026: "it must close again with Esc, so keyboard-only works"): the flyouts without an Esc of
/// their own (the gallery's slideshow menu, the media page's layout menu, the settings' reset question, the help popover and the like) kept the keyboard
/// until a click outside. The key reaches the popup's root only when nothing inside took it, so a field that clears its text, a list that closes first or
/// a popover with its own layers (the "Rate and tag" popover) keeps its order. A popup that is no light-dismiss one (the quote preview, the hover
/// enlargement) is left alone; menus and drop-downs handle Esc themselves.
/// </summary>
internal static class PopupEscape
{
    private static bool _installed;

    /// <summary>Once per process, on the UI thread (App start). Not called by unit tests, which never open windows.</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        InputElement.KeyDownEvent.AddClassHandler<PopupRoot>(OnPopupRootKeyDown, RoutingStrategies.Bubble);
    }

    private static void OnPopupRootKeyDown(PopupRoot root, KeyEventArgs e)
    {
        if (OwningPopup(root) is not { } popup || !ClosesOnEscape(e, popup.IsOpen, popup.IsLightDismissEnabled)) return;
        e.Handled = true;
        popup.Close();
    }

    /// <summary>The popup that shows <paramref name="root"/> (its logical parent), or null.</summary>
    internal static Popup? OwningPopup(PopupRoot root) => root.Parent as Popup ?? root.GetLogicalParent() as Popup;

    /// <summary>A plain Esc nothing inside the popup took closes an open light-dismiss popup.</summary>
    internal static bool ClosesOnEscape(KeyEventArgs e, bool open, bool lightDismiss) =>
        !e.Handled && e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && open && lightDismiss;
}
