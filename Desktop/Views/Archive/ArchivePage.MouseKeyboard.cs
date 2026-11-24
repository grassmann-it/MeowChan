using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.MediaActions;

namespace MeowChan;

// Archive page, mouse and keyboard quick wins. The middle click on a thread row of the card list and of the table opens its source in the browser (the menu's
// "Open source in browser"); on a media tile, a row of the media table, the thumbnail of a post and the picture of the media sidebar it opens the saved file
// with its default program. The thread table also searches by type (audit V7). Each uses the command the
// row's menu has, under that command's own rules.
public partial class ArchivePage
{
    private readonly MiddleClickTracker _threadMiddleClick = new();
    private readonly MiddleClickTracker _mediaMiddleClick = new();
    private readonly MiddleClickTracker _mediaTableMiddleClick = new();
    private readonly MiddleClickTracker _postMediumMiddleClick = new();
    private (ArchiveAssetRow? Saved, (ArchivedThreadView Thread, ArchivePostRow Post, ArchiveAttachmentRow Attachment)? Inline)? _sidebarMiddleTarget;

    private void AttachMouseKeyboardQuickWins()
    {
        foreach (Control list in new Control[] { ArchiveThreads, ArchiveThreadTable })
        {
            list.AddHandler(PointerPressedEvent, OnThreadMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
            list.AddHandler(PointerReleasedEvent, OnThreadMiddleReleased, RoutingStrategies.Tunnel);
        }
        ArchiveMediaTiles.AddHandler(PointerPressedEvent, OnMediaTileMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ArchiveMediaTiles.AddHandler(PointerReleasedEvent, OnMediaTileMiddleReleased, RoutingStrategies.Tunnel);
        ArchiveMediaGrid.AddHandler(PointerPressedEvent, OnMediaTableMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ArchiveMediaGrid.AddHandler(PointerReleasedEvent, OnMediaTableMiddleReleased, RoutingStrategies.Tunnel);
        // The pane is the one control of the reader that moves between the archive view and the large reader (a host outside the page), so the posts' thumbnails
        // are watched there; the sidebar sits beside the pane in the large reader and watches itself (EnsureMediaSidebar).
        ArchiveReaderPane.AddHandler(PointerPressedEvent, OnPostMediumMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        ArchiveReaderPane.AddHandler(PointerReleasedEvent, OnPostMediumMiddleReleased, RoutingStrategies.Tunnel);
        TypeAheadSearch.AttachDataGrid(ArchiveThreadTable, row => (row as ArchiveThreadRow)?.Summary.Title,
            source => MainWindow.IsListCommandSource(source, ArchiveThreadTable), ArchiveListAcceptsQuickInput);
    }

    // The rules of the list's key commands (OnArchiveListKeyDown) for the page itself: nothing over it, no search or export open, no reload running.
    private bool ArchiveListAcceptsQuickInput() =>
        !_ended && _available && !_largeReader && !_busy && !IsFilenameSearchOpen && !ArchiveExportDialog.IsVisible && !IsReaderDialogOpen;

    private void OnThreadMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control list || !MiddleClickTracker.IsPress(e, list)) { _threadMiddleClick.Cancel(); return; }
        _threadMiddleClick.Pressed(MiddleClickTracker.RowAt<ArchiveThreadRow>(e.Source, list));
    }

    private void OnThreadMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control list || e.Handled || !MiddleClickTracker.IsRelease(e)) return;
        ArchiveThreadRow? row = MiddleClickTracker.RowAt<ArchiveThreadRow>(MiddleClickTracker.HitAt(e, list), list);
        if (!_threadMiddleClick.Released(row) || row == null) return;
        e.Handled = true;
        OpenThreadSourceByMiddleClick(row);
    }

    /// <summary>The thread's source in the browser, by the same rule as the menu entry: the shell can open it and the thread has an address (else the page says so).</summary>
    internal bool OpenThreadSourceByMiddleClick(ArchiveThreadRow row)
    {
        if (!ArchiveListAcceptsQuickInput() || SelectionLeadAction == null) return false;
        if (string.IsNullOrWhiteSpace(row.Summary.Url)) { Message(Texts.T("archive.command.noSource")); return false; }
        RunArchiveLeadAction(row, "browser");
        return true;
    }

