using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan;

// What the media tiles' own parts ask of the page (dead-affordance audit, package D): the tag editor for the "+N" link and the badge zones, and
// the guard that leaves a right click or the Menu key on a tag chip to the chip's own menu.
public sealed partial class MediaPage
{
    // "+N" and the untagged mark / tag count of a tile: the tile's "Tag…" (the quick tag popover on the tile, or the reason when it cannot be tagged).
    private void EditTagsOfTile(MediaLibraryTileRow row, Control anchor)
    {
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy) return;
        _ = RunTileTagEditorAsync(row, anchor);
    }

    private async Task RunTileTagEditorAsync(MediaLibraryTileRow row, Control anchor)
    {
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible;
        try { await OpenTagsAsync(row.Entry, row, anchor, Current); }
        catch (Exception error) when (UnhandledErrors.TryRecover(error, "media.tile.tags")) { /* Logged and shown. */ }
    }

    // A tag chip of a tile (a button with its own menu): the grid's shared tile menu leaves its right click and Menu key alone.
    private bool IsTileTagChip(object? source) => source is Visual visual
        && visual.GetSelfAndVisualAncestors().TakeWhile(node => !ReferenceEquals(node, _grid)).OfType<Button>().Any(WorkspaceChips.HasMenu);
}
