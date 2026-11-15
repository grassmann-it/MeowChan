using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Gallery;

namespace MeowChan;

// Interaction audit 1.1.0: a double click on a tile opens the gallery at that tile (the same as Enter on it), never from the tile's
// star, its chips or its links and never from empty grid space. Ctrl+wheel (⌘+wheel on macOS) and Ctrl+Plus / Ctrl+Minus step the
// tile size of the Layout menu (Small, Medium, Large) and save it like the menu does; nothing outside these three sizes exists.
public sealed partial class MediaPage
{
    private readonly WheelSteps _tileSizeWheel = new();
    private readonly MiddleClickTracker _tileMiddleClick = new();

    private void InstallTileInteraction()
    {
        _grid.DoubleTapped += OnTileDoubleTapped;
        _grid.AddHandler(PointerWheelChangedEvent, OnTileSizeWheel, RoutingStrategies.Tunnel);
        _grid.AddHandler(KeyDownEvent, OnTileSizeKey, RoutingStrategies.Tunnel);
        // Audit V5: the middle click on a tile opens its folder, the menu's "Show in folder".
        _grid.AddHandler(PointerPressedEvent, OnTileMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _grid.AddHandler(PointerReleasedEvent, OnTileMiddleReleased, RoutingStrategies.Tunnel);
    }

    private MediaLibraryTileRow? MiddleClickRow(object? source) =>
        ItemActivation.IsOwnedByInteractivePart(source, _grid) ? null : RowAt(source);

    private void OnTileMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!MiddleClickTracker.IsPress(e, _grid)) { _tileMiddleClick.Cancel(); return; }
        _tileMiddleClick.Pressed(MiddleClickRow(e.Source));
    }

    private void OnTileMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Handled || !MiddleClickTracker.IsRelease(e)) return;
        MediaLibraryTileRow? row = MiddleClickRow(e.Source);
        if (!_tileMiddleClick.Released(row) || row == null) return;
        e.Handled = true;
        OpenActions.Start(() => RunMediaCommandOnTileAsync(FolderCommandId, row), "media.grid.middleClick");
    }

    /// <summary>
    /// A menu command of <paramref name="row"/>'s medium, run by a click on that tile (the selection stays as it is). The rules are those of the menu entry and of
    /// its shortcut: a saved medium, nothing being built or busy, the host's action wired.
    /// </summary>
    internal async Task RunMediaCommandOnTileAsync(string id, MediaLibraryTileRow row)
    {
        if (_lifetime.IsCancellationRequested || _building != null || _selectionBusy || ExecuteMediaMenuActionAsync == null
            || row.Entry.Presence != MediaLibraryPresence.Present) return;
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && IsEffectivelyVisible;
        await ExecuteMediaActionAsync(id, row.Entry, Current);
    }

    internal const string FolderCommandId = "media.show-in-folder";

    private void OnTileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!ItemActivation.IsItemDoubleClick(e, _grid) || RowAt(e.Source) is not { } row) return;
        e.Handled = true;
        OpenActions.Start(() => OpenTileAsync(row), "media.grid.doubleClick");
    }

    /// <summary>
    /// Opens the gallery at <paramref name="row"/>, the tile that was double-clicked (not whatever else is selected), with the window
    /// around it like Enter on one tile. The tile becomes the selection first, so the gallery's return lands on it.
    /// </summary>
    internal async Task OpenTileAsync(MediaLibraryTileRow row)
    {
        if (_lifetime.IsCancellationRequested || OpenGallery == null) return;
        if (!ReferenceEquals(SelectedRow, row) || _grid.Selection.Count != 1) _grid.SelectedItem = row;
        (IReadOnlyList<MediaLibraryEntry> window, int start) = MediaLibraryView.GalleryWindow(_shown, PresentPositions, row.Index);
        if (start < 0) return;
        await OpenGallery(window, start);
    }

    /// <summary>The tile size one step larger (<paramref name="steps"/> &gt; 0) or smaller; the ends stay where they are.</summary>
    internal static MediaTileSize StepTileSize(MediaTileSize size, int steps) =>
        (MediaTileSize)Math.Clamp((int)size + Math.Sign(steps), (int)MediaTileSize.Small, (int)MediaTileSize.Large);

    /// <summary>Applies the next tile size and saves the layout (one save per step; nothing happens at the ends).</summary>
    internal bool StepTileSize(int steps)
    {
        if (steps == 0 || _lifetime.IsCancellationRequested) return false;
        MediaTileSize next = StepTileSize(_layout.TileSize, steps);
        if (next == _layout.TileSize) return false;
        ApplyLayout(_layout with { TileSize = next });
        return true;
    }

    private void OnTileSizeWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Handled || !ItemActivation.IsZoomWheel(e.KeyModifiers)) return;
        // The wheel with Ctrl never scrolls the grid: a step changes the size, the rest of a touchpad gesture waits for the next one.
        e.Handled = true;
        int steps = _tileSizeWheel.Add(e.Delta.Y, DateTime.UtcNow);
        if (steps != 0) StepTileSize(steps);
    }

    private void OnTileSizeKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || CommandRouter.IsNativeInput(e.Source, e.Key)) return;
        int steps = CommandBindings.Matches(e, TileSizeLargerCommandId) ? 1 : CommandBindings.Matches(e, TileSizeSmallerCommandId) ? -1 : 0;
        if (steps == 0) return;
        e.Handled = true;
        StepTileSize(steps);
    }

    internal const string TileSizeLargerCommandId = "media.tile-size.larger";
    internal const string TileSizeSmallerCommandId = "media.tile-size.smaller";
}
