using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan;

public partial class MainWindow
{
    private PostBookmarksDialog? _postBookmarksDialog;
    private PostBookmarksDialog? _postBookmarkEditor;
    private Action? _postBookmarkReaderReturn;
    private readonly List<IDisposable> _postBookmarkRegistrations = new();
    private MenuItem? _postBookmarksMenu;
    private bool CanOpenPostBookmarks => !_isClosing && _session.State == DesktopSessionState.Ready
        && !_largeReaderOpen && _gallery == null && _postBookmarkEditor == null;

    private void InitializePostBookmarks()
    {
        _postBookmarksMenu = CommandBindings.CreateMenuItem("post-bookmarks.open-manager",
            async () => await ShowPostBookmarksAsync(), () => CanOpenPostBookmarks);
        this.FindControl<MenuItem>("WorkspaceMenuItem")!.Items.Insert(2, _postBookmarksMenu);
        ArchivePage.EditPostBookmark = EditPostBookmarkAsync;
        ArchivePage.OpenPostBookmarks = async () => await ShowPostBookmarksAsync();
        Closed += (_, _) =>
        {
            foreach (IDisposable registration in _postBookmarkRegistrations) registration.Dispose();
            _postBookmarksDialog?.CloseForProfileEnd();
            _postBookmarkEditor?.CloseForProfileEnd();
        };
    }

    // W17 calls after W13 creates _shortcutRouter and workspace pages are available.
    private void RegisterPostBookmarkCommands()
    {
        // W17 shared registration hunk. W13 owns the central router lifecycle.
        _postBookmarkRegistrations.Add(_shortcutRouter!.Register(new CommandRegistration(
            "post-bookmarks.open-manager", this, () => !_isClosing && !_largeReaderOpen && _gallery == null
                && _postBookmarkEditor == null, () => CanOpenPostBookmarks,
            async () => await ShowPostBookmarksAsync())));
        _postBookmarkRegistrations.Add(_shortcutRouter!.Register(new CommandRegistration(
            "post-bookmarks.edit-post", ArchivePage.BookmarkCommandOwner,
            () => !_isClosing && _gallery == null && _postBookmarkEditor == null && ArchivePage.ReaderShowsPosts,
            () => _session.State == DesktopSessionState.Ready && ArchivePage.FocusedBookmarkPost != null,
            async () => { if (ArchivePage.FocusedBookmarkPost is { } key) await EditPostBookmarkAsync(key, null); }, Priority: 200)));
    }

    private async Task EditPostBookmarkAsync(PostBookmarkKey key, Control? origin)
    {
        if (_isClosing || _session.State != DesktopSessionState.Ready) return;
        if (_postBookmarkEditor != null) { _postBookmarkEditor.Activate(); return; }
        PostBookmarksDialog editor = new(_session, (_, _) => Task.FromResult(false));
        _postBookmarkEditor = editor;
        try
        {
            Task closed = editor.ShowDialog(this);
            await editor.InitializeAsync(key);
            await closed;
        }
        finally
        {
            _postBookmarkEditor = null;
            if (!_isClosing && origin?.IsEffectivelyVisible == true) origin.Focus();
        }
    }

    private async Task ShowPostBookmarksAsync()
    {
        if (!CanOpenPostBookmarks) return;
        if (_postBookmarksDialog != null)
        {
            if (_largeReaderOpen) CloseLargeReader();
            if (!_postBookmarksDialog.IsVisible) _postBookmarksDialog.ReturnFromReader();
            else _postBookmarksDialog.Activate();
            return;
        }
        PostBookmarksDialog dialog = new(_session, OpenBookmarkedPostAsync);
        _postBookmarksDialog = dialog;
        if (_largeReaderOpen) CloseLargeReader();
        dialog.Closed += (_, _) =>
        {
            if (ReferenceEquals(_postBookmarksDialog, dialog)) _postBookmarksDialog = null;
            _postBookmarkReaderReturn = null;
        };
        dialog.Show(this);
        await dialog.InitializeAsync();
    }

    private async Task<bool> OpenBookmarkedPostAsync(PostBookmarkKey key, CancellationToken token)
    {
        if (_isClosing || _largeReaderOpen || _gallery != null) return false;
        return await ArchivePage.OpenPostBookmarkAsync(key, () =>
        {
            OpenLargeReader();
            // No bookmark-specific thread scope. Existing reader/quote/gallery navigation remains authoritative.
            _postBookmarkReaderReturn = () => _postBookmarksDialog?.ReturnFromReader();
        }, token);
    }

    private void RefreshPostBookmarkLabels()
    {
        if (_postBookmarksMenu != null)
        { CommandBindings.Apply(_postBookmarksMenu, "post-bookmarks.open-manager"); CommandBindings.Refresh(_postBookmarksMenu); }
    }

    private void ReturnToPostBookmarks()
    {
        Action? restore = _postBookmarkReaderReturn;
        _postBookmarkReaderReturn = null;
        if (!_isClosing) restore?.Invoke();
    }
}
