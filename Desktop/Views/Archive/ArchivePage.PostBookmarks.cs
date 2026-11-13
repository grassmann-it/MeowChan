using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
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
    internal Control BookmarkCommandOwner => ArchiveReaderPane;
    internal Func<PostBookmarkKey, Control?, Task>? EditPostBookmark { get; set; }
    internal Action? OpenPostBookmarks { get; set; }
    internal PostBookmarkKey? FocusedBookmarkPost => _thread != null && ReaderShowsPosts
        && ArchivePosts.SelectedItem is ArchivePostRow row
        && _thread.FindPostIndex(row.Id) >= 0 ? new(_thread.Summary.Id, row.Id) : null;

    private void InitializePostBookmarks()
    {
        Button manager = CommandBindings.CreateButton("post-bookmarks.open-manager", () => { if (!IsFilenameSearchOpen) OpenPostBookmarks?.Invoke(); },
            () => !_ended && !IsFilenameSearchOpen && _available && OpenPostBookmarks != null, iconWithLabel: true);
        manager.Name = "ArchivePostBookmarks";
        ArchiveResultActions.Items.Add(manager);
        // Installed on the pane itself: the same handler survives reader detach/reattach.
        ArchiveReaderPane.AddHandler(ContextRequestedEvent, OnPostBookmarkContext,
            RoutingStrategies.Bubble);
    }

    private void OnPostBookmarkContext(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || _ended || IsFilenameSearchOpen || !_available || _thread == null || e.Source is not Control source) return;
        Control[] ancestors = source.GetSelfAndVisualAncestors().OfType<Control>().ToArray();
        if (ancestors.OfType<ArchiveAttachmentView>().Any()) return; // Existing media menu carries the same action.
        ArchivePostRow? row = ancestors.Select(node => node.DataContext).OfType<ArchivePostRow>().FirstOrDefault();
        if (row == null && !e.TryGetPosition(this, out _)) row = ArchivePosts.SelectedItem as ArchivePostRow;
        if (row == null || _thread.FindPostIndex(row.Id) < 0) return;
        ContextMenu menu = BuildPostMenu(row, source);
        e.Handled = true; menu.Open(source);
    }

    /// <summary>
    /// The post menu: copy text, number and (where the source's address scheme is known) link, then the bookmark action.
    /// Everything is frozen to the post it was opened on; copying is local only and never starts a network request.
    /// </summary>
    internal ContextMenu BuildPostMenu(ArchivePostRow row, Control origin)
    {
        ArgumentNullException.ThrowIfNull(row);
        ResourceId thread = _thread!.Summary.Id;
        string? link = PostMenuText.Link(thread, _thread.Summary.Url, row.Id);
        ContextMenu menu = new();
        menu.Items.Add(CreatePostCopyItem("post.copy-text", PostMenuText.Text(row), "post.copy.textDone"));
        menu.Items.Add(CreatePostCopyItem("post.copy-number", PostMenuText.Number(row.Id), "post.copy.numberDone"));
        if (link != null) menu.Items.Add(CreatePostCopyItem("post.copy-link", link, "post.copy.linkDone"));
        if (EditPostBookmark != null)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(CreatePostBookmarkMenuItem(new PostBookmarkKey(thread, row.Id), origin));
        }
        return menu;
    }

    /// <summary>Test seam: receives the copied text instead of the clipboard.</summary>
    internal Func<string, Task>? CopyTextForTests { get; set; }

    private MenuItem CreatePostCopyItem(string commandId, string text, string doneKey)
        => CommandBindings.CreateMenuItem(commandId, () => _ = CopyPostTextAsync(text, doneKey), () => !_ended);

    private async Task CopyPostTextAsync(string text, string doneKey)
    {
        try
        {
            if (CopyTextForTests is { } seam) await seam(text);
            else if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
            else throw new InvalidOperationException("The clipboard is unavailable.");
            if (!_ended) Message(Texts.T(doneKey));
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_ended) Message(Texts.T("post.copy.failed"), true); }
    }

    private MenuItem CreatePostBookmarkMenuItem(PostBookmarkKey frozen, Control origin)
        => CommandBindings.CreateMenuItem("post-bookmarks.edit-post", async () =>
        {
            try { if (!IsFilenameSearchOpen && EditPostBookmark != null) await EditPostBookmark(frozen, origin); }
            catch (Exception error) { if (!_ended) Message(Texts.T("bookmarks.failed", error.Message), true); }
        }, () => !_ended && !IsFilenameSearchOpen && EditPostBookmark != null);

    /// <summary>Reads one local immutable view and refuses any missing-post fallback.</summary>
    internal async Task<bool> OpenPostBookmarkAsync(PostBookmarkKey key, Action beforeShow,
        CancellationToken token = default)
    {
        key.Validate();
        if (_ended || IsFilenameSearchOpen || _busy || !_available || _openReader == null) return false;
        int request = _contentRequest;
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await using ArchiveReader reader = await _openReader(operation.Token);
        PostBookmarkTarget? target = await PostBookmarkTarget.ResolveAsync(reader, key, operation.Token);
        operation.Token.ThrowIfCancellationRequested();
        if (_ended || IsFilenameSearchOpen || request != _contentRequest) return false;
        if (target == null) return false;
        // Capture the previous archive state before publishing the exact target view.
        beforeShow();
        _history.Clear(); CancelReferences();
        ShowCapturedView(target.View, target.PostIndex / PageSize * PageSize, key.Post);
        await RevealPostAsync(key.Post); // An explicit jump: a post the reader view hides is shown.
        await RestoreReaderAnchorAsync(new ReaderAnchor(key.Thread, key.Post, 0, key.Post));
        return !_ended && _thread?.Summary.Id == key.Thread && _thread.FindPostIndex(key.Post) >= 0;
    }
}
