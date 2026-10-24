using System;
using System.Collections.Generic;
using MeowChan.Interfaces;
using Avalonia.Controls;
using Avalonia.Media;
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
        if (_largeReaderOpen || _isClosing) return;
        // The outer close path chooses the original control's focus after
        // reattaching. Delayed anchor work restores geometry without taking it.
        _readerRestoreOrigin = ArchivePage.CaptureGalleryOrigin(restoreReaderFocus: false);
        _readerOriginPage = _selectedPage;
        _readerOriginFocus = origin;
        ContentControl host = this.FindControl<ContentControl>("ReaderHost")!;
        host.Content = ArchivePage.DetachReader();
        host.IsVisible = true;
        _largeReaderOpen = true;
        RefreshReadingResumePresentation();
        if (frozenThreads != null) ArchivePage.SetReaderScope(frozenThreads);
        else _ = ArchivePage.FreezeReaderScopeAsync();
    }

    private void CloseLargeReader(bool restoreOrigin = true)
    {
        if (!_largeReaderOpen) return;
        if (_gallery != null && restoreOrigin) CloseGallery(restoreOrigin: false);
        ContentControl host = this.FindControl<ContentControl>("ReaderHost")!;
        Control pane = (Control)host.Content!;
        host.Content = null;
        host.IsVisible = false;
        ArchivePage.AttachReader(pane);
        _largeReaderOpen = false;
        RefreshReadingResumePresentation();
        if (restoreOrigin)
        {
            _readerRestoreOrigin?.Invoke();
            if (_selectedPage != _readerOriginPage) NavigateWorkspace(_readerOriginPage, activateArchive: false);
            _readerOriginFocus?.Focus();
        }
        _readerRestoreOrigin = null;
        _readerOriginFocus = null;
        ReturnToReaderGroups(restoreOrigin); // URSA-12 shared close hook.
        if (restoreOrigin) ReturnToPostBookmarks();
    }
}
