using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class MainWindow
{
    private ReaderGroupsDialog? _readerGroupsDialog;
    private ReaderGroupReadRequest? _readerGroupReturn;
    private long _readerGroupReadGeneration;
    private Control? _readerGroupsOrigin;
    internal bool CanOpenReaderGroups => _archivePage?.IsFilenameSearchOpen != true && !_isClosing && !_largeReaderOpen && _gallery == null
        && _readerGroupsDialog == null && _session.State == DesktopSessionState.Ready;

    private void InitializeReaderGroups()
    {
        MenuItem item = ReaderGroupsMenuItem;
        CommandBindings.Apply(item, "reader-groups.open-manager");
        item.Click += async (_, _) => await OpenReaderGroupsAsync(item);
        ArchivePage.OpenReaderGroupsRequested = async origin => await OpenReaderGroupsAsync(origin);
        ArchivePage.SearchStateChanged += RefreshReaderGroupsEntry;
        RefreshReaderGroupsEntry();
    }

    // Installed once after both workspace pages and the central router exist.
    private void RegisterReaderGroupCommands()
    {
        if (_shortcutRouter == null || _archivePage == null) return;
        _shortcutRouter.Register(new CommandRegistration("reader-groups.open-manager", this,
            () => !_largeReaderOpen && _gallery == null,
            () => CanOpenReaderGroups, () => _ = OpenReaderGroupsAsync(), Priority: 0));
        Control pane = _archivePage.FindControl<Control>("ArchiveReaderPane")!;
        _shortcutRouter.Register(new CommandRegistration("reader.skip-unavailable-group-member", pane,
            () => _largeReaderOpen && (_gallery == null || _gallerySuspended),
            () => _archivePage.CanSkipUnavailableGroupMember,
            () => _ = _archivePage.SkipUnavailableGroupMemberAsync(), Priority: 200));
    }

    private void RefreshReaderGroupsEntry()
    {
        if (this.FindControl<MenuItem>("ReaderGroupsMenuItem") is MenuItem menu)
        {
            CommandBindings.Apply(menu, "reader-groups.open-manager");
            menu.IsEnabled = CanOpenReaderGroups;
        }
        if (_archivePage?.FindControl<Button>("ArchiveReaderGroups") is Button button)
        { CommandBindings.Apply(button, "reader-groups.open-manager"); button.IsEnabled = CanOpenReaderGroups; }
    }

    internal async Task OpenReaderGroupsAsync(Control? origin = null)
    {
        if (!CanOpenReaderGroups) return;
        _readerGroupsOrigin = origin;
        await ShowReaderGroupsAsync(null);
    }

    private async Task ShowReaderGroupsAsync(ReaderGroupsDialogOrigin? restore)
    {
        if (_archivePage?.IsFilenameSearchOpen == true || _isClosing || _session.State != DesktopSessionState.Ready) return;
        ReaderGroupsDialog dialog = new(_session, restore);
        _readerGroupsDialog = dialog;
        ReaderGroupReadRequest? request;
        try { request = await dialog.ShowDialog<ReaderGroupReadRequest?>(this); }
        finally { if (ReferenceEquals(_readerGroupsDialog, dialog)) _readerGroupsDialog = null; }
        if (_archivePage?.IsFilenameSearchOpen == true || _isClosing || _session.State != DesktopSessionState.Ready) return;
        if (request == null) { _readerGroupsOrigin?.Focus(); _readerGroupsOrigin = null; return; }
        await OpenReaderGroupAsync(request);
    }

    internal async Task OpenReaderGroupAsync(ReaderGroupReadRequest request)
    {
        if (_archivePage?.IsFilenameSearchOpen == true || _isClosing || _largeReaderOpen || _readerGroupsDialog != null
            || _session.State != DesktopSessionState.Ready || request.Threads.Count == 0) return;
        long generation = ++_readerGroupReadGeneration;
        _readerGroupReturn = request;
        ArchivePage page = ArchivePage;
        OpenLargeReader(_readerGroupsOrigin, request.Threads);
        page.BeginReaderGroupScope(request.Threads);
        int scopeGeneration = page.ReaderGroupScopeGeneration;
        bool loaded = await page.LoadReaderGroupThreadAsync(request.Threads[0]);
        // A false result also means a canceled/retired scope. Its continuation has no authority
        // over a reopened manager or a later reader, even if it targets the same group/thread.
        if (generation != _readerGroupReadGeneration || !ReferenceEquals(_readerGroupReturn, request)
            || !_largeReaderOpen || _isClosing || _session.State != DesktopSessionState.Ready
            || !ReferenceEquals(_archivePage, page) || page.ReaderGroupScopeGeneration != scopeGeneration) return;
        if (!loaded)
        {
            // A target can disappear between the dialog's availability snapshot and opening.
            // Returning to the saved dialog is explicit and does not choose another archive row.
            _readerGroupReturn = request with { Origin = request.Origin with
                { Notice = null, NoticeReason = ReaderGroupsNoticeReason.FirstMemberUnavailable } };
            CloseLargeReader();
        }
    }

    private void ReturnToReaderGroups(bool restoreOrigin)
    {
        long generation = ++_readerGroupReadGeneration;
        ReaderGroupReadRequest? request = _readerGroupReturn;
        _readerGroupReturn = null;
        ArchivePage.EndReaderGroupScope();
        if (request != null && restoreOrigin && !_isClosing)
            Dispatcher.UIThread.Post(async () =>
            {
                if (generation == _readerGroupReadGeneration && !_largeReaderOpen && _readerGroupsDialog == null)
                    await ShowReaderGroupsAsync(request.Origin);
            });
    }
}