    private void OnMediaTileMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!MiddleClickTracker.IsPress(e, ArchiveMediaTiles)) { _mediaMiddleClick.Cancel(); return; }
        _mediaMiddleClick.Pressed(MiddleClickTracker.RowAt<ArchiveAssetRow>(e.Source, ArchiveMediaTiles));
    }

    private void OnMediaTileMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Handled || !MiddleClickTracker.IsRelease(e)) return;
        ArchiveAssetRow? asset = MiddleClickTracker.RowAt<ArchiveAssetRow>(MiddleClickTracker.HitAt(e, ArchiveMediaTiles), ArchiveMediaTiles);
        if (!_mediaMiddleClick.Released(asset) || asset == null) return;
        e.Handled = true;
        OpenMediaByMiddleClick(asset);
    }

    private void OnMediaTableMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (!MiddleClickTracker.IsPress(e, ArchiveMediaGrid)) { _mediaTableMiddleClick.Cancel(); return; }
        _mediaTableMiddleClick.Pressed(MiddleClickTracker.RowAt<ArchiveAssetRow>(e.Source, ArchiveMediaGrid));
    }

    private void OnMediaTableMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Handled || !MiddleClickTracker.IsRelease(e)) return;
        ArchiveAssetRow? asset = MiddleClickTracker.RowAt<ArchiveAssetRow>(MiddleClickTracker.HitAt(e, ArchiveMediaGrid), ArchiveMediaGrid);
        if (!_mediaTableMiddleClick.Released(asset) || asset == null) return;
        e.Handled = true;
        OpenMediaByMiddleClick(asset);
    }

    // The thumbnail of a post's medium (not its file line, star or video controls; not the sidebar, which has its own handlers in either host).
    private ArchiveAttachmentView? PostThumbnailAt(object? source)
    {
        if (source is not Visual visual) return null;
        Visual[] path = visual.GetSelfAndVisualAncestors().ToArray();
        if (_mediaSidebar != null && path.Any(node => ReferenceEquals(node, _mediaSidebar))) return null;
        ArchiveAttachmentView? view = path.OfType<ArchiveAttachmentView>().FirstOrDefault();
        return view != null && path.Any(node => ReferenceEquals(node, view.PreviewButton)) ? view : null;
    }

    private void OnPostMediumMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control pane || !MiddleClickTracker.IsPress(e, pane)) { _postMediumMiddleClick.Cancel(); return; }
        _postMediumMiddleClick.Pressed(PostThumbnailAt(e.Source));
    }

    private void OnPostMediumMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not Control pane || e.Handled || !MiddleClickTracker.IsRelease(e)) return;
        ArchiveAttachmentView? view = PostThumbnailAt(MiddleClickTracker.HitAt(e, pane));
        if (!_postMediumMiddleClick.Released(view) || view?.DataContext is not ArchiveAttachmentRow attachment) return;
        e.Handled = true;
        string? postId = view.GetSelfAndVisualAncestors().Select(node => node.DataContext).OfType<ArchivePostRow>().FirstOrDefault()?.Id;
        OpenMediaByMiddleClick(null, attachment, postId);
    }

    // The picture of the media sidebar (its frame, a video's play surface included; not the header or the video row), in either host.
    private void OnSidebarMiddlePressed(object? sender, PointerPressedEventArgs e)
    {
        _sidebarMiddleTarget = _mediaSidebarShown && _mediaSidebar is { } sidebar && MiddleClickTracker.IsPress(e, sidebar)
            && InSidebarFrame(e.Source, sidebar) ? (SavedMediaSidebarRow, _displayedSidebarMedium) : null;
    }

    private void OnSidebarMiddleReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Handled || !MiddleClickTracker.IsRelease(e) || _mediaSidebar is not { } sidebar) return;
        var pressed = _sidebarMiddleTarget;
        _sidebarMiddleTarget = null;
        if (pressed is not { } target || !_mediaSidebarShown || !InSidebarFrame(MiddleClickTracker.HitAt(e, sidebar), sidebar)
            || target.Saved != SavedMediaSidebarRow || target.Inline != _displayedSidebarMedium) return;
        if (target.Saved is { } saved) OpenMediaByMiddleClick(saved);
        else if (target.Inline is { } shown) OpenMediaByMiddleClick(null, shown.Attachment, shown.Post.Id);
        else return;
        e.Handled = true;
    }

    private static bool InSidebarFrame(object? source, ReaderMediaSidebar sidebar) =>
        source is Visual visual && visual.GetSelfAndVisualAncestors().Any(node => ReferenceEquals(node, sidebar.FrameArea));

    private void OpenMediaByMiddleClick(ArchiveAssetRow? asset, ArchiveAttachmentRow? attachment = null, string? postId = null)
    {
        if (_ended || !_available || _busy || _thread == null || _selectedThread == null || IsReaderDialogOpen
            || OpenMediaExternallyAsync is not { } open) return;
        if (attachment != null) asset = postId == null ? null : FindMatchingAsset(_selectedThread, postId, attachment.Attachment, _assets);
        if (asset != null && ResolveArchiveMediaSubject(asset, attachment, postId).Target is { } entry)
            OpenActions.Start(() => open(entry), "archive.media.middleClick");
    }

    /// <summary>The one "Show in folder" of the media menu's entry and of the middle click: the file's folder, when the host can open folders and the file has one. False when not available.</summary>
    private bool TryShowMediaInFolder(MeowChan.Storage.MediaAssetReference? asset)
    {
        if (asset == null || _openFolder == null || System.IO.Path.GetDirectoryName(asset.Path) is not string directory) return false;
        _openFolder(directory);
        return true;
    }
}
