using System;
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
// panel they write the medium it shows. A grouped tile, a file that is not saved and a tile whose state is not known yet are left alone. Each write is an
// EXPLICIT target (M sets, U clears, F sets or clears relative to the effective value: the pending target when a write is pending, else the saved state) through
// the shared media actions "favorites.add" / "favorites.remove", never a flip of a possibly stale row state, so a repeated or held key cannot undo itself.
// A held key acts once (HeldKeyLatch, released by the key-up or a lost activation) and a key pressed while the last key write runs is ignored.
public partial class MediaPage
{
    /// <summary>The last favourite key's writes (tests and smokes wait for it).</summary>
    internal Task FavoriteKeyTask { get; private set; } = Task.CompletedTask;

    /// <summary>The target of a favourite write that is pending for this tile (the host's queue); null when none is. The keys read it before the row's saved state.</summary>
    internal Func<MediaLibraryEntry, bool?>? PendingFavorite { get; set; }

    private readonly HeldKeyLatch _favoriteKeyLatch = new();

    /// <summary>A favourite key in the grid: true when it was one (handled, even when nothing changes).</summary>
    private bool TryRouteGridFavoriteKey(KeyEventArgs e)
    {
        FavoriteKeyAction action = FavoriteKeys.Classify(id => CommandBindings.Matches(e, id));
        if (action == FavoriteKeyAction.None) return false;
        e.Handled = true;
        if (FavoriteKeyIsRepeat(e)) return true;
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
        if (FavoriteKeyIsRepeat(e)) return true;
        if (PanelFavoriteAvailable()) FavoriteKeyTask = WriteFavoriteKeyAsync(action, new[] { row });
        return true;
    }

    /// <summary>
    /// True when this key press must not act: the key is held (a repeat of a press that already acted) or the last favourite key's write is still running.
    /// The latch is taken even then, so a key held through the write does not act when it ends.
    /// </summary>
    private bool FavoriteKeyIsRepeat(KeyEventArgs e)
    {
        _favoriteKeyLatch.Observe(this);
        return _favoriteKeyLatch.IsRepeat(e.Key) || !FavoriteKeyTask.IsCompleted;
    }

    /// <summary>
    /// The favourite state of a tile as the keys see it: null when it cannot be written (group, not saved, unknown); otherwise the effective value, the
    /// pending target when <paramref name="pending"/> knows one, else the row's saved state (which is stale until the page refreshes).
    /// </summary>
    internal static bool? FavoriteKeyState(MediaLibraryTileRow row, Func<MediaLibraryEntry, bool?>? pending = null)
    {
        bool? saved = row.Tile.IsGroup || row.Entry.Presence != MediaLibraryPresence.Present ? null
            : row.State?.Favorite switch { MediaLibraryFlag.Yes => true, MediaLibraryFlag.No => false, _ => null };
        return saved == null ? null : pending?.Invoke(row.Entry) ?? saved;
    }

    private async Task WriteFavoriteKeyAsync(FavoriteKeyAction action, IReadOnlyList<MediaLibraryTileRow> rows)
    {
        bool?[] effective = rows.Select(row => FavoriteKeyState(row, PendingFavorite)).ToArray();
        foreach ((int index, bool selected) in FavoriteKeys.Writes(action, effective))
        {
            if (_lifetime.IsCancellationRequested) return;
            await SetTileFavoriteAsync(rows[index].Entry, selected);
        }
    }

    /// <summary>The explicit favourite write of a key: the shared media action "favorites.add" or "favorites.remove" for the shown occurrence.</summary>
    private Task SetTileFavoriteAsync(MediaLibraryEntry entry, bool selected)
    {
        if (ExecuteMediaMenuActionAsync is not { } execute || _lifetime.IsCancellationRequested) return Task.CompletedTask;
        object? rows = _grid.ItemsSource;
        return execute(selected ? FavoriteKeys.AddId : FavoriteKeys.RemoveId, entry, () => !_lifetime.IsCancellationRequested && ReferenceEquals(rows, _grid.ItemsSource));
    }
}
