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
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    internal static bool IsNativeShortcutSource(object? source) => source is Visual visual && visual.GetSelfAndVisualAncestors()
        .Any(node => node is TextBox or SelectableTextBlock or Ursa.Controls.AutoCompleteBox or ComboBox or Button or MenuItem or Slider or TabItem);

    // Keep row identities through the asynchronous batch; host admission still
    // checks the exact watch/generation rather than inferring it from a URL.
    partial void ApplyFrozenUserPriority(ThreadRow[] rows, bool high);
    partial void QueryUserPriorityAvailable(ref bool available);

    private bool UserPriorityAvailable()
    {
        bool available = false;
        QueryUserPriorityAvailable(ref available);
        return available;
    }

    private bool IsCurrentUserPriorityRow(ThreadRow row) =>
        _threadRows.TryGetValue(row.WatchId, out ThreadRow? current) && ReferenceEquals(row, current)
        && (_appliedThreadSearchReconciliation?.Includes(row) ?? false);

    private void AddUserPriorityMenus(ContextMenuBuilder menu, bool ready)
    {
        ThreadRow[] frozen = SelectedThreads().Where(IsCurrentUserPriorityRow).DistinctBy(row => row.WatchId).ToArray();
        bool CanExecute() => ready && !RowCommandBusy && !_selectionCommandRunning && _restoreCompleted && UserPriorityAvailable();
        foreach (bool high in new[] { true, false })
        {
            ThreadRow[] affected = frozen.Where(row => row.IsHighPriority != high).ToArray();
            if (affected.Length == 0) continue;
            string key = high ? "priority.menuHigh" : "priority.menuNormal";
            string label = Texts.T(key, affected.Length);
            menu.Item(key, label, () =>
            {
                if (!UserPriorityAvailable()) return;
                ThreadRow[] targets = affected.Where(row => IsUserPriorityTarget(row, high, IsCurrentUserPriorityRow)).ToArray();
                if (targets.Length > 0) ApplyFrozenUserPriority(targets, high);
            }, available: CanExecute(), availableWhen: CanExecute);
        }
    }

    internal static bool IsUserPriorityTarget(ThreadRow row, bool high, Func<ThreadRow, bool> isCurrent)
        => isCurrent(row) && row.IsHighPriority != high;

    // The real menu factory is also usable without a native window/bitmap backend.
    // Only item-owned click handlers are installed: no row/language event subscriptions.
    internal static MenuItem[] CreateUserPriorityMenuItems(IEnumerable<ThreadRow> selection, bool ready,
        Func<bool> canExecute, Func<ThreadRow, bool> isCurrent, Action<ThreadRow[], bool> apply)
    {
        ThreadRow[] frozen = selection.Where(isCurrent).DistinctBy(row => row.WatchId).ToArray();
        var items = new List<MenuItem>();
        bool available = ready && canExecute();
        foreach (bool high in new[] { true, false })
        {
            ThreadRow[] affected = frozen.Where(row => row.IsHighPriority != high).ToArray();
            if (affected.Length == 0) continue;
            string key = high ? "priority.menuHigh" : "priority.menuNormal";
            string label = Texts.T(key, affected.Length);
            var item = new MenuItem { Header = label, Tag = key, IsEnabled = available };
            Avalonia.Automation.AutomationProperties.SetName(item, label);
            item.Click += (_, _) =>
            {
                if (!item.IsEnabled || !canExecute()) return;
                // Never expand to a later selection, a newly eligible row, or a
                // replacement watch. State may have changed while the menu was open.
                ThreadRow[] targets = affected.Where(row => IsUserPriorityTarget(row, high, isCurrent)).ToArray();
                if (targets.Length > 0) apply(targets, high);
            };
            items.Add(item);
        }
        return items.ToArray();
    }

    private long? _threadLeadId;
    private long? _boardLeadId;
    private bool _selectionCommandRunning;
    // Commands belong to the list itself. Editors, controls, popups and overlays
    // keep their native keys, including text selection and clipboard commands.
    internal static bool IsListCommandSource(object? source, Control list)
    {
        if (source is not Visual visual) return false;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, list)) return true;
            if (node is TextBox or SelectableTextBlock or Ursa.Controls.AutoCompleteBox or ComboBox or Button or MenuItem or Slider or TabItem
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
            if (grid.ContextMenu is ContextMenu menu) menu.Opening += (_, args) =>
            {
                // Populate before the popup measures its first frame. Rebuilding in
                // Opened can leave the native host sized for an empty menu.
                BuildDownloadMenu(grid, menu);
                args.Cancel = menu.ItemCount == 0;
            };
            grid.CurrentCellChanged += (_, _) =>
            {
                if (grid.CollectionView?.CurrentItem is ThreadRow thread) _threadLeadId = thread.WatchId;
                if (grid.CollectionView?.CurrentItem is BoardRow board) _boardLeadId = board.WatchId;
            };
        }
        InitializeDownloadHeaderMenus(); // right-click on a column header (MainWindow.Columns.cs)
        ArchivePage.RemoveSelectedWatches = RemoveArchiveWatchesAsync;
        ArchivePage.DeletePermanentlyRequested = DeleteArchivePermanentlyAsync;
        ArchivePage.CanRemoveWatch = id => !_sessionRowsApplying && _threadRows.Values.Any(row => row.Snapshot.Id == id);
        ArchivePage.CheckSelectedWatches = CheckArchiveWatchesAsync;
        ArchivePage.CopySelectedUrls = urls => CopyUrlsAsync(urls, ArchivePage.CaptureActionErrorReporter());
        ArchivePage.SelectionLeadAction = (id, action) =>
        {
            if (_sessionRowsApplying) return;
            ThreadRow? row = _threadRows.Values.FirstOrDefault(row => row.Snapshot.Id == id);
            if (action == "browser") { OpenTarget(row?.Url ?? ArchivePage.ArchiveUrlFor(id), ArchivePage.CaptureActionErrorReporter()); return; }
            if (action == "folder") { string? directory = row?.DownloadDirectory ?? ArchivePage.ArchiveDirectoryFor(id); if (directory != null) OpenDirectory(directory, ArchivePage.CaptureActionErrorReporter()); return; }
            if (action == "rules")
            {
                // From the large reader the page behind it is not reachable: leave it first, without going back to its origin.
                if (!_restoreCompleted || WorkspaceCommandBusy) return;
                if (_largeReaderOpen && _gallery == null) CloseLargeReader(restoreOrigin: false);
                if (NavigateWorkspace(WorkspacePage.Rules))
                    _rulesPage?.EditScope(Rules.MediaRuleScope.ForThread(id.Source, id.Board, id.Thread!));
            }
        };
        ArchivePage.ShowThreadInMediaRequested = ShowThreadInMedia; // QW-A #7: the archive list's and the reader's thread menus
        ArchivePage.SelectionDetailsRequested = id =>
        {
            if (_sessionRowsApplying) return;
            ThreadRow? row = _threadRows.Values.FirstOrDefault(row => row.Snapshot.Id == id);
            if (row == null) return;
            // From the large reader (its "Thread" menu) the Downloads page behind it is not reachable: leave the reader first.
            if (_largeReaderOpen && _gallery == null && _restoreCompleted && !WorkspaceCommandBusy) CloseLargeReader(restoreOrigin: false);
            OpenDiagnosticWatch(new DiagnosticTarget(DiagnosticTargetKind.Thread, row.WatchId));
        };
    }

    private DataGrid DownloadList(bool boards) => this.FindControl<DataGrid>(boards ? "BoardsDataGrid" : "ThreadsDataGrid")!;
    internal ThreadRow[] SelectedThreadsForTests() => SelectedThreads();
    private ThreadRow[] SelectedThreads() => _sessionRowsApplying ? Array.Empty<ThreadRow>() : DownloadList(false).SelectedItems.Cast<object>().OfType<ThreadRow>()
        .Where(row => _threadRows.TryGetValue(row.WatchId, out ThreadRow? current) && ReferenceEquals(row, current)
            && (_appliedThreadSearchReconciliation?.Includes(row) ?? false)).ToArray();
    private BoardRow[] SelectedBoards() => _sessionRowsApplying ? Array.Empty<BoardRow>() : DownloadList(true).SelectedItems.Cast<object>().OfType<BoardRow>()
        .Where(row => _boardRows.TryGetValue(row.WatchId, out BoardRow? current) && ReferenceEquals(row, current)
            && (_appliedBoardSearchReconciliation?.Includes(row) ?? false)).ToArray();
    private ThreadRow? LeadThread => _sessionRowsApplying || SelectedThreads().Length == 0 ? null
        : SelectedThreads().FirstOrDefault(row => row.WatchId == _threadLeadId) ?? SelectedThreads().FirstOrDefault();
    private BoardRow? LeadBoard => _sessionRowsApplying || SelectedBoards().Length == 0 ? null
        : SelectedBoards().FirstOrDefault(row => row.WatchId == _boardLeadId) ?? SelectedBoards().FirstOrDefault();

    private void SelectDownloadContextRow(object? sender, PointerPressedEventArgs e)
    {
        if (_sessionRowsApplying) { e.Handled = true; return; }
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
            if (grid.ContextMenu is ContextMenu menu)
            {
                // ContextMenu.Open(control) bypasses Opening in Avalonia 12.
                BuildDownloadMenu(grid, menu);
                if (menu.ItemCount > 0) menu.Open(grid);
            }
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
        if (_sessionRowsApplying && sender is DataGrid sliceGrid && IsListCommandSource(e.Source, sliceGrid)
            && (e.Key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown
                || CommandBindings.Matches(e, "selection.select-all")))
        { e.Handled = true; return; }
        if (sender is not DataGrid grid || e.Handled || !IsListCommandSource(e.Source, grid)
            || _largeReaderOpen || (_gallery != null && !_gallerySuspended) || !IsEnabled
            || _selectedPage != WorkspacePage.Downloads || RowCommandBusy || _selectionCommandRunning) return;
        bool boards = ReferenceEquals(grid, DownloadList(true));
        if (CommandBindings.Matches(e, "menu.context-open"))
        {
            e.Handled = true;
            if (grid.ContextMenu is ContextMenu menu && grid.SelectedItems.Count > 0)
            { BuildDownloadMenu(grid, menu); if (menu.ItemCount > 0) menu.Open(grid); }
            return;
        }
        // The dedicated Menu key remains native. An overridden free Shift+F10 does not.
        if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift) { e.Handled = true; return; }
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
        if (CommandBindings.Matches(e, "selection.select-all"))
        {
            e.Handled = true; grid.SelectAll(); return;
        }
        if (CommandBindings.Matches(e, "selection.copy-links"))
        {
            e.Handled = true; await CopyUrlsAsync(boards ? SelectedBoards().Select(row => row.Url).ToArray() : SelectedThreads().Select(row => row.Url).ToArray()); return;
        }
        if (CommandBindings.Matches(e, "export.open-html-dialog") && !boards)
        { e.Handled = true; await ExportDownloadSelectionAsync(grid); return; }
        if (CommandBindings.Matches(e, "watch.remove"))
        { e.Handled = true; await RemoveDownloadSelectionAsync(boards); return; }
        if (CommandBindings.Matches(e, "thread.details"))
        { e.Handled = true; OpenWorkspaceDetails(); return; }
        // Revision 19: "Show in Media" (Alt+M) for the lead thread, like the thread menu entry; boards have no media of their own.
        if (!boards && CommandBindings.Matches(e, "thread.show-in-media"))
        { e.Handled = true; if (LeadThread is ThreadRow row) ShowThreadInMedia(row.Snapshot.Id, row.Title); return; }
        if (CommandBindings.Matches(e, "thread.read") || CommandBindings.Matches(e, "gallery.open"))
        {
            e.Handled = true;
            if (boards) OpenWorkspaceDetails();
            else if (LeadThread is ThreadRow row)
            {
                if (CommandBindings.Matches(e, "gallery.open")) await OpenActions.RunAsync(() => OpenGalleryAsync(row.Snapshot.Id, origin: grid), "downloads.list.gallery");
                else if (grid.SelectedItems.Count == 1 && SelectedThreads().Length == 1)
                    await OpenActions.RunAsync(() => OpenDownloadReaderAsync(row.Snapshot.Id, grid), "downloads.list.read");
            }
            return;
        }
        if (CommandBindings.Matches(e, "watch.check") || CommandBindings.Matches(e, "watch.retry-media"))
        { e.Handled = true; await CheckDownloadSelectionAsync(boards, CommandBindings.Matches(e, "watch.retry-media")); return; }
        if (boards && CommandBindings.Matches(e, "board.filters"))
        { e.Handled = true; if (LeadBoard is BoardRow row) await OpenBoardAdmissionAsync(row); return; }
        if (CommandBindings.Matches(e, "rules.edit-scope"))
        { e.Handled = true; OnEditSelectionRules(sender, new RoutedEventArgs()); return; }
        if (CommandBindings.Matches(e, "location.open-folder"))
        { e.Handled = true; if (boards) OnOpenBoardDirectoryClick(sender, new RoutedEventArgs()); else OnOpenThreadDirectoryClick(sender, new RoutedEventArgs()); return; }
        if (CommandBindings.Matches(e, "location.open-source"))
        { e.Handled = true; if (boards) OnOpenBoardInBrowserClick(sender, new RoutedEventArgs()); else OnOpenThreadInBrowserClick(sender, new RoutedEventArgs()); }
    }

    private async Task RunSelectionCommandAsync<T>(IReadOnlyList<T> frozen, Func<T, Task> command)
    {
        if (_selectionCommandRunning || !_restoreCompleted || RowCommandBusy || frozen.Count == 0) return;
        _selectionCommandRunning = true;
        var failures = new List<string>();
        try
        {
            foreach (T id in frozen)
            {
                await AwaitSessionRowsAsync();
                if (_isClosing) return;
                try { await command(id); }
                catch (Exception error) { failures.Add(error.Message); }
            }
            if (failures.Count > 0) ShowNotification(Texts.T("notification.error"),
                Texts.Count("selection.failed", frozen.Count, failures.Count, frozen.Count, string.Join("; ", failures.Distinct())), NotificationType.Error, context: string.Join(",", frozen), operation: "selection.command");
        }
        finally { _selectionCommandRunning = false; RefreshWorkspace(); }
    }

    private Task RemoveDownloadSelectionAsync(bool boards)
    {
        long[] ids = boards ? SelectedBoards().Select(row => row.WatchId).ToArray() : SelectedThreads().Select(row => row.WatchId).ToArray();
        return RemoveWatchesWithUndoAsync(ids, boards, id => RemoveVisibleDownloadWatchAsync(id, boards));
    }

    // Removing a watch is deliberately light (no confirmation), so the way back is an undo toast. The
    // addresses are remembered before the removal; only watches that were really removed are offered.
    private async Task RemoveWatchesWithUndoAsync(IReadOnlyList<long> ids, bool boards, Func<long, Task<bool>> remove)
    {
        List<string> removed = new();
        await RunSelectionCommandAsync(ids, async id =>
        {
            string? url = boards ? (_boardRows.TryGetValue(id, out BoardRow? board) ? board.Url : null)
                : _threadRows.TryGetValue(id, out ThreadRow? thread) ? thread.Url : null;
            if (await remove(id) && url != null) removed.Add(url);
        });
        if (removed.Count > 0 && !_isClosing) ShowWatchRemovedToast(new WatchRemovalUndo(removed, ReadmitRemovedWatchesAsync));
    }

    private void ShowWatchRemovedToast(WatchRemovalUndo undo) => ShowActionNotification(
        Texts.T("notification.session"), Texts.T("watch.removed", undo.Count), NotificationType.Information,
        new[] { new FeedbackToastAction(Texts.T("watch.undo"), () => UndoWatchRemovalAsync(undo), "rotate-ccw") }, operation: "watch.removed");

    private async Task UndoWatchRemovalAsync(WatchRemovalUndo undo)
    {
        WatchUndoOutcome outcome = await undo.RunAsync();
        if (_isClosing) return;
        switch (outcome)
        {
            case WatchUndoOutcome.Restored:
                ShowNotification(Texts.T("notification.session"), Texts.T("watch.undoDone", undo.Restored), NotificationType.Information, operation: "watch.undo");
                break;
            case WatchUndoOutcome.Partial:
                ShowNotification(Texts.T("notification.session"), Texts.T("watch.undoPartial", undo.Count - undo.Restored, undo.Count), NotificationType.Warning, operation: "watch.undo");
                break;
            default:
                ShowNotification(Texts.T("notification.session"), Texts.T("watch.undoUnavailable"), NotificationType.Warning, operation: "watch.undo");
                break;
        }
    }

    // The same admission as the Add row, so the result is the normal URL report. Null: it could not start.
    private async Task<int?> ReadmitRemovedWatchesAsync(string text)
    {
        if (!_restoreCompleted || _isClosing || _importing || WorkspaceCommandBusy) return null;
        _importing = true;
        UpdateInteractionState();
        try
        {
            await SubmitInputAsync(text);
            return _admissionResults.Count(item => item.Status is AdmissionStatus.Added or AdmissionStatus.AlreadyPresent);
        }
        finally { _importing = false; UpdateInteractionState(); }
    }

    private async Task<bool> RemoveVisibleDownloadWatchAsync(long id, bool boards)
    {
        if (_sessionRowsApplying) return false;
        if (boards ? !SelectedBoards().Any(row => row.WatchId == id) : !SelectedThreads().Any(row => row.WatchId == id)) return false;
        await (boards ? _session.RemoveBoardAsync(id) : _session.RemoveThreadAsync(id));
        return true;
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
            ShowNotification(Texts.T("notification.session"), text, NotificationType.Information, context: string.Join(",", frozen.Select(target => target.WatchId)), operation: "selection.check");
        }
        if (retried) ShowNotification(Texts.T("notification.session"), acceptedMedia == 0
            ? Texts.T(RetryNoneKey(frozen.Where(target => target.Kind == DiagnosticTargetKind.Thread).Select(target => target.WatchId)))
            : Texts.Count(_session.Projection.IsPaused ? "work.retryPaused" : "work.retryAccepted", acceptedMedia),
            NotificationType.Information, context: string.Join(",", frozen.Select(target => target.WatchId)), operation: "selection.media-retry");
    }

    // The reason is read from the current row after the host declined, so the toast names what is true now.
    private string RetryNoneKey(IEnumerable<long> watchIds) => RetryNoneClassifier.KeyFor(RetryNoneClassifier.Combine(
        watchIds.Select(id => _threadRows.TryGetValue(id, out ThreadRow? row)
            ? RetryNoneClassifier.Classify(row.Snapshot.Work, watchExists: true)
            : RetryNoneReason.Removed)));

    private Task RemoveArchiveWatchesAsync(IReadOnlyList<ResourceId> ids)
    {
        long[] watchIds = ids.Select(id => _threadRows.Values.FirstOrDefault(row => row.Snapshot.Id == id)?.WatchId)
            .OfType<long>().Distinct().ToArray();
        return RemoveWatchesWithUndoAsync(watchIds, boards: false, async id => { await _session.RemoveThreadAsync(id); return true; });
    }
    private Task CheckArchiveWatchesAsync(IReadOnlyList<ResourceId> ids, bool retry)
    {
        DiagnosticTarget[] targets = ids.Select(id => _threadRows.Values.FirstOrDefault(row => row.Snapshot.Id == id)?.WatchId)
            .OfType<long>().Distinct().Select(id => new DiagnosticTarget(DiagnosticTargetKind.Thread, id)).ToArray();
        return RunCheckTargetsAsync(targets, retry);
    }

    private async Task OpenDownloadReaderAsync(ResourceId id, Control? origin)
    {
        if (_sessionRowsApplying) return;
        ResourceId[] scope = DownloadList(false).CollectionView.Cast<ThreadRow>().Where(row => !row.IsHistorical).Select(row => row.Snapshot.Id).ToArray();
        OpenLargeReader(origin, scope);
        await ArchivePage.LoadThreadAsync(id);
    }

    private async Task ExportDownloadSelectionAsync(Control? origin)
    {
        ResourceId[] ids = SelectedThreads().Select(row => row.Snapshot.Id).Distinct().ToArray();
        if (ids.Length == 0 || RowCommandBusy) return;
        Action restore = CaptureDownloadSelectionOrigin(origin);
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        await ArchivePage.OpenExportForThreadsAsync(ids, origin, restore);
    }

    private Action CaptureDownloadSelectionOrigin(Control? origin)
    {
        long[] threads = SelectedThreads().Select(row => row.WatchId).ToArray();
        long[] boards = SelectedBoards().Select(row => row.WatchId).ToArray();
        long? threadLead = _threadLeadId, boardLead = _boardLeadId;
        int tab = DownloadsTabs.SelectedIndex;
        long filterRevision = _downloadFilterRevision;
        var scrolls = new[] { DownloadList(false), DownloadList(true) }.Select(grid => grid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()).ToArray();
        Vector[] offsets = scrolls.Select(scroll => scroll?.Offset ?? default).ToArray();
        return () =>
        {
            if (_isClosing || !NavigateWorkspace(WorkspacePage.Downloads, activateArchive: false)) return;
            DownloadsTabs.SelectedIndex = tab;
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
}
