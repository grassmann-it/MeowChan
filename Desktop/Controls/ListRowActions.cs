using System;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The two gestures every list of a dialog has beside its buttons (dead-affordance audit 15.11.): a double click on a row runs the dialog's primary
/// action, and a right-click, the Menu key or Shift+F10 opens a menu that offers the very same actions as the dialog's buttons. Both go through the
/// dialog's own action code, so neither can do something the buttons would refuse.
/// </summary>
internal static class ListRowActions
{
    /// <summary>
    /// A double click on an item of <paramref name="list"/> (not on an interactive part of it, not on the empty space under the items) runs
    /// <paramref name="primary"/>. The action decides by itself whether it is available.
    /// </summary>
    internal static void OnDoubleClick(ListBox list, Action primary)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(primary);
        list.DoubleTapped += (_, e) =>
        {
            if (!ItemActivation.IsItemDoubleClick(e, list)) return;
            e.Handled = true;
            primary();
        };
    }

    /// <summary>
    /// The context menu of <paramref name="list"/>: it opens on a right-click (the list selects the row under the pointer first), the Menu key and
    /// Shift+F10 (on the selected row); the menu is composed fresh at every opening and focus returns to the list.
    /// </summary>
    internal static ContextMenuHost AttachMenu(ListBox list, Func<ContextMenuBuilder?> compose)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(compose);
        return new ContextMenuHost(list, () => list.SelectedItem == null ? null : compose(), fallbackFocus: () => list.Focus());
    }
}
