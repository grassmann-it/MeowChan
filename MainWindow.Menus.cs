using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
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
        if (boards)
        {
            BoardRow? lead = LeadBoard;
            if (lead == null) return null;
            BoardRow[] rows = SelectedBoards();
            return new(true, lead.Snapshot.Id, lead.WatchId, lead.DownloadDirectory, lead.Url,
                rows.Select(row => row.WatchId).ToArray(), rows.Select(row => row.Url).ToArray(), Array.Empty<ResourceId>(), Array.Empty<ResourceId>(),
                rows.Select(row => new DiagnosticTarget(DiagnosticTargetKind.Board, row.WatchId)).Where(_session.CanRequestCheck).ToArray(),
                Array.Empty<DiagnosticTarget>(), lead);
        }
        else
        {
            ThreadRow? lead = LeadThread;
            if (lead == null) return null;
            ThreadRow[] rows = SelectedThreads();
            return new(false, lead.Snapshot.Id, lead.WatchId, lead.DownloadDirectory, lead.Url,
                rows.Select(row => row.WatchId).ToArray(), rows.Select(row => row.Url).ToArray(), rows.Select(row => row.Snapshot.Id).ToArray(),
                DownloadList(false).CollectionView.Cast<ThreadRow>().Select(row => row.Snapshot.Id).ToArray(),
                rows.Select(row => new DiagnosticTarget(DiagnosticTargetKind.Thread, row.WatchId)).Where(_session.CanRequestCheck).ToArray(),
                rows.Where(row => row.CanRetryMedia).Select(row => new DiagnosticTarget(DiagnosticTargetKind.Thread, row.WatchId)).ToArray(), null);
        }
    }

    private void BuildDownloadMenu(DataGrid grid, ContextMenu menu)
    {
        DownloadMenuTarget? frozen = CaptureDownloadMenu(ReferenceEquals(grid, DownloadList(true)));
        menu.Items.Clear();
        if (frozen == null) { menu.Close(); return; }
        bool ready = _restoreCompleted && !WorkspaceCommandBusy && !_selectionCommandRunning;
        string unavailable = Texts.Language == "de" ? "Für diese Auswahl derzeit nicht verfügbar." : "Currently unavailable for this selection.";
        MenuItem Add(string id, Action action, bool available = true, int? count = null)
        {
            var item = CommandBindings.CreateMenuItem(id, action, () => ready && available && !WorkspaceCommandBusy && !_selectionCommandRunning,
                () => ready && available ? null : unavailable);
            item.Tag = id;
            if (count is { } number && number != 1) item.Header = CommandCatalog.Label(id) + " (" + number + ")";
            menu.Items.Add(item);
            if (!ready || !available)
            {
                // Disabled menu entries are skipped by native keyboard navigation.
                // This enabled explanatory item exposes the reason without hovering.
                string reason = CommandCatalog.Label(id) + ": " + unavailable;
                var explanation = new MenuItem
                {
                    Header = reason,
                    Icon = CommandBindings.Icon("thread.details"),
                    Command = new DelegateCommand(() => ShowNotification(Texts.T("notification.session"), reason,
                        Avalonia.Controls.Notifications.NotificationType.Information))
                };
                Avalonia.Automation.AutomationProperties.SetName(explanation, reason);
                menu.Items.Add(explanation);
            }
            return item;
        }
        if (!frozen.Boards)
        {
            Add("thread.read", async () => await OpenFrozenReaderAsync(frozen, grid),
                frozen.Threads.Length == 1 && grid.SelectedItems.Count == 1);
            Add("gallery.open", async () => await OpenGalleryAsync(frozen.Lead, origin: grid));
        }
        Add("thread.details", () => OpenFrozenDetails(frozen));
        if (frozen.Boards) Add("board.filters", async () => { if (frozen.Board != null) await OpenBoardAdmissionAsync(frozen.Board); });
        menu.Items.Add(new Separator());
        if (!frozen.Boards) Add("export.open-html-dialog", async () => await ExportFrozenThreadsAsync(frozen.Threads, grid), count: frozen.Threads.Length);
        Add("selection.copy-links", async () => await CopyUrlsAsync(frozen.Urls), count: frozen.Urls.Length);
        Add("location.open-folder", () => OpenDirectory(frozen.Folder));
        Add("location.open-source", () => OpenTarget(frozen.Url));
        menu.Items.Add(new Separator());
        Add("watch.check", async () => await RunCheckTargetsAsync(frozen.Checkable, retry: false), frozen.Checkable.Length > 0, frozen.Checkable.Length);
        if (!frozen.Boards)
            Add("watch.retry-media", async () => await RunCheckTargetsAsync(frozen.Retriable, retry: true), frozen.Retriable.Length > 0, frozen.Retriable.Length);
        Add("rules.edit-scope", () => EditFrozenRules(frozen.Lead));
        menu.Items.Add(new Separator());
        Add("watch.remove", async () => await RunSelectionCommandAsync(frozen.WatchIds,
            id => frozen.Boards ? _session.RemoveBoardAsync(id) : _session.RemoveThreadAsync(id)), count: frozen.WatchIds.Length);
    }

    private async Task OpenFrozenReaderAsync(DownloadMenuTarget frozen, Control origin)
    {
        OpenLargeReader(origin, frozen.Scope);
        await ArchivePage.LoadThreadAsync(frozen.Lead);
    }

    private void OpenFrozenDetails(DownloadMenuTarget frozen)
    {
        DataGrid grid = DownloadList(frozen.Boards);
        if (frozen.Boards && _boardRows.TryGetValue(frozen.LeadWatch, out BoardRow? board))
        { _boardLeadId = board.WatchId; if (!grid.SelectedItems.Contains(board)) grid.SelectedItem = board; }
        else if (!frozen.Boards && _threadRows.TryGetValue(frozen.LeadWatch, out ThreadRow? thread))
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
        if (ids.Length == 0 || WorkspaceCommandBusy) return;
        Action restore = CaptureDownloadSelectionOrigin(origin);
        if (!NavigateWorkspace(WorkspacePage.Archive, activateArchive: false)) return;
        await ArchivePage.OpenExportForThreadsAsync(ids, origin, restore);
    }
}
