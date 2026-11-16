using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// 16.11.: the reader's Media tab chooses Tiles or Table with the Archive results bar's joined pair (ToggleButton.segment: icon before text,
// the one that is on highlighted), not with one button that names the other view. The saved choice is unchanged (Config.ArchiveMediaTiles:
// true = Tiles). The pair never holds anything of its own: _mediaTiles is the truth and SyncMediaLayoutSwitch writes it to both buttons,
// whoever changed it (a click, Space or Enter on a focused button, the command, or the preference being loaded).
public partial class ArchivePage
{
    private CommandRouter? _mediaViewCommands;

    private void InitializeMediaLayoutSwitch()
    {
        LocalizeMediaLayoutSwitch();
        SyncMediaLayoutSwitch();
        // The command "Choose media view" (archive.media-view) keeps its meaning of one switch: a key a user bound to it flips Tiles and Table,
        // as it did with the single button. With two buttons carrying the same command the generic visible-action route could not pick one.
        Control pane = ArchiveReaderPane;
        pane.AttachedToVisualTree += (_, _) =>
        {
            _mediaViewCommands?.Dispose(); _mediaViewCommands = new(pane);
            _mediaViewCommands.Register(new("archive.media-view", pane, () => pane.IsEffectivelyVisible && ArchiveMediaTab.IsSelected,
                () => !_ended, () => OpenActions.Start(() => SetMediaLayoutAsync(!_mediaTiles), "archive.media-layout"), Priority: 100));
        };
        pane.DetachedFromVisualTree += (_, _) => { _mediaViewCommands?.Dispose(); _mediaViewCommands = null; };
        pane.KeyDown += (_, e) => _mediaViewCommands?.Route(e);
    }

    private void LocalizeMediaLayoutSwitch()
    {
        ArchiveMediaLayoutTiles.Content = Texts.T("gallery.tiles");
        ArchiveMediaLayoutTable.Content = Texts.T("gallery.table");
    }

    /// <summary>The view the reader's Media tab shows: tiles or the table (the saved choice, loaded or changed here).</summary>
    internal bool MediaShowsTiles => _mediaTiles;

    // Writes _mediaTiles to everything that shows it: the list that is visible, Columns (the table's), and the pair's highlight.
    private void SyncMediaLayoutSwitch()
    {
        ArchiveMediaGrid.IsVisible = !_mediaTiles;
        ArchiveMediaTiles.IsVisible = _mediaTiles;
        ArchiveMediaColumnsButton.IsVisible = !_mediaTiles;
        SyncMediaLayoutHighlight();
    }

    // The pair alone: it follows the preference even before the page is initialized (the lists are laid out when it is).
    private void SyncMediaLayoutHighlight()
    {
        ArchiveMediaLayoutTiles.IsChecked = _mediaTiles;
        ArchiveMediaLayoutTable.IsChecked = !_mediaTiles;
    }

    private void OnMediaLayoutClick(object? sender, RoutedEventArgs e)
    {
        bool tiles = ReferenceEquals(sender, ArchiveMediaLayoutTiles);
        OpenActions.Start(() => SetMediaLayoutAsync(tiles), "archive.media-layout");
    }

    internal async System.Threading.Tasks.Task SetMediaLayoutAsync(bool tiles)
    {
        if (tiles == _mediaTiles)
        {
            // A button that is on and is pressed again stays on (the click unchecked it): the pair shows the truth again.
            SyncMediaLayoutSwitch();
            return;
        }
        MediaPosition? position = CaptureMediaPosition();
        _mediaTiles = tiles;
        SyncMediaLayoutSwitch();
        RestoreMediaPosition(position);
        try { if (SaveMediaTiles != null) await SaveMediaTiles(_mediaTiles); }
        catch (Exception exception)
        {
            MediaTilesPreference = !_mediaTiles;
            Message(exception.Message, true);
        }
    }
}
