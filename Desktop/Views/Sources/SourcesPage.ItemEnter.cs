using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan;

// Enter on the focused row of the watch list (goal delta P2-3 audit). A real Enter is raised on the row's own ListBoxItem, and Avalonia 12 lets that item
// consume it for its selection before the list's plain bubble KeyDown handler (OnWatchKeyDown) sees it. This tunnel handler takes Enter for the row's own
// container, selects that row when the focus had moved without selecting, and runs the same action as the handler and the "Open watch" button. Anything
// else keeps the existing route; the source cards' items do not consume Enter (SourcesItemEnterTests).
public partial class SourcesPage
{
    private void InstallWatchEnter() =>
        this.FindControl<ListBox>("SourceWatchesList")!.AddHandler(KeyDownEvent, OnWatchRowEnter, RoutingStrategies.Tunnel);

    private void OnWatchRowEnter(object? sender, KeyEventArgs e)
    {
        if (!ItemActivation.IsPrimaryKey(e) || sender is not ListBox list || e.Source is not ListBoxItem item
            || item.DataContext is not SourceWatchRow row || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), list)) return;
        if (!ReferenceEquals(list.SelectedItem, row)) list.SelectedItem = row;
        OnWatchKeyDown(list, e);
    }
}
