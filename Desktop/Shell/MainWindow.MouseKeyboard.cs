using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;

namespace MeowChan;

// Downloads lists, mouse and keyboard quick wins: type-ahead search of the thread and board tables (audit V7) and the middle click on a row, which opens the
// row's source in the browser like the menu's "Open source in browser" (audit V5).
public partial class MainWindow
{
    private readonly MiddleClickTracker _downloadMiddleClick = new();

    // The rules of the list's key commands (OnDownloadListKeyDown): the Downloads page itself, nothing over it, no row command running.
    private bool DownloadListAcceptsQuickInput() =>
        !_isClosing && !_sessionRowsApplying && _restoreCompleted && IsEnabled && _selectedPage == WorkspacePage.Downloads
        && !_largeReaderOpen && (_gallery == null || _gallerySuspended) && !RowCommandBusy && !_selectionCommandRunning;

    private void InitializeDownloadTypeAhead()
    {
        foreach (bool boards in new[] { false, true })
        {
            DataGrid grid = DownloadList(boards);
            TypeAheadSearch.AttachDataGrid(grid, DownloadRowLabel, source => IsListCommandSource(source, grid), DownloadListAcceptsQuickInput);
        }
    }

    /// <summary>The text of a download row the type-ahead search compares: a thread's title, a board's name.</summary>
    internal static string? DownloadRowLabel(object row) => row switch
    {
        ThreadRow thread => thread.Title,
        BoardRow board => string.IsNullOrWhiteSpace(board.NiceName) ? board.BoardCode : board.NiceName,
        _ => null
    };

    private void InitializeDownloadMiddleClick()
    {
        foreach (bool boards in new[] { false, true })
        {
            DataGrid grid = DownloadList(boards);
            grid.AddHandler(InputElement.PointerPressedEvent, OnDownloadMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            grid.AddHandler(InputElement.PointerReleasedEvent, OnDownloadMiddleReleased, RoutingStrategies.Tunnel);
        }
    }

    private object? DownloadRowAt(object? source, DataGrid grid) =>
        ReferenceEquals(grid, DownloadList(true)) ? MiddleClickTracker.RowAt<BoardRow>(source, grid) : MiddleClickTracker.RowAt<ThreadRow>(source, grid);

    private void OnDownloadMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid || !MiddleClickTracker.IsPress(e, grid)) { _downloadMiddleClick.Cancel(); return; }
        _downloadMiddleClick.Pressed(DownloadRowAt(e.Source, grid));
    }

    private void OnDownloadMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not DataGrid grid || !MiddleClickTracker.IsRelease(e)) return;
        object? row = DownloadRowAt(e.Source, grid);
        if (!_downloadMiddleClick.Released(row) || !DownloadListAcceptsQuickInput()) return;
        // The menu's rule for a row that is no longer in the list (it was removed or replaced between press and release): nothing opens.
        string? target = row switch
        {
            ThreadRow thread when _threadRows.TryGetValue(thread.WatchId, out ThreadRow? current) && ReferenceEquals(current, thread) => thread.Url,
            BoardRow board when _boardRows.TryGetValue(board.WatchId, out BoardRow? current) && ReferenceEquals(current, board) => board.Url,
            _ => null
        };
        if (string.IsNullOrWhiteSpace(target)) return;
        e.Handled = true;
        OpenTarget(target);
    }
}
