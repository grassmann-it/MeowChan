using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    internal static bool IsNativeShortcutSource(object? source) => source is Visual visual && visual.GetSelfAndVisualAncestors()
        .Any(node => node is TextBox or SelectableTextBlock or ComboBox or Button or MenuItem or Slider or TabItem);

    private long? _threadLeadId;
    private long? _boardLeadId;
    private bool _selectionCommandRunning;
    private static KeyModifiers ListCommandModifier => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    // Commands belong to the list itself. Editors, controls, popups and overlays
    // keep their native keys, including text selection and clipboard commands.
    internal static bool IsListCommandSource(object? source, Control list)
    {
        if (source is not Visual visual) return false;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, list)) return true;
            if (node is TextBox or SelectableTextBlock or ComboBox or Button or MenuItem or Slider or TabItem
                or DataGridColumnHeader) return false;
        }
        return false;
    }

    private void InitializeSelectionCommands()
    {
        foreach (string name in new[] { "ThreadsDataGrid", "BoardsDataGrid" })
        {
            DataGrid grid = this.FindControl<DataGrid>(name)!;
            grid.AddHandler(KeyDownEvent, OnDownloadListKeyDown, RoutingStrategies.Tunnel);
            grid.AddHandler(ContextRequestedEvent, OnDownloadContextRequested, RoutingStrategies.Tunnel);
            if (grid.ContextMenu is ContextMenu menu) menu.Opened += (_, _) => BuildDownloadMenu(grid, menu);
            grid.CurrentCellChanged += (_, _) =>
            {
                if (grid.CollectionView?.CurrentItem is ThreadRow thread) _threadLeadId = thread.WatchId;
                if (grid.CollectionView?.CurrentItem is BoardRow board) _boardLeadId = board.WatchId;
            };
        }
        ArchivePage.RemoveSelectedWatches = RemoveArchiveWatchesAsync;
        ArchivePage.DeletePermanentlyRequested = DeleteArchivePermanentlyAsync;
        ArchivePage.CanRemoveWatch = id => _threadRows.Values.Any(row => row.Snapshot.Id == id);
        ArchivePage.CheckSelectedWatches = CheckArchiveWatchesAsync;
        ArchivePage.CopySelectedUrls = CopyUrlsAsync;
        ArchivePage.SelectionLeadAction = (id, action) =>
        {
            ThreadRow? row = _threadRows.Values.FirstOrDefault(row => row.Snapshot.Id == id);
            if (action == "browser") { OpenTarget(row?.Url ?? ArchivePage.ArchiveUrlFor(id)); return; }
            if (action == "folder") { string? directory = row?.DownloadDirectory ?? ArchivePage.ArchiveDirectoryFor(id); if (directory != null) OpenDirectory(directory); return; }
            if (action == "rules" && NavigateWorkspace(WorkspacePage.Rules))
                _rulesPage?.EditScope(Rules.MediaRuleScope.ForThread(id.Source, id.Board, id.Thread!));
        };
        ArchivePage.SelectionDetailsRequested = id =>
        {
            ThreadRow? row = _threadRows.Values.FirstOrDefault(row => row.Snapshot.Id == id);
            if (row != null) OpenDiagnosticWatch(new DiagnosticTarget(DiagnosticTargetKind.Thread, row.WatchId));
        };
    }

    internal static void NormalizeSelectionMenu(ContextMenu menu)
    {
        foreach (MenuItem item in menu.Items.OfType<MenuItem>())
        {
            if (item.Tag is string key && item.Classes.Contains("localized")) item.Header = Texts.T(key);
            if (OperatingSystem.IsMacOS() && item.InputGesture is KeyGesture gesture && gesture.KeyModifiers.HasFlag(KeyModifiers.Control))
                item.InputGesture = new KeyGesture(gesture.Key, (gesture.KeyModifiers & ~KeyModifiers.Control) | KeyModifiers.Meta);
        }
    }

    private DataGrid DownloadList(bool boards) => this.FindControl<DataGrid>(boards ? "BoardsDataGrid" : "ThreadsDataGrid")!;
    private ThreadRow[] SelectedThreads() => DownloadList(false).SelectedItems.Cast<object>().OfType<ThreadRow>()
        .Where(row => UrlList.Contains(row) && VisibleThreads.Contains(row)).ToArray();
    private BoardRow[] SelectedBoards() => DownloadList(true).SelectedItems.Cast<object>().OfType<BoardRow>()
        .Where(row => BoardsList.Contains(row) && VisibleBoards.Contains(row)).ToArray();
    private ThreadRow? LeadThread => SelectedThreads().Length == 0 ? null
        : SelectedThreads().FirstOrDefault(row => row.WatchId == _threadLeadId) ?? SelectedThreads().FirstOrDefault();
    private BoardRow? LeadBoard => SelectedBoards().Length == 0 ? null
        : SelectedBoards().FirstOrDefault(row => row.WatchId == _boardLeadId) ?? SelectedBoards().FirstOrDefault();

    private void SelectDownloadContextRow(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not DataGrid grid) return;
        _downloadPointerContextHandled.Remove(grid);
        DataGridRow? hit = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<DataGridRow>().FirstOrDefault();
        object? item = hit?.DataContext;
        if (item is ThreadRow thread) _threadLeadId = thread.WatchId;
        if (item is BoardRow board) _boardLeadId = board.WatchId;
        if (!e.GetCurrentPoint(grid).Properties.IsRightButtonPressed) return;
        _downloadPointerContextHandled.Add(grid);
        if (item == null)
        {
            grid.SelectedItems.Clear();
            if (grid == DownloadList(false)) _threadLeadId = null; else _boardLeadId = null;
            grid.ContextMenu?.Close();
        }
        else
        {
            if (!grid.SelectedItems.Contains(item)) grid.SelectedItem = item;
            grid.Focus();
            grid.ContextMenu?.Open(grid);
        }
        e.Handled = true;
        RefreshWorkspace();
    }

    private readonly HashSet<Control> _downloadPointerContextHandled = new();

    private void OnDownloadContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is DataGrid emptyGrid && emptyGrid.SelectedItems.Count == 0)
        { e.Handled = true; return; }
        if (sender is Control list && e.TryGetPosition(list, out _)
            && _downloadPointerContextHandled.Remove(list))
            e.Handled = true;
        // Positionless keyboard requests keep Avalonia's native ContextMenu/Shift+F10 behavior.
    }

    private async void OnDownloadListKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not DataGrid grid || e.Handled || !IsListCommandSource(e.Source, grid)
            || _largeReaderOpen || (_gallery != null && !_gallerySuspended) || !IsEnabled
            || _selectedPage != WorkspacePage.Downloads || WorkspaceCommandBusy || _selectionCommandRunning) return;
        bool boards = ReferenceEquals(grid, DownloadList(true));
        if (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            // Native grid navigation handles range selection and moves CurrentItem.
            Dispatcher.UIThread.Post(() =>
            {
                if (grid.CollectionView?.CurrentItem is ThreadRow thread) _threadLeadId = thread.WatchId;
                if (grid.CollectionView?.CurrentItem is BoardRow board) _boardLeadId = board.WatchId;
                RefreshWorkspace();
            }, DispatcherPriority.Background);
            return;
        }
        KeyModifiers mods = e.KeyModifiers;
        if (mods == ListCommandModifier && e.Key == Key.A)
        {
            e.Handled = true; grid.SelectAll(); return;
        }
        if (mods == ListCommandModifier && e.Key == Key.C)
        {
            e.Handled = true; await CopyUrlsAsync(boards ? SelectedBoards().Select(row => row.Url).ToArray() : SelectedThreads().Select(row => row.Url).ToArray()); return;
        }
        if (mods == ListCommandModifier && e.Key == Key.E && !boards)
        { e.Handled = true; await ExportDownloadSelectionAsync(grid); return; }
        if (mods == KeyModifiers.None && e.Key == Key.Delete)
        { e.Handled = true; await RemoveDownloadSelectionAsync(boards); return; }
        if (e.Key == Key.Enter && mods == KeyModifiers.Alt)
        { e.Handled = true; OpenWorkspaceDetails(); return; }
        if (e.Key == Key.Enter && mods is KeyModifiers.None or KeyModifiers.Shift)
        {
            e.Handled = true;
            if (boards) OpenWorkspaceDetails();
            else if (LeadThread is ThreadRow row)
            {
                if (mods == KeyModifiers.Shift) await OpenGalleryAsync(row.Snapshot.Id, origin: grid);
                else if (grid.SelectedItems.Count == 1 && SelectedThreads().Length == 1) await OpenDownloadReaderAsync(row.Snapshot.Id, grid);
            }
            return;
        }
        if (e.Key == Key.F5 && mods is KeyModifiers.None or KeyModifiers.Shift)
        { e.Handled = true; await CheckDownloadSelectionAsync(boards, mods == KeyModifiers.Shift); return; }
        if (boards && mods == (ListCommandModifier | KeyModifiers.Shift) && e.Key == Key.R)
        { e.Handled = true; if (LeadBoard is BoardRow row) await OpenBoardAdmissionAsync(row); return; }
        if (mods == ListCommandModifier && e.Key == Key.R)
        { e.Handled = true; OnEditSelectionRules(sender, new RoutedEventArgs()); return; }
        if (mods == ListCommandModifier && e.Key == Key.O)
        { e.Handled = true; if (boards) OnOpenBoardDirectoryClick(sender, new RoutedEventArgs()); else OnOpenThreadDirectoryClick(sender, new RoutedEventArgs()); return; }
        if (mods == (ListCommandModifier | KeyModifiers.Shift) && e.Key == Key.O)
        { e.Handled = true; if (boards) OnOpenBoardInBrowserClick(sender, new RoutedEventArgs()); else OnOpenThreadInBrowserClick(sender, new RoutedEventArgs()); }
    }

    private async Task RunSelectionCommandAsync<T>(IReadOnlyList<T> frozen, Func<T, Task> command)
    {
        if (_selectionCommandRunning || !_restoreCompleted || WorkspaceCommandBusy || frozen.Count == 0) return;
        _selectionCommandRunning = true;
        var failures = new List<string>();
        try
        {
            foreach (T id in frozen)
            {
                try { await command(id); }
                catch (Exception error) { failures.Add(error.Message); }
            }
            if (failures.Count > 0) ShowNotification(Texts.T("notification.error"),
                Texts.T("selection.failed", failures.Count, frozen.Count, string.Join("; ", failures.Distinct())), NotificationType.Error);
        }
        finally { _selectionCommandRunning = false; RefreshWorkspace(); }
    }

    private Task RemoveDownloadSelectionAsync(bool boards)
    {
        long[] ids = boards ? SelectedBoards().Select(row => row.WatchId).ToArray() : SelectedThreads().Select(row => row.WatchId).ToArray();
        return RunSelectionCommandAsync(ids, id => RemoveVisibleDownloadWatchAsync(id, boards));
    }

    private Task RemoveVisibleDownloadWatchAsync(long id, bool boards)
    {
        if (boards ? !SelectedBoards().Any(row => row.WatchId == id) : !SelectedThreads().Any(row => row.WatchId == id)) return Task.CompletedTask;
        return boards ? _session.RemoveBoardAsync(id) : _session.RemoveThreadAsync(id);
    }

    private bool IsCurrentDownloadTarget(DiagnosticTarget target) => target.Kind == DiagnosticTargetKind.Board
        ? SelectedBoards().Any(row => row.WatchId == target.WatchId)
        : SelectedThreads().Any(row => row.WatchId == target.WatchId);

    private Task CheckDownloadSelectionAsync(bool boards, bool retry)
    {
        DiagnosticTarget[] targets = boards
            ? SelectedBoards().Where(row => _session.CanRequestCheck(new DiagnosticTarget(DiagnosticTargetKind.Board, row.WatchId)))
                .Select(row => new DiagnosticTarget(DiagnosticTargetKind.Board, row.WatchId)).ToArray()
            : SelectedThreads().Where(row => retry ? row.CanRetryMedia : _session.CanRequestCheck(new DiagnosticTarget(DiagnosticTargetKind.Thread, row.WatchId)))
                .Select(row => new DiagnosticTarget(DiagnosticTargetKind.Thread, row.WatchId)).ToArray();
        return RunCheckTargetsAsync(targets, retry, IsCurrentDownloadTarget);
    }

    private async Task RunCheckTargetsAsync(DiagnosticTarget[] frozen, bool retry, Func<DiagnosticTarget, bool>? isCurrent = null)
    {
        var responses = new List<(DiagnosticTarget Target, Interfaces.CheckRequestStatus Status)>();
        int acceptedMedia = 0;
        bool retried = false;
        await RunSelectionCommandAsync(frozen, async target =>
        {
            if (isCurrent != null && !isCurrent(target)) return;
            if (retry && target.Kind == DiagnosticTargetKind.Thread)
            { acceptedMedia += await _session.RetryMissingMediaAsync(target.WatchId); retried = true; }
            else
            {
                Interfaces.CheckRequestStatus result = await _session.RequestCheckAsync(target);
                if (result is not (Interfaces.CheckRequestStatus.Scheduled or Interfaces.CheckRequestStatus.AlreadyScheduled))
                    throw new InvalidOperationException(CheckActionText.ForResult(result, target.Kind));
                responses.Add((target, result));
            }
        });
        if (responses.Count > 0)
        {
            string text = CheckActionText.ForResult(responses[0].Status, responses[0].Target.Kind);
            if (frozen.Length > 1) text += " · " + responses.Count + "/" + frozen.Length;
            ShowNotification(Texts.T("notification.session"), text, NotificationType.Information);
        }
        if (retried) ShowNotification(Texts.T("notification.session"), Texts.T(acceptedMedia == 0 ? "work.retryNone"
            : _session.IsPaused ? "work.retryPaused" : "work.retryAccepted", acceptedMedia),
            acceptedMedia == 0 ? NotificationType.Warning : NotificationType.Information);
    }

    private Task RemoveArchiveWatchesAsync(IReadOnlyList<ResourceId> ids)
    {
        long[] watchIds = ids.Select(id => _threadRows.Values.FirstOrDefault(row => row.Snapshot.Id == id)?.WatchId)
            .OfType<long>().Distinct().ToArray();
        return RunSelectionCommandAsync(watchIds, _session.RemoveThreadAsync);
    }
    private Task CheckArchiveWatchesAsync(IReadOnlyList<ResourceId> ids, bool retry)
    {
        DiagnosticTarget[] targets = ids.Select(id => _threadRows.Values.FirstOrDefault(row => row.Snapshot.Id == id)?.WatchId)
            .OfType<long>().Distinct().Select(id => new DiagnosticTarget(DiagnosticTargetKind.Thread, id)).ToArray();
        return RunCheckTargetsAsync(targets, retry);
    }

    private async Task OpenDownloadReaderAsync(ResourceId id, Control? origin)
    {
        ResourceId[] scope = DownloadList(false).CollectionView.Cast<ThreadRow>().Select(row => row.Snapshot.Id).ToArray();
        OpenLargeReader(origin, scope);
        await ArchivePage.LoadThreadAsync(id);
    }

    private async Task ExportDownloadSelectionAsync(Control? origin)
    {
        ResourceId[] ids = SelectedThreads().Select(row => row.Snapshot.Id).Distinct().ToArray();
        if (ids.Length == 0 || WorkspaceCommandBusy) return;
        Action restore = CaptureDownloadSelectionOrigin(origin);
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        await ArchivePage.OpenExportForThreadsAsync(ids, origin, restore);
    }

    private Action CaptureDownloadSelectionOrigin(Control? origin)
    {
        long[] threads = SelectedThreads().Select(row => row.WatchId).ToArray();
        long[] boards = SelectedBoards().Select(row => row.WatchId).ToArray();
        long? threadLead = _threadLeadId, boardLead = _boardLeadId;
        int tab = this.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex;
        long filterRevision = _downloadFilterRevision;
        var scrolls = new[] { DownloadList(false), DownloadList(true) }.Select(grid => grid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()).ToArray();
        Vector[] offsets = scrolls.Select(scroll => scroll?.Offset ?? default).ToArray();
        return () =>
        {
            if (_isClosing || !NavigateWorkspace(WorkspacePage.Downloads, activateArchive: false)) return;
            this.FindControl<TabControl>("DownloadsTabs")!.SelectedIndex = tab;
            DownloadList(false).SelectedItems.Clear(); DownloadList(true).SelectedItems.Clear();
            foreach (long id in threads) if (_threadFilterExclusions.GetValueOrDefault(id) <= filterRevision && _threadRows.TryGetValue(id, out ThreadRow? row) && VisibleThreads.Contains(row)) DownloadList(false).SelectedItems.Add(row);
            foreach (long id in boards) if (_boardFilterExclusions.GetValueOrDefault(id) <= filterRevision && _boardRows.TryGetValue(id, out BoardRow? row) && VisibleBoards.Contains(row)) DownloadList(true).SelectedItems.Add(row);
            _threadLeadId = threadLead; _boardLeadId = boardLead;
            Dispatcher.UIThread.Post(() =>
            {
                for (int i = 0; i < scrolls.Length; ++i) if (scrolls[i] != null) scrolls[i]!.Offset = offsets[i];
                if (origin?.IsEffectivelyVisible == true) origin.Focus(); else DownloadList(tab == 1).Focus();
            }, DispatcherPriority.Loaded);
        };
    }

    private async void OnExportDownloadSelection(object? sender, RoutedEventArgs e) => await ExportDownloadSelectionAsync(DownloadList(false));
    private async void OnReadDownloadSelection(object? sender, RoutedEventArgs e)
    { if (SelectedThreads().Length == 1 && LeadThread is ThreadRow row) await OpenDownloadReaderAsync(row.Snapshot.Id, DownloadList(false)); }
    private async void OnGalleryDownloadSelection(object? sender, RoutedEventArgs e)
    { if (LeadThread is ThreadRow row) await OpenGalleryAsync(row.Snapshot.Id, origin: DownloadList(false)); }
    private async void OnBoardAdmissionSelection(object? sender, RoutedEventArgs e)
    { if (LeadBoard is BoardRow row) await OpenBoardAdmissionAsync(row); }
}
