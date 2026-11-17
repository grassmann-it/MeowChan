using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;

namespace MeowChan;

// Enter on a focused media tile (goal delta P2-3). A real Enter is raised on the focused tile's own ListBoxItem, and Avalonia 12 lets that item consume
// Enter/Space for its selection before a plain bubble handler on the grid ever sees the key. This tunnel handler therefore takes Enter for the tile's own
// container, the keyboard form of the double click: it opens the same gallery window, and a focused tile that is not selected becomes the selection first
// (as with a double click). Anything inside the tile (the star, a tag chip, a link) has another event source and keeps its own Enter; an Enter raised on the
// list itself keeps the bubble branch of OnGridKeyDown.
public sealed partial class MediaPage
{
    // Registered before every other tunnel handler of the grid: Avalonia runs the tunnel handlers of one element in reverse registration order, so this one
    // sees the key last, after the context-menu, tile-size and panel handlers had their chance to handle it (they set Handled, which ends it here).
    private void InstallTileEnter() => _grid.AddHandler(KeyDownEvent, OnTileEnterKeyDown, RoutingStrategies.Tunnel);

    private void OnTileEnterKeyDown(object? sender, KeyEventArgs e)
    {
        if (!IsOpenKey(e) || e.Source is not ListBoxItem item || item.DataContext is not MediaLibraryTileRow row) return;
        if (_lifetime.IsCancellationRequested || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(item), _grid)) return;
        int index = _grid.IndexFromContainer(item);
        if (index < 0) return;
        e.Handled = true;
        // A focused tile that is not part of the selection (Ctrl+arrow moved the focus only) becomes the selection, like a double click on it.
        if (!_grid.Selection.IsSelected(index)) _grid.SelectedItem = row;
        // Several selected tiles open together; one opens around itself.
        OpenActions.Start(() => _grid.Selection.Count > 1
            ? OpenSelectionGalleryAtAsync(row.Tile.Entry) : OpenRowAsync(row), "media.grid.enter");
    }

    // A single effective command: removing or rebinding it removes both default keys.
    private static bool IsOpenKey(KeyEventArgs e) => !e.Handled && CommandBindings.Matches(e, GalleryOpenCommandId);

    internal const string MediaOpenCommandId = "gallery.open", GalleryOpenCommandId = "gallery.open";

    private Task OpenTileEnterAsync() => _grid.Selection.Count > 1 ? OpenSelectionGalleryAsync() : OpenSelectedAsync();
}
