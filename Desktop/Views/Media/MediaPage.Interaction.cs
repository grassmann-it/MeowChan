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
    private TileSizeInput? _tileSizeInput;

    private void InstallTileInteraction()
    {
        _grid.DoubleTapped += OnTileDoubleTapped;
        // The shared tile size input (TileSizeInput): the same wheel and keys as the tagging grid and the archive tiles.
        _tileSizeInput = TileSizeInput.Install(_grid, TileSizeCommands.Media, () => _layout.TileSize, ApplyTileSize);
    }

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
    internal static MediaTileSize StepTileSize(MediaTileSize size, int steps) => MediaTileSizes.Step(size, steps);

    /// <summary>Applies the next tile size and saves the layout (one save per step; nothing happens at the ends).</summary>
    internal bool StepTileSize(int steps) => _tileSizeInput?.StepBy(steps) ?? false;

    /// <summary>Sets a tile size and saves the layout (Ctrl+0 sets Medium); nothing happens when it is the size already.</summary>
    internal bool SetTileSize(MediaTileSize size) => _tileSizeInput?.Set(size) ?? false;

    /// <summary>
    /// The tagging grid shares this size (one tile size for both): its input changed the setting, so the page shows it now and saves nothing
    /// again (the shell saved it).
    /// </summary>
    internal void ApplySavedTileSize(MediaTileSize size) => ApplyLayout(_layout with { TileSize = size }, save: false);

    private bool ApplyTileSize(MediaTileSize size)
    {
        if (_lifetime.IsCancellationRequested) return false;
        ApplyLayout(_layout with { TileSize = size });
        return true;
    }

    internal const string TileSizeLargerCommandId = "media.tile-size.larger";
    internal const string TileSizeSmallerCommandId = "media.tile-size.smaller";
    internal const string TileSizeResetCommandId = "media.tile-size.reset";
}
