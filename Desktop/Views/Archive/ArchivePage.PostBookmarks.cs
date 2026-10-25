using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Persistence;

namespace MeowChan;

public partial class ArchivePage
{
    internal Control BookmarkCommandOwner => Control<Grid>("ArchiveReaderPane");
    internal Func<PostBookmarkKey, Control?, Task>? EditPostBookmark { get; set; }
    internal Action? OpenPostBookmarks { get; set; }
    internal PostBookmarkKey? FocusedBookmarkPost => _thread != null && ReaderShowsPosts
        && Control<ListBox>("ArchivePosts").SelectedItem is ArchivePostRow row
        && _thread.FindPostIndex(row.Id) >= 0 ? new(_thread.Summary.Id, row.Id) : null;

    private void InitializePostBookmarks()
    {
        Button manager = CommandBindings.CreateButton("post-bookmarks.open-manager", () => OpenPostBookmarks?.Invoke(),
            () => !_ended && _available && OpenPostBookmarks != null, iconWithLabel: true);
        manager.Name = "ArchivePostBookmarks";
        Control<ActionToolBar>("ArchiveResultActions").Items.Add(manager);
        // Installed on the pane itself: the same handler survives reader detach/reattach.
        Control<Grid>("ArchiveReaderPane").AddHandler(ContextRequestedEvent, OnPostBookmarkContext,
            RoutingStrategies.Bubble);
    }

    private void OnPostBookmarkContext(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _ended || !_available || _thread == null || EditPostBookmark == null || e.Source is not Control source) return;
        Control[] ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        if (ancestors.OfType<ArchiveAttachmentView>().Any()) return; // Existing media menu carries the same action.
        ArchivePostRow? row = ancestors.Select(node => node.DataContext).OfType<ArchivePostRow>().FirstOrDefault();
        if (row == null && !e.TryGetPosition(this, out _)) row = Control<ListBox>("ArchivePosts").SelectedItem as ArchivePostRow;
        if (row == null || _thread.FindPostIndex(row.Id) < 0) return;
        var frozen = new PostBookmarkKey(_thread.Summary.Id, row.Id);
        ContextMenu menu = new();
        menu.Items.Add(CreatePostBookmarkMenuItem(frozen, source));
        e.Handled = true; menu.Open(source);
    }

    private MenuItem CreatePostBookmarkMenuItem(PostBookmarkKey frozen, Control origin)
        => CommandBindings.CreateMenuItem("post-bookmarks.edit-post", async () =>
        {
            try { if (EditPostBookmark != null) await EditPostBookmark(frozen, origin); }
            catch (Exception error) { if (!_ended) Message(Texts.T("bookmarks.failed", error.Message), true); }
        }, () => !_ended && EditPostBookmark != null);

    /// <summary>Reads one local immutable view and refuses any missing-post fallback.</summary>
    internal async Task<bool> OpenPostBookmarkAsync(PostBookmarkKey key, Action beforeShow,
        CancellationToken token = default)
    {
        key.Validate();
        if (_ended || _busy || !_available || _openReader == null) return false;
        int request = _contentRequest;
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await using ArchiveReader reader = await _openReader(operation.Token);
        PostBookmarkTarget? target = await PostBookmarkTarget.ResolveAsync(reader, key, operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (_ended || request != _contentRequest) return false;
        if (target == null) return false;
        // Capture the previous archive state before publishing the exact target view.
        beforeShow();
        _history.Clear(); CancelReferences();
        ShowCapturedView(target.View, target.PostIndex / PageSize * PageSize, key.Post);
        await RestoreReaderAnchorAsync(new ReaderAnchor(key.Thread, key.Post, 0, key.Post));
        return !_ended && _thread?.Summary.Id == key.Thread && _thread.FindPostIndex(key.Post) >= 0;
    }
}
