using System;
using System.Collections.Generic;
using MeowChan.Interfaces;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using MeowChan.Desktop;

namespace MeowChan;

public partial class MainWindow
{
    private bool _largeReaderOpen;
    private Action? _readerRestoreOrigin;
    private WorkspacePage _readerOriginPage;
    private Control? _readerOriginFocus;

    private void OpenLargeReader(Control? origin = null) => OpenLargeReader(origin, null);

    private void OpenLargeReader(Control? origin, IReadOnlyList<ResourceId>? frozenThreads)
    {
        if (_largeReaderOpen || _isClosing || _archivePage?.IsReaderDialogOpen == true) return;
        // The outer close path chooses the original control's focus after
        // reattaching. Delayed anchor work restores geometry without taking it.
        _readerRestoreOrigin = ArchivePage.CaptureGalleryOrigin(restoreReaderFocus: false);
        _readerOriginPage = _selectedPage;
        _readerOriginFocus = origin;
        ArchivePage.SetReaderCloseTarget(_selectedPage switch
        {
            WorkspacePage.Downloads => "command.reader.close.toDownloads",
            WorkspacePage.Archive => "command.reader.close.toArchive",
            _ => null
        });
        ContentControl host = ReaderHost;
        host.Content = ArchivePage.DetachReader();
        host.IsVisible = true;
        _largeReaderOpen = true;
        RefreshReadingResumePresentation();
        if (frozenThreads != null) ArchivePage.SetReaderScope(frozenThreads);
        else _ = ArchivePage.FreezeReaderScopeAsync();
    }

    // O2-01: the origin of an Enter or a double-click (the list, a tapped row) takes no focus after the reader closed; the list's selected row does.
    // The page is shown again in this turn and may not have its layout yet: a focus that did not take, or was lost again, is repeated once after it.
    private void RestoreReaderOriginFocus(Control? origin, WorkspacePage page)
    {
        Control? Target() => ReaderOriginFocus.Resolve(origin, page switch
        {
            WorkspacePage.Downloads => ThreadsDataGrid,
            WorkspacePage.Archive => ArchivePage.ThreadListFocusTarget,
            _ => null
        });
        bool focused = Target()?.Focus() == true;
        if (focused && FocusManager?.GetFocusedElement() != null) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_largeReaderOpen && !_isClosing && FocusManager?.GetFocusedElement() == null) Target()?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void CloseLargeReader(bool restoreOrigin = true)
    {
        if (!_largeReaderOpen || (restoreOrigin && _archivePage?.IsFilenameSearchOpen == true)) return;
        if (_gallery != null && restoreOrigin) CloseGallery(restoreOrigin: false);
        ContentControl host = ReaderHost;
        Control pane = (Control)host.Content!;
        host.Content = null;
        host.IsVisible = false;
        ArchivePage.AttachReader(pane);
        _largeReaderOpen = false;
        ArchivePage.SetReaderCloseTarget(null);
        RefreshReadingResumePresentation();
        if (restoreOrigin)
        {
            _readerRestoreOrigin?.Invoke();
            if (_selectedPage != _readerOriginPage) NavigateWorkspace(_readerOriginPage, activateArchive: false);
            RestoreReaderOriginFocus(_readerOriginFocus, _readerOriginPage);
        }
        _readerRestoreOrigin = null;
        _readerOriginFocus = null;
        ReturnToReaderGroups(restoreOrigin); // URSA-12 shared close hook.
        if (restoreOrigin) ReturnToPostBookmarks();
    }
}
