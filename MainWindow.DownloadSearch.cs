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
    private long _downloadFilterRevision;
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
        RefreshWorkspace();
    }

    private void RefreshDownloadSearch()
    {
        if (_refreshingDownloadSearch) return;
        _refreshingDownloadSearch = true;
        try
        {
            string query = DownloadSearchText;
            ReconcileDownloadResult(VisibleThreads, UrlList.Where(row => DownloadSearch.Matches(row, query)).ToArray(), DownloadList(false));
            ReconcileDownloadResult(VisibleBoards, BoardsList.Where(row => DownloadSearch.Matches(row, query)).ToArray(), DownloadList(true));
            if (!SelectedThreads().Any(row => row.WatchId == _threadLeadId)) _threadLeadId = SelectedThreads().FirstOrDefault()?.WatchId;
            if (!SelectedBoards().Any(row => row.WatchId == _boardLeadId)) _boardLeadId = SelectedBoards().FirstOrDefault()?.WatchId;
            RefreshDownloadSearchPresentation();
        }
        finally { _refreshingDownloadSearch = false; }
    }

    private void ReconcileDownloadResult<T>(ObservableCollection<T> visible, T[] result, DataGrid grid) where T : class
    {
        HashSet<T> included = new(result);
        // The result is independent of virtualization and scrolling. Remove only actual non-results.
        foreach (T item in grid.SelectedItems.OfType<T>().Where(item => !included.Contains(item)).ToArray())
            grid.SelectedItems.Remove(item);
        foreach (T item in visible.Where(item => !included.Contains(item)).ToArray())
        {
            visible.Remove(item);
            _downloadFilterRevision++;
        }
        HashSet<T> existing = new(visible);
        foreach (T item in result) if (existing.Add(item)) visible.Add(item);
        // DataGrid may move currency when a result disappears; currency must not create a new selection.
        foreach (T item in grid.SelectedItems.OfType<T>().Where(item => !included.Contains(item)).ToArray())
            grid.SelectedItems.Remove(item);
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
