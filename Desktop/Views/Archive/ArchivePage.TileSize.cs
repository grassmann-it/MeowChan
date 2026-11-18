using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan;

// The tile size (Small, Medium, Large) of the saved-media tiles in the reader's Media tab. Ctrl+wheel (⌘+wheel on macOS) over the tiles and the
// keys of archive.media-tile-size.* (Ctrl/Cmd+Plus, Minus, 0) step it or set Medium, through the same input as the media page and the tagging grid
// (TileSizeInput). The size is a setting of its own (Config.ArchiveMediaTileSize, default Medium): these tiles show other parts than the media
// page's tiles, so they do not follow its size. The table view has no tile size and ignores the gestures. Only the three sizes exist.
public partial class ArchivePage
{
    private MediaTileSize _tileSize = MediaTileSize.Medium;
    private TileSizeInput? _tileSizeInput;

    /// <summary>The saved size (the shell, when the page is built): shown now, nothing is saved back.</summary>
    internal MediaTileSize MediaTileSizePreference
    {
        get => _tileSize;
        set => ApplyMediaTileSize(value);
    }

    /// <summary>Saves a size the person chose; the shell writes it as a view preference of the profile.</summary>
    internal Func<MediaTileSize, Task>? SaveMediaTileSize { get; set; }

    /// <summary>The tile input of the tile view (tests and smokes).</summary>
    internal TileSizeInput? MediaTileSizeInput => _tileSizeInput;

    private void InitializeMediaTileSize()
    {
        // The panel is made with the size the page has when the list builds it; a later change updates the panel that exists (ApplyMediaTileSize).
        ArchiveMediaTiles.ItemsPanel = new FuncTemplate<Panel?>(() =>
        {
            ArchiveTileAppearance appearance = ArchiveTileMetrics.For(_tileSize);
            return new MediaTilePanel { ColumnWidth = appearance.CellWidth, ItemHeight = appearance.CellHeight };
        });
        ArchiveMediaTileView.SetTileSize(ArchiveMediaTiles, _tileSize);
        _tileSizeInput = TileSizeInput.Install(ArchiveMediaTiles, TileSizeCommands.Archive, () => _tileSize, ChooseMediaTileSize);
    }

    private bool ChooseMediaTileSize(MediaTileSize size)
    {
        if (_ended) return false;
        ApplyMediaTileSize(size);
        _ = SaveMediaTileSizeAsync(size);
        return true;
    }

    private async Task SaveMediaTileSizeAsync(MediaTileSize size)
    {
        try { if (SaveMediaTileSize != null) await SaveMediaTileSize(size); }
        catch (Exception exception) { if (!_ended) Message(exception.Message, true); }
    }

    // The realized tiles and the panel's cells change size; the rows, the selection and the pictures stay, and the row at the top of the view
    // stays at the top of the view while the cells grow or shrink.
    private void ApplyMediaTileSize(MediaTileSize size)
    {
        size = Enum.IsDefined(size) ? size : MediaTileSize.Medium;
        MediaTilePanel? panel = TilePanel();
        if (size == _tileSize && (panel == null || panel.ItemHeight == ArchiveTileMetrics.For(size).CellHeight)) return;
        (int Index, double Offset)? anchor = panel?.CaptureAnchor();
        _tileSize = size;
        ArchiveMediaTileView.SetTileSize(ArchiveMediaTiles, size);
        if (panel == null) return;
        ArchiveTileAppearance appearance = ArchiveTileMetrics.For(size);
        panel.ColumnWidth = appearance.CellWidth;
        panel.ItemHeight = appearance.CellHeight;
        panel.InvalidateMeasure();
        if (anchor is { } kept)
            Dispatcher.UIThread.Post(() =>
            {
                if (!_ended) panel.RestoreAnchor(kept.Index, -Math.Min(-kept.Offset, appearance.CellHeight - 1));
            }, DispatcherPriority.Background);
    }
}
