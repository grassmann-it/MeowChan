using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Gallery;

namespace MeowChan;

// UX review 17.11.2026 (Tom's screencast, review finding PF-05): a presentation the data asked for — the index rebuilt in the background (every
// activation does it), own state re-read (a favorite, rating or tag), the tagging queue, sound results, the language — swapped the grid's rows
// under the user's eyes. The swap reset the scroll position, the ListBox then scrolled to the restored selection, and every tile reloaded its
// preview: three content states and two jumps within a second when the Media page was opened. Such a presentation now keeps the view:
//  - before the swap the page notes the selected tile (the anchor of the selection) with its line's distance from the top of the view while it is
//    on screen, else the tile at the top of the view with its offset; after the swap that tile is found again by its bytes and put back at the
//    same distance (MediaTilePanel.RestoreAnchor). A tile that is gone gives way to its nearest neighbour of the old order that is still shown;
//    new tiles in front of it (a newer thread) therefore do not push the view.
//  - the selection itself follows the usual rule (selected tiles that are still shown stay selected, MediaPage.Selection.cs); the ListBox does not
//    scroll to it on its own during the swap.
//  - the previews the old tiles showed are handed to the tiles of the same bytes after the swap (MediaLibraryTileView.ThumbnailHandover); what is
//    not taken within the swap's layout is given back to the cache.
// A changed filter, order or search starts fresh as before (the user asked for other rows).
public sealed partial class MediaPage
{
    /// <summary>The tile whose line stays where it was (its bytes key and old position) and that line's distance from the top of the view, in dip.</summary>
    private sealed record ViewKeep(IReadOnlyList<MediaLibraryTile> OldTiles, int Pivot, double Offset);

    private Dictionary<MediaLibraryTileKey, Bitmap>? _handover;
    private bool _handoverReleasePosted;

    /// <summary>Where the grid stands before a data presentation; null when it shows no rows or is not laid out.</summary>
    private ViewKeep? CaptureView()
    {
        if (TilePanel() is not { } panel || panel.CaptureAnchor() is not { } anchor || _tiles.Count == 0 || panel.Columns <= 0) return null;
        int columns = panel.Columns;
        double height = panel.ItemHeight;
        double top = anchor.Index / columns * height - anchor.Offset; // the top of the view in the panel's coordinates
        int selected = _grid.Selection.AnchorIndex is var at && at >= 0 && at < _tiles.Count && _grid.Selection.IsSelected(at) ? at : _grid.SelectedIndex;
        if (selected >= 0 && selected < _tiles.Count && _grid.ContainerFromIndex(selected) is Control { IsVisible: true } container
            && container.TranslatePoint(default, _grid) is { } point && point.Y + container.Bounds.Height > 0 && point.Y < _grid.Bounds.Height)
            return new ViewKeep(_tiles, selected, selected / columns * height - top);
        return new ViewKeep(_tiles, anchor.Index, anchor.Offset);
    }

    /// <summary>Puts the noted tile's line back at its distance from the top of the view (or its nearest surviving neighbour's).</summary>
    private void RestoreView(ViewKeep keep)
    {
        if (TilePanel() is not { } panel || _tiles.Count == 0) return;
        int index = MediaLibraryReturn.LocateKept(keep.OldTiles, keep.Pivot, _tiles);
        if (index >= 0) panel.RestoreAnchor(index, keep.Offset);
    }

    // The ListBox scrolls to the selection's anchor on its own when its position changed (it queues that scroll); carrying the selection over in a
    // swap is not a new selection, and the view is put back right after it (RestoreView).
    private void HoldListScroll() => TilePanel()?.HoldScrollIntoView(_grid.Selection.AnchorIndex);

    /// <summary>Takes the previews the realized tiles show, for the tiles of the same bytes after the swap.</summary>
    private void HandOverThumbnails()
    {
        foreach (Control container in _grid.GetRealizedContainers())
        {
            if (container is not ContentControl { Presenter.Child: MediaLibraryTileView tile } || tile.TakeShownThumbnail() is not { } shown) continue;
            _handover ??= new();
            if (!_handover.TryAdd(MediaLibraryTileKey.Of(shown.Tile), shown.Bitmap)) MediaTileThumbnails.Release(shown.Bitmap);
        }
        if (_handover == null || _handoverReleasePosted) return;
        _handoverReleasePosted = true;
        // After the swap's layout (the tiles bound and shown then take theirs): what is left goes back to the cache.
        Dispatcher.UIThread.Post(ReleaseHandover, DispatcherPriority.Background);
    }

    private Bitmap? TakeHandedOverThumbnail(MediaLibraryTile tile) =>
        _handover is { } handover && handover.Remove(MediaLibraryTileKey.Of(tile), out Bitmap? bitmap) ? bitmap : null;

    private void ReleaseHandover()
    {
        _handoverReleasePosted = false;
        if (_handover is not { } left) return;
        _handover = null;
        foreach (Bitmap bitmap in left.Values) MediaTileThumbnails.Release(bitmap);
    }
}
