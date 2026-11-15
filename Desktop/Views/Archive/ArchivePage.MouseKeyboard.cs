using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;

namespace MeowChan;

// Archive page, mouse and keyboard quick wins. The middle click on a thread row of the card list and of the table opens its source in the browser (the menu's
// "Open source in browser"); on a media tile it opens the medium's folder (the media menu's "Show in folder"), audit V5. The thread table also searches by type
// (audit V7). Each uses the command the row's menu has, under that command's own rules.
public partial class ArchivePage
{
    private readonly MiddleClickTracker _threadMiddleClick = new();
    private readonly MiddleClickTracker _mediaMiddleClick = new();

    private void AttachMouseKeyboardQuickWins()
    {
        foreach (Control list in new Control[] { ArchiveThreads, ArchiveThreadTable })
        {
            list.AddHandler(PointerPressedEvent, OnThreadMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, OnThreadMiddleReleased, RoutingStrategies.Tunnel);
        }
        ArchiveMediaTiles.AddHandler(PointerPressedEvent, OnMediaTileMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ArchiveMediaTiles.AddHandler(PointerReleasedEvent, OnMediaTileMiddleReleased, RoutingStrategies.Tunnel);
        TypeAheadSearch.AttachDataGrid(ArchiveThreadTable, row => (row as ArchiveThreadRow)?.Summary.Title,
            source => MainWindow.IsListCommandSource(source, ArchiveThreadTable), ArchiveListAcceptsQuickInput);
    }

    // The rules of the list's key commands (OnArchiveListKeyDown) for the page itself: nothing over it, no search or export open, no reload running.
    private bool ArchiveListAcceptsQuickInput() =>
        !_ended && _available && !_largeReader && !_busy && !IsFilenameSearchOpen && !ArchiveExportDialog.IsVisible && !IsReaderDialogOpen;

    private void OnThreadMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control list || !MiddleClickTracker.IsPress(e, list)) { _threadMiddleClick.Cancel(); return; }
        _threadMiddleClick.Pressed(MiddleClickTracker.RowAt<ArchiveThreadRow>(e.Source, list));
    }

    private void OnThreadMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control list || e.Handled || !MiddleClickTracker.IsRelease(e)) return;
        ArchiveThreadRow? row = MiddleClickTracker.RowAt<ArchiveThreadRow>(e.Source, list);
        if (!_threadMiddleClick.Released(row) || row == null) return;
        e.Handled = true;
        OpenThreadSourceByMiddleClick(row);
    }

    /// <summary>The thread's source in the browser, by the same rule as the menu entry: the shell can open it and the thread has an address (else the page says so).</summary>
    internal bool OpenThreadSourceByMiddleClick(ArchiveThreadRow row)
    {
        if (!ArchiveListAcceptsQuickInput() || SelectionLeadAction == null) return false;
        if (string.IsNullOrWhiteSpace(row.Summary.Url)) { Message(Texts.T("archive.command.noSource")); return false; }
        RunArchiveLeadAction(row, "browser");
        return true;
    }

    private void OnMediaTileMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!MiddleClickTracker.IsPress(e, ArchiveMediaTiles)) { _mediaMiddleClick.Cancel(); return; }
        _mediaMiddleClick.Pressed(MiddleClickTracker.RowAt<ArchiveAssetRow>(e.Source, ArchiveMediaTiles));
    }

    private void OnMediaTileMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Handled || !MiddleClickTracker.IsRelease(e)) return;
        ArchiveAssetRow? asset = MiddleClickTracker.RowAt<ArchiveAssetRow>(e.Source, ArchiveMediaTiles);
        if (!_mediaMiddleClick.Released(asset) || asset == null) return;
        e.Handled = true;
        OpenMediaFolderByMiddleClick(asset);
    }

    /// <summary>
    /// The folder of a medium's file, through the media menu of that tile: the menu entry's own availability decides (the host can open folders, the file is
    /// saved), exactly as for the "Show in folder" key. False when the page is not ready or the entry is not available.
    /// </summary>
    internal bool OpenMediaFolderByMiddleClick(ArchiveAssetRow asset)
    {
        if (_ended || !_available || _busy || _thread == null || _selectedThread == null || IsReaderDialogOpen) return false;
        ContextMenu menu = BuildArchiveMediaMenu(asset, null, ArchiveMediaTiles, null, MediaMenuSurface.ArchiveTiles).Menu;
        MenuItem? folder = menu.Items.OfType<MenuItem>().FirstOrDefault(item => CommandBindings.ActionId(item) == MediaFolderCommandId);
        if (folder is not { IsEnabled: true }) return false;
        CommandBindings.InvokeAction(folder);
        return true;
    }

    internal const string MediaFolderCommandId = "media.show-in-folder";
}
