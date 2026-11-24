using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class MainWindow
{
    // A menu holds immutable identities and values, never the current mutable selection.
    private sealed record DownloadMenuTarget(bool Boards, ResourceId Lead, long LeadWatch, string Folder, string Url,
        long[] WatchIds, string[] Urls, ResourceId[] Threads, ResourceId[] Scope,
        DiagnosticTarget[] Checkable, DiagnosticTarget[] Retriable, BoardRow? Board);

    private DownloadMenuTarget? CaptureDownloadMenu(bool boards)
    {
        DesktopSessionProjection projection = _session.Projection;
        bool CanCheck(DiagnosticTarget target) => DesktopSessionHost.CanRequestCheck(projection, target);
        if (boards)
        {
            BoardRow? lead = LeadBoard;
            if (lead == null) return null;
            BoardRow[] rows = SelectedBoards();
            return new(true, lead.Snapshot.Id, lead.WatchId, lead.DownloadDirectory, lead.Url,
                rows.Select(row => row.WatchId).ToArray(), rows.Select(row => row.Url).ToArray(), Array.Empty<ResourceId>(), Array.Empty<ResourceId>(),
                rows.Select(row => new DiagnosticTarget(DiagnosticTargetKind.Board, row.WatchId)).Where(CanCheck).ToArray(),
                Array.Empty<DiagnosticTarget>(), lead);
        }
        else
        {
            ThreadRow? lead = LeadThread;
            if (lead == null) return null;
            ThreadRow[] rows = SelectedThreads();
            return new(false, lead.Snapshot.Id, lead.WatchId, lead.DownloadDirectory, lead.Url,
                rows.Select(row => row.WatchId).ToArray(), rows.Select(row => row.Url).ToArray(), rows.Select(row => row.Snapshot.Id).ToArray(),
                DownloadList(false).CollectionView.Cast<ThreadRow>().Where(row => !row.IsHistorical).Select(row => row.Snapshot.Id).ToArray(),
                rows.Select(row => new DiagnosticTarget(DiagnosticTargetKind.Thread, row.WatchId)).Where(CanCheck).ToArray(),
                projection.IsStale || projection.View.State != DesktopSessionState.Ready
                    ? [] : rows.Where(row => DesktopSessionHost.HasRetriableMedia(row.Snapshot.Work))
                    .Select(row => new DiagnosticTarget(DiagnosticTargetKind.Thread, row.WatchId)).ToArray(), null);
        }
    }

    private void BuildDownloadMenu(DataGrid grid, ContextMenu menu)
    {
        DownloadMenuTarget? frozen = CaptureDownloadMenu(ReferenceEquals(grid, DownloadList(true)));
        if (frozen == null) { ContextMenuBuilder.Clear(menu); menu.Close(); return; }
        bool ready = _restoreCompleted && !RowCommandBusy && !_selectionCommandRunning;
        string Unavailable(string id) => Texts.T(!ready
            ? RowCommandBusy || _selectionCommandRunning ? "menu.unavailable.busy"
              : !_restoreCompleted ? "menu.unavailable.restoring" : "menu.unavailable.selection"
            : id switch
            {
                "thread.read" => frozen.Threads.Length == 1 ? "menu.unavailable.nothingCaptured" : "menu.unavailable.singleThread",
                "watch.check" => "menu.unavailable.nothingToCheck",
                "watch.retry-media" => "menu.unavailable.nothingToRetry",
                _ => "menu.unavailable.selection"
            });
        ContextMenuBuilder builder = new ContextMenuBuilder().Named(Texts.T("nav.downloads"));
        void Add(string id, Action action, bool available = true, int? count = null)
        {
            bool CanExecute() => ready && available && !RowCommandBusy && !_selectionCommandRunning;
            string label = count is { } number && number != 1 ? CommandCatalog.Label(id) + " (" + number + ")" : CommandCatalog.Label(id);
            string? Reason() => CanExecute() ? null : Unavailable(id);
            builder.Command(id, () =>
            {
                DownloadMenuTarget? current = RevalidateDownloadMenu(frozen);
                if (current == null) return;
                frozen = current;
                action();
            }, CanExecute(), Reason(), label, availableWhen: CanExecute, reasonWhen: Reason);
        }
        void AddCheck(string id, DiagnosticTarget[] targets, bool retry)
        {
            int selectionCount = frozen.WatchIds.Length;
            string Label() => selectionCount == 1 ? CommandCatalog.Label(id)
                : Texts.T("selection.actionSubset", CommandCatalog.Label(id), targets.Length, selectionCount);
            bool CanExecute() => ready && targets.Length > 0 && !RowCommandBusy && !_selectionCommandRunning;
            string? Reason() => CanExecute() ? null : Unavailable(id);
            // The original eligible subset bounds the command. Recheck each member after the
            // projection settles, since an earlier request can change the later targets.
            bool IsExecutable(DiagnosticTarget target) => grid.IsEffectivelyVisible && IsCurrentDownloadTarget(target)
                && (retry ? _session.CanRetryMissingMedia(target.WatchId) : _session.CanRequestCheck(target));
            builder.Command(id, () => OpenActions.Start(() => RunCheckTargetsAsync(targets, retry, IsExecutable), id),
                CanExecute(), Reason(), availableWhen: CanExecute, reasonWhen: Reason, labelWhen: Label);
        }
        if (!frozen.Boards)
        {
            Add("thread.read", () => OpenActions.Start(() => OpenFrozenReaderAsync(frozen, grid), "downloads.menu.read"),
                frozen.Threads.Length == 1 && !NothingCaptured(LeadRowOf(frozen)));
            Add("gallery.open", () => OpenActions.Start(() => OpenGalleryAsync(frozen.Lead, origin: grid), "downloads.menu.gallery"));
            // A selection of several threads shows them all, as one thread set (QW-A #7 follow-up); one thread keeps its thread: chip.
            Add("thread.show-in-media", () => ShowSelectedThreadsInMedia(frozen.Threads, frozen.Lead,
                _threadRows.TryGetValue(frozen.LeadWatch, out ThreadRow? leadRow) ? leadRow.Title : null), count: frozen.Threads.Length);
        }
        Add("thread.details", () => OpenFrozenDetails(frozen));
        if (frozen.Boards) Add("board.filters", async () => { if (frozen.Board != null) await OpenBoardAdmissionAsync(frozen.Board); });
        builder.Section();
        if (!frozen.Boards) Add("export.open-html-dialog", async () => await ExportFrozenThreadsAsync(frozen.Threads, grid), count: frozen.Threads.Length);
        Add("selection.copy-links", async () => await CopyUrlsAsync(frozen.Urls), count: frozen.Urls.Length);
        Add("location.open-folder", () => OpenDirectory(frozen.Folder));
        Add("location.open-source", () => OpenTarget(frozen.Url));
        builder.Section();
        AddCheck("watch.check", frozen.Checkable, retry: false);
        if (!frozen.Boards)
            AddCheck("watch.retry-media", frozen.Retriable, retry: true);
        if (!frozen.Boards) AddUserPriorityMenus(builder, ready);
        Add("rules.edit-scope", () => EditFrozenRules(frozen.Lead));
        builder.Section();
        Add("watch.remove", async () => await RemoveWatchesWithUndoAsync(frozen.WatchIds,
            frozen.Boards, id => RemoveVisibleDownloadWatchAsync(id, frozen.Boards)), count: frozen.WatchIds.Length);
        builder.Populate(menu);
        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(item => item.Tag is "priority.menuHigh" or "priority.menuNormal"))
            item.Icon = CommandBindings.Icon(Equals(item.Tag, "priority.menuHigh") ? "reader.previous-post" : "reader.next-post");
    }

    private DownloadMenuTarget? RevalidateDownloadMenu(DownloadMenuTarget frozen)
    {
        if (RowCommandBusy) return null;
        long[] selected = frozen.Boards ? SelectedBoards().Select(row => row.WatchId).ToArray()
            : SelectedThreads().Select(row => row.WatchId).ToArray();
        if (!selected.Contains(frozen.LeadWatch)) return null;
        long[] ids = frozen.WatchIds.Where(selected.Contains).ToArray();
        ResourceId[] threads = frozen.Threads.Where(id => SelectedThreads().Any(row => row.Snapshot.Id == id)).ToArray();
        string[] urls = frozen.Urls.Where(url => frozen.Boards ? SelectedBoards().Any(row => row.Url == url)
            : SelectedThreads().Any(row => row.Url == url)).ToArray();
        return frozen with { WatchIds = ids, Threads = threads, Urls = urls,
            Scope = frozen.Scope.Where(id => VisibleThreads.Any(row => row.Snapshot.Id == id)).ToArray() };
    }

    private async Task OpenFrozenReaderAsync(DownloadMenuTarget frozen, Control origin)
    {
        if (ReportNothingCaptured(LeadRowOf(frozen))) return;
        await ArchivePage.OpenReaderOnThreadAsync(frozen.Lead, () => OpenLargeReader(origin, frozen.Scope));
    }

    private void OpenFrozenDetails(DownloadMenuTarget frozen)
    {
        DataGrid grid = DownloadList(frozen.Boards);
        if (frozen.Boards && _boardRows.TryGetValue(frozen.LeadWatch, out BoardRow? board) && VisibleBoards.Contains(board))
        { _boardLeadId = board.WatchId; if (!grid.SelectedItems.Contains(board)) grid.SelectedItem = board; }
        else if (!frozen.Boards && _threadRows.TryGetValue(frozen.LeadWatch, out ThreadRow? thread) && VisibleThreads.Contains(thread))
        { _threadLeadId = thread.WatchId; if (!grid.SelectedItems.Contains(thread)) grid.SelectedItem = thread; }
        else return;
        OpenWorkspaceDetails();
    }

    private void EditFrozenRules(ResourceId id)
    {
        if (!NavigateWorkspace(WorkspacePage.Rules)) return;
        _rulesPage?.EditScope(id.Thread == null ? Rules.MediaRuleScope.ForBoard(id.Source, id.Board)
            : Rules.MediaRuleScope.ForThread(id.Source, id.Board, id.Thread));
    }

    private async Task ExportFrozenThreadsAsync(ResourceId[] ids, Control? origin)
    {
        if (ids.Length == 0 || RowCommandBusy) return;
        Action restore = CaptureDownloadSelectionOrigin(origin);
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        await ArchivePage.OpenExportForThreadsAsync(ids, origin, restore);
    }
}
