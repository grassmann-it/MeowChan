using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    public ObservableCollection<ThreadRow> VisibleThreads { get; } = new();
    public ObservableCollection<BoardRow> VisibleBoards { get; } = new();
    private bool _refreshingDownloadSearch;
    private bool _downloadSearchOpen;
    private long _downloadSearchQueryRevision;
    private long _appliedDownloadSearchRevision = -1;
    private long _applyingDownloadSearchRevision;
    private DownloadSearch.Query? _applyingDownloadQuery;
    private DownloadSearchReconciliation<ThreadRow>? _threadSearchReconciliation;
    private DownloadSearchReconciliation<BoardRow>? _boardSearchReconciliation;
    private DownloadSearchReconciliation<ThreadRow>? _appliedThreadSearchReconciliation;
    private DownloadSearchReconciliation<BoardRow>? _appliedBoardSearchReconciliation;
    private HashSet<ThreadRow> _retainedThreadSet = new();
    private HashSet<BoardRow> _retainedBoardSet = new();
    private ThreadRow[] _retainedThreadSelection = Array.Empty<ThreadRow>();
    private BoardRow[] _retainedBoardSelection = Array.Empty<BoardRow>();
    private IEnumerator<Action>? _restoreDownloadSelection;
    private bool DownloadSearchNeedsReconciliation => _appliedDownloadSearchRevision != _downloadSearchQueryRevision;
    private long _downloadFilterRevision;
    private readonly Dictionary<long, long> _threadFilterExclusions = new();
    private readonly Dictionary<long, long> _boardFilterExclusions = new();
    public string DownloadSearchText => this.FindControl<TextBox>("DownloadSearchInput")?.Text ?? string.Empty;
    public int VisibleResultCount => this.FindControl<TabControl>("DownloadsTabs")?.SelectedIndex == 1
        ? VisibleBoards.Count : VisibleThreads.Count;
    public bool CanFocusDownloadSearch => _workspaceInitialized && _selectedPage == WorkspacePage.Downloads
        && !_isClosing && IsEnabled && !_largeReaderOpen && (_gallery == null || _gallerySuspended);

    public void FocusDownloadSearch()
    {
        if (!CanFocusDownloadSearch) return;
        _downloadSearchOpen = true;
        RefreshDownloadSearchPresentation();
        TextBox input = WorkspaceControl<TextBox>("DownloadSearchInput");
        input.Focus();
        input.SelectAll();
    }

    private void OnDownloadSearchClick(object? sender, RoutedEventArgs e) => FocusDownloadSearch();
    private void OnDownloadSearchClose(object? sender, RoutedEventArgs e)
    {
        _downloadSearchOpen = false;
        RefreshDownloadSearchPresentation();
        WorkspaceControl<Button>("DownloadSearchButton").Focus();
    }
    private void OnDownloadSearchClear(object? sender, RoutedEventArgs e)
    {
        WorkspaceControl<TextBox>("DownloadSearchInput").Text = string.Empty;
        if (_downloadSearchOpen) WorkspaceControl<TextBox>("DownloadSearchInput").Focus();
    }
    private void OnDownloadSearchChanged(object? sender, TextChangedEventArgs e)
    {
        if (!_workspaceInitialized) return;
        _downloadFilterRevision++;
        _downloadSearchQueryRevision++;
        _sessionRefresh.RequestImmediate();
    }

    private void CaptureDownloadSearchInput()
    {
        _applyingDownloadSearchRevision = _downloadSearchQueryRevision;
        _applyingDownloadQuery = new DownloadSearch.Query(DownloadSearchText);
        _retainedThreadSelection = DownloadList(false).SelectedItems.OfType<ThreadRow>().ToArray();
        _retainedBoardSelection = DownloadList(true).SelectedItems.OfType<BoardRow>().ToArray();
        _retainedThreadSet = new(_retainedThreadSelection);
        _retainedBoardSet = new(_retainedBoardSelection);
        _threadSearchReconciliation = null; _boardSearchReconciliation = null;
        _restoreDownloadSelection?.Dispose(); _restoreDownloadSelection = null;
        _refreshingDownloadSearch = true;
    }

    private void BeginDownloadSearch(DesktopSessionProjection projection)
    {
        if (_threadSearchReconciliation != null) return;
        DownloadSearch.Query literal = _applyingDownloadQuery!.Value;
        _threadSearchReconciliation = new(UrlList, VisibleThreads, row => literal.Matches(row), row =>
        {
            DownloadList(false).SelectedItems.Remove(row);
            if (_threadRows.ContainsKey(row.WatchId)) _threadFilterExclusions[row.WatchId] = ++_downloadFilterRevision;
            else _threadFilterExclusions.Remove(row.WatchId);
        }, projection.Epoch, _applyingDownloadSearchRevision);
        _boardSearchReconciliation = new(BoardsList, VisibleBoards, row => literal.Matches(row), row =>
        {
            DownloadList(true).SelectedItems.Remove(row);
            if (_boardRows.ContainsKey(row.WatchId)) _boardFilterExclusions[row.WatchId] = ++_downloadFilterRevision;
            else _boardFilterExclusions.Remove(row.WatchId);
        }, projection.Epoch, _applyingDownloadSearchRevision);
    }

    // Source rows stay frozen until filtering, reorder and selection repair all complete. Each dispatcher
    // callback advances only one plan. The current literal query never changes under an in-flight plan.
    private bool StepDownloadSearch(DesktopSessionProjection projection)
    {
        BeginDownloadSearch(projection);
        if (!_threadSearchReconciliation!.IsCompleted)
        {
            _threadSearchReconciliation.Step(64, TimeSpan.FromMilliseconds(4)); return false;
        }
        if (!_boardSearchReconciliation!.IsCompleted)
        {
            _boardSearchReconciliation.Step(64, TimeSpan.FromMilliseconds(4)); return false;
        }
        _restoreDownloadSelection ??= RestoreDownloadSelection().GetEnumerator();
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int count = 0; count < 64; count++)
        {
            if (!_restoreDownloadSelection.MoveNext())
            {
                _restoreDownloadSelection.Dispose(); _restoreDownloadSelection = null;
                _appliedDownloadSearchRevision = _applyingDownloadSearchRevision;
                _refreshingDownloadSearch = false;
                _retainedThreadSelection = Array.Empty<ThreadRow>(); _retainedBoardSelection = Array.Empty<BoardRow>();
                _applyingDownloadQuery = null;
                _appliedThreadSearchReconciliation = _threadSearchReconciliation;
                _appliedBoardSearchReconciliation = _boardSearchReconciliation;
                _threadSearchReconciliation = null; _boardSearchReconciliation = null;
                RefreshDownloadSearchPresentation();
                return true;
            }
            _restoreDownloadSelection.Current();
            if (System.Diagnostics.Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(4)) break;
        }
        return false;
    }

    private IEnumerable<Action> RestoreDownloadSelection()
    {
        DataGrid threads = DownloadList(false), boards = DownloadList(true);
        // Visit even no-op selections through the slice budget. The helpers already own O(1) inclusion sets.
        foreach (ThreadRow row in threads.SelectedItems.OfType<ThreadRow>().ToArray())
            yield return () => { if (!_retainedThreadSet.Contains(row) || !_threadSearchReconciliation!.Includes(row)) threads.SelectedItems.Remove(row); };
        foreach (BoardRow row in boards.SelectedItems.OfType<BoardRow>().ToArray())
            yield return () => { if (!_retainedBoardSet.Contains(row) || !_boardSearchReconciliation!.Includes(row)) boards.SelectedItems.Remove(row); };
        foreach (ThreadRow row in _retainedThreadSelection)
            yield return () => { if (_threadSearchReconciliation!.Includes(row) && !threads.SelectedItems.Contains(row)) threads.SelectedItems.Add(row); };
        foreach (BoardRow row in _retainedBoardSelection)
            yield return () => { if (_boardSearchReconciliation!.Includes(row) && !boards.SelectedItems.Contains(row)) boards.SelectedItems.Add(row); };
    }

    private void RefreshDownloadSearchPresentation()
    {
        if (!_workspaceInitialized) return;
        bool active = !string.IsNullOrWhiteSpace(DownloadSearchText);
        WorkspaceControl<Grid>("DownloadSearchBar").IsVisible = _downloadSearchOpen || active;
        WorkspaceControl<TextBox>("DownloadSearchInput").IsVisible = _downloadSearchOpen;
        WorkspaceControl<Button>("DownloadSearchClose").IsVisible = _downloadSearchOpen;
        WorkspaceControl<Button>("DownloadSearchClear").IsVisible = active;
        TextBlock summary = WorkspaceControl<TextBlock>("DownloadSearchSummary");
        Grid.SetColumn(summary, _downloadSearchOpen ? 1 : 0);
        Grid.SetColumnSpan(summary, _downloadSearchOpen ? 1 : 2);
        summary.MaxWidth = _downloadSearchOpen ? 120 : double.PositiveInfinity;
        summary.Text = active && !_downloadSearchOpen ? Texts.T("downloads.searchActive", VisibleResultCount, DownloadSearchText)
            : Texts.T("downloads.searchCount", VisibleResultCount);
        ToolTip.SetTip(summary, summary.Text);
        AutomationProperties.SetName(summary, summary.Text);
        bool boards = WorkspaceControl<TabControl>("DownloadsTabs").SelectedIndex == 1;
        WorkspaceControl<Grid>("ThreadsResultPanel").IsVisible = !boards;
        WorkspaceControl<Grid>("BoardsResultPanel").IsVisible = boards;
        WorkspaceControl<TextBlock>("ThreadsEmptyState").Text = Texts.T(active ? "downloads.searchEmpty" : "downloads.empty");
        WorkspaceControl<TextBlock>("BoardsEmptyState").Text = Texts.T(active ? "downloads.searchEmpty" : "downloads.boardsEmpty");
        TextBox input = WorkspaceControl<TextBox>("DownloadSearchInput");
        input.PlaceholderText = Texts.T("downloads.searchPlaceholder");
        AutomationProperties.SetName(input, Texts.T("downloads.searchPlaceholder"));
    }
}
