using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The middle click on a row or tile (audit V5): a press and a release of the middle button on the same item run that item's quick action (a thread
/// row opens its source in the browser, a media tile opens its folder). A press on one item and a release on another is a drag, not a click; the
/// buttons, chips and links inside an item keep their own meaning. The action itself is always the command the item's menu has, with that command's
/// own rules for when it is available.
/// </summary>
internal sealed class MiddleClickTracker
{
    private object? _pressed;

    /// <summary>The middle button went down on <paramref name="item"/> (null: on nothing the click could act on).</summary>
    internal void Pressed(object? item) => _pressed = item;

    /// <summary>The middle button came up on <paramref name="item"/>; true when it is the item it went down on. The press is used up either way.</summary>
    internal bool Released(object? item)
    {
        object? pressed = _pressed;
        _pressed = null;
        return pressed != null && ReferenceEquals(pressed, item);
    }

    /// <summary>Forgets a press (the surface lost the pointer or its rows were replaced).</summary>
    internal void Cancel() => _pressed = null;

    /// <summary>Whether the event is the press of the middle button, as seen from <paramref name="relativeTo"/>.</summary>
    internal static bool IsPress(PointerPressedEventArgs e, Visual relativeTo) =>
        e.GetCurrentPoint(relativeTo).Properties.PointerUpdateKind == PointerUpdateKind.MiddleButtonPressed;

    /// <summary>Whether the event is the release of the middle button.</summary>
    internal static bool IsRelease(PointerReleasedEventArgs e) => e.InitialPressMouseButton == MouseButton.Middle;

    /// <summary>
    /// The data of the row or list item a pointer event's source lies in, when the source is not an interactive part of it and the item carries a
    /// <typeparamref name="T"/>; null on empty space, a header or a button. Rows and list items only (the card list and the tables).
    /// </summary>
    internal static T? RowAt<T>(object? source, Visual surface) where T : class =>
        ItemActivation.IsOwnedByInteractivePart(source, surface) ? null : ItemActivation.ItemAt<T>(source, surface);
}
