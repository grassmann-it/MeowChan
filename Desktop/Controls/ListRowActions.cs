using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

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
    /// Both gestures of the primary action from one call: a double click (see <see cref="OnDoubleClick"/>) and Enter on a focused row. A focused
    /// <c>ListBoxItem</c> consumes Enter before a bubble handler of the list can see it, so the key is taken in the tunnel phase. The row that has
    /// the focus is selected first (a focus-only move with Ctrl+arrow leaves another row selected); with the list itself focused, the selected row counts.
    /// Interactive parts of a row keep their own Enter. The action decides by itself whether it is available.
    /// </summary>
    internal static void OnActivate(ListBox list, Action primary)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(primary);
        OnDoubleClick(list, primary);
        list.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (!ItemActivation.IsPrimaryKey(e)) return;
            Control? container = ItemActivation.ContainerAt(e.Source, list);
            if (container == null)
            {
                if (!ReferenceEquals(e.Source, list) || list.SelectedItem == null) return;
            }
            else if (ItemActivation.IsOwnedByInteractivePart(e.Source, list)) return;
            e.Handled = true;
            if (container is ListBoxItem { IsSelected: false } item)
            {
                if (list.SelectionMode.HasFlag(SelectionMode.Multiple)) item.IsSelected = true; // adds to the selection, keeps the others
                else list.SelectedIndex = list.IndexFromContainer(item);
            }
            primary();
        }, RoutingStrategies.Tunnel);
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
