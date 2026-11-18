using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan;

// Enter on the focused row of the watch list (goal delta P2-3 audit). A real Enter is raised on the row's own ListBoxItem, and Avalonia 12 lets that item
// consume it for its selection before the list's plain bubble KeyDown handler (OnWatchKeyDown) sees it. This tunnel handler takes Enter for the row's own
// container, selects that row when the focus had moved without selecting, and runs the same action as the handler and the "Open watch" button. Anything
// else keeps the existing route; the source cards' items do not consume Enter (SourcesItemEnterTests).
public partial class SourcesPage
{
    private void InstallWatchEnter()
    {
        ListBox list = this.FindControl<ListBox>("SourceWatchesList")!;
        list.AddHandler(KeyDownEvent, OnWatchRowEnter, RoutingStrategies.Tunnel);
        list.AddHandler(KeyDownEvent, OnWatchContextKey, RoutingStrategies.Tunnel);
    }

    // The rebindable "Open context menu" (Shift+F10 and the Menu key by default) opens the watch row's menu like on the other lists. Avalonia's
    // explicit Open skips the menu's Opening event, so the entries are composed first and the menu opens only with something to show. A
    // Shift+F10 that was rebound must not open it through the old native accelerator.
    private void OnWatchContextKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || sender is not ListBox list) return;
        if (!CommandBindings.Matches(e, "menu.context-open"))
        {
            if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift) e.Handled = true;
            return;
        }
        e.Handled = true;
        if (e.Source is ListBoxItem { DataContext: SourceWatchRow row } item
            && ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), list) && !ReferenceEquals(list.SelectedItem, row))
            list.SelectedItem = row;
        if (list.SelectedItem is not SourceWatchRow || list.ContextMenu is not { } menu) return;
        BuildWatchContextMenu(list);
        if (menu.ItemCount > 0) menu.Open(list);
    }

    private void OnWatchRowEnter(object? sender, KeyEventArgs e)
    {
        if (!ItemActivation.IsPrimaryKey(e) || sender is not ListBox list || e.Source is not ListBoxItem item
            || item.DataContext is not SourceWatchRow row || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), list)) return;
        if (!ReferenceEquals(list.SelectedItem, row)) list.SelectedItem = row;
        OnWatchKeyDown(list, e);
    }
}
