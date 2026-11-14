using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    // One reservation owns opening, the native modal, its real session drain and result navigation.
    // Cancellation invalidates permission immediately, but never releases a still-draining owner.
    private sealed class FilenameSearchOwner(Window window, Control surface, CancellationToken lifetime)
    {
        internal readonly Window Window = window;
        internal readonly Control Surface = surface;
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        internal FilenameSearchDialog? Dialog;
    }
    private FilenameSearchOwner? _filenameSearchOwner;
    internal bool IsFilenameSearchOpen => _filenameSearchOwner != null;
    internal FilenameSearchDialog? FilenameSearchWindow => _filenameSearchOwner?.Dialog;
    internal Task FilenameSearchTask { get; private set; } = Task.CompletedTask;
    // Instance-only native-show boundary agreed with the host test authors. Production always
    // shows this real dialog; neither navigation nor the real session drain is replaceable.
    internal Func<FilenameSearchDialog, Window, Task<ArchiveFilenameHit?>>? FilenameSearchShowForTests { get; set; }

    private bool CanOpenFilenameSearch => !_ended && !_obscuredByGallery && _available && !_busy && _openReader != null
        && !IsReaderDialogOpen && !_savedSearchDialogOpen && !_transferPreviewOpening && _transferPreviewDialog == null
        && !_exportRunning && !_exportLoading && !_exportPicking && !_exportPickerOpen && !_exportResolving;

    // Page-level eligibility of the file name scope (both search fields share it); host smokes wait on it.
    internal bool FilenameSearchEligibleForTests => CanOpenFilenameSearch;

    private void UpdateFilenameSearchActions()
    {
        // AP2: the file name search has no own toolbar entry any more; the scope chip of the
        // archive field and of the reader find bar opens it (UpdateUnifiedSearch owns their state).
        UpdateUnifiedSearch();
        foreach (object? item in ArchiveResultActions.Items)
            if (item is Button { Name: "ArchivePostBookmarks" } manager)
                MeowChan.Desktop.Commands.CommandBindings.Refresh(manager);
    }

    // query: the text of the scoped search field. A non-empty query is searched as soon as the dialog opens;
    // null or empty opens the dialog idle, exactly like the former toolbar entry.
    internal Task OpenFilenameSearchAsync(Control? origin = null, string? query = null)
    {
        if (!CanOpenFilenameSearch) return Task.CompletedTask;
        Control surface = _largeReader ? ArchiveReaderPane : this;
        Window? window = (origin == null ? null : TopLevel.GetTopLevel(origin) as Window)
            ?? TopLevel.GetTopLevel(surface) as Window;
        if (window is not { IsVisible: true } || window.OwnedWindows.Any(child => child.IsVisible)
            || !surface.IsEffectivelyVisible
            || !ReferenceEquals(TopLevel.GetTopLevel(surface), window)) return Task.CompletedTask;
        CancelPendingGalleryOpen();
        var operation = new FilenameSearchOwner(window, surface, _lifetime.Token);
        _filenameSearchOwner = operation; // Synchronous, including before any injected Show await.
        // A text find in the open thread is closed as before; the reader bar in the File name scope stays
        // open with its draft, so the search can be refined after a cancelled dialog (AP2).
        if (ReaderScope == MeowChan.Desktop.Controls.SearchScope.Text) CloseThreadFind();
        ++_readerFocusRequest;
        ++_readerInteractionRevision;
        CancelReaderMediaActivation();
        CancelReferences();
        UpdateActions();
        return FilenameSearchTask = RunFilenameSearchAsync(operation, query);
    }

    private bool FilenameSearchMayReturn(FilenameSearchOwner operation) =>
        ReferenceEquals(_filenameSearchOwner, operation) && !operation.Cancellation.IsCancellationRequested
        && !_ended && _available && !_savedSearchDialogOpen && operation.Window.IsVisible
        && !operation.Window.OwnedWindows.Any(child => child.IsVisible) && operation.Surface.IsEffectivelyVisible
        && ReferenceEquals(TopLevel.GetTopLevel(operation.Surface), operation.Window);

    private void InvalidateFilenameSearch()
    {
        if (_filenameSearchOwner is not { } operation) return;
        operation.Cancellation.Cancel();
        if (operation.Dialog is { } dialog)
        {
            // The operation's finally owns the actual join. Closing alone is not a drain.
            _ = dialog.TerminateAsync();
            if (dialog.IsVisible) dialog.Close();
        }
    }

    private async Task RunFilenameSearchAsync(FilenameSearchOwner operation, string? query)
    {
        Window window = operation.Window;
        Visual[] ancestors = operation.Surface.GetSelfAndVisualAncestors().ToArray();
        void Closing(object? sender, WindowClosingEventArgs e) => InvalidateFilenameSearch();
        void Detached(object? sender, VisualTreeAttachmentEventArgs e) => InvalidateFilenameSearch();
        void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == Visual.IsVisibleProperty && !operation.Surface.IsEffectivelyVisible)
                InvalidateFilenameSearch();
        }
        window.Closing += Closing;
        operation.Surface.DetachedFromVisualTree += Detached;
        foreach (Visual ancestor in ancestors) ancestor.PropertyChanged += VisibilityChanged;
        try
        {
            if (!FilenameSearchMayReturn(operation)) return;
            var dialog = new FilenameSearchDialog(_openReader!, query);
            operation.Dialog = dialog;
            ArchiveFilenameHit? selected;
            try
            {
                selected = FilenameSearchShowForTests is { } show
                    ? await show(dialog, window) : await dialog.ShowDialog<ArchiveFilenameHit?>(window);
            }
            finally
            {
                await dialog.TerminateAsync();
                // Also covers a failed native Show; never release reservation before session drain.
                if (dialog.IsVisible) dialog.Close();
            }
            if (selected == null || !FilenameSearchMayReturn(operation)) return;
            await NavigateFilenameHitAsync(operation, selected);
        }
        catch (OperationCanceledException) when (operation.Cancellation.IsCancellationRequested) { }
        catch (Exception)
        {
            if (FilenameSearchMayReturn(operation)) Message(FilenameSearchCatalog.T("filename.search.failed"), true);
        }
        finally
        {
            window.Closing -= Closing;
            operation.Surface.DetachedFromVisualTree -= Detached;
            foreach (Visual ancestor in ancestors) ancestor.PropertyChanged -= VisibilityChanged;
            if (ReferenceEquals(_filenameSearchOwner, operation)) _filenameSearchOwner = null;
            operation.Cancellation.Dispose();
            if (!_ended) UpdateActions();
            // No delayed focus restoration: native modal ownership handles normal close, while
            // cancellation, workspace replacement and explicit navigation never focus an old origin.
        }
    }

    private async Task NavigateFilenameHitAsync(FilenameSearchOwner operation, ArchiveFilenameHit hit)
    {
        ArchivedThreadView? view;
        await using (ArchiveReader reader = await _openReader!(operation.Cancellation.Token))
            view = await reader.ReadThreadAsync(hit.Thread, operation.Cancellation.Token);
        if (!FilenameSearchMayReturn(operation)) return;
        int index = ResolveFilenameHitPostIndex(view, hit);
        if (index < 0)
        {
            Message(Texts.T("readerGroups.unavailableLocally"), true);
            return; // No first-post fallback, selection mutation, gallery, or path/URL reconstruction.
        }
        await LoadThreadAsync(hit.Thread, true, view, null, operation);
        if (FilenameSearchMayReturn(operation) && ReferenceEquals(_thread, view)) ShowPost(hit.Post, operation);
    }

    // One production validator, also exercised with real catalog snapshots by navigation tests.
    internal static int ResolveFilenameHitPostIndex(ArchivedThreadView? view, ArchiveFilenameHit? hit)
    {
        if (view == null || hit == null || view.Summary.Id != hit.Thread
            || view.Summary.Revision != hit.CatalogThreadRevision) return -1;
        int index = view.FindPostIndex(hit.Post);
        if (index < 0) return -1;
        CapturedPost post = view.GetPosts(index, 1).Single();
        // Deleted retained observations remain navigable, but never silently substitute a
        // different revision, attachment, source/board/thread, position, or deletion state.
        return post.IsDeleted == hit.PostIsDeleted
            && post.Attachments?.Any(attachment => attachment.Position == hit.Position
                && attachment.Variant == hit.Variant && attachment.OriginalFileName == hit.OriginalName
                && attachment.AttachmentRevisionKey == hit.AttachmentRevisionKey
                && attachment.IsDeleted == hit.AttachmentIsDeleted) == true ? index : -1;
    }

}
