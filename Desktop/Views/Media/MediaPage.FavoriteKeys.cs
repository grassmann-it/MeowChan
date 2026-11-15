using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Input;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Gallery;

namespace MeowChan;

// The favourite keys of the Media page (Tom's decision of 15.11.2026, defaults revision 20): M adds, U removes, F toggles (FavoriteKeys). In the grid they
// write every selected tile (U only the selected favourites, M only the others, F removes all when all are favourites and otherwise adds the rest); in the
// panel they write the medium it shows. A grouped tile, a file that is not saved and a tile whose state is not known yet are left alone. Each write is the
// shared media action "favorites.toggle" (ToggleTileFavoriteAsync), the same as the tile's star, the panel's star and the menu's entry.
public partial class MediaPage
{
    /// <summary>The last favourite key's writes (tests and smokes wait for it).</summary>
    internal Task FavoriteKeyTask { get; private set; } = Task.CompletedTask;

    /// <summary>A favourite key in the grid: true when it was one (handled, even when nothing changes).</summary>
    private bool TryRouteGridFavoriteKey(KeyEventArgs e)
    {
        FavoriteKeyAction action = FavoriteKeys.Classify(id => CommandBindings.Matches(e, id));
        if (action == FavoriteKeyAction.None) return false;
        e.Handled = true;
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy || ExecuteMediaMenuActionAsync == null) return true;
        FavoriteKeyTask = WriteFavoriteKeyAsync(action, _grid.Selection.SelectedItems.OfType<MediaLibraryTileRow>().ToArray());
        return true;
    }

    /// <summary>A favourite key with the focus in the media panel: the medium the panel shows.</summary>
    private bool TryRoutePanelFavoriteKey(KeyEventArgs e)
    {
        FavoriteKeyAction action = FavoriteKeys.Classify(id => CommandBindings.Matches(e, id));
        if (action == FavoriteKeyAction.None || CommandRouter.IsNativeInput(e.Source, e.Key) || _panelRow is not { } row) return false;
        e.Handled = true;
        if (PanelFavoriteAvailable()) FavoriteKeyTask = WriteFavoriteKeyAsync(action, new[] { row });
        return true;
    }

    /// <summary>The favourite state of a tile as the keys see it: null when it cannot be written (group, not saved, unknown).</summary>
    internal static bool? FavoriteKeyState(MediaLibraryTileRow row) =>
        row.Tile.IsGroup || row.Entry.Presence != MediaLibraryPresence.Present ? null
        : row.State?.Favorite switch { MediaLibraryFlag.Yes => true, MediaLibraryFlag.No => false, _ => null };

    private async Task WriteFavoriteKeyAsync(FavoriteKeyAction action, IReadOnlyList<MediaLibraryTileRow> rows)
    {
        bool?[] marked = rows.Select(FavoriteKeyState).ToArray();
        foreach (int index in FavoriteKeys.Targets(action, marked))
        {
            if (_lifetime.IsCancellationRequested) return;
            await ToggleTileFavoriteAsync(rows[index].Entry);
        }
    }
}
