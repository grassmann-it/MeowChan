using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Gallery;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class ArchivePage
{
    private ArchiveThreadRow? _contextThread;
    private int _gallerySelectionRequest;
    private CancellationTokenSource? _gallerySelectionCancellation;
    // Snapshotted synchronously by the shell delegates, then retained through native cleanup.
    internal Func<bool>? GallerySelectionGuard { get; private set; }
    internal Func<bool> CaptureGalleryHandoffGuard()
    {
        int request = _gallerySelectionRequest, content = _contentRequest;
        var threadOpen = OpenGallery; var mediaOpen = OpenMediaGallery; var resultsOpen = OpenResultsGallery;
        return () => _available && !_ended && !_lifetime.IsCancellationRequested && !_busy && !IsReaderDialogOpen
            && request == _gallerySelectionRequest && content == _contentRequest
            && ReferenceEquals(threadOpen, OpenGallery) && ReferenceEquals(mediaOpen, OpenMediaGallery)
            && ReferenceEquals(resultsOpen, OpenResultsGallery);
    }
    internal void CancelPendingGalleryOpen()
    {
        // Also the existing shell workspace-leave/forced gallery-close hook, including
        // a detached reader whose hidden ArchivePage has no new visibility transition.
        InvalidateFilenameSearch();
        ++_gallerySelectionRequest;
        _gallerySelectionCancellation?.Cancel();
    }
    internal Action? GalleryReturn { get; set; }
    // State, not the button: whether the return is on offer is _galleryReturnOffered, so a layout that moves the button away keeps the gesture.
    private bool _galleryReturnOffered;
    internal bool CanReturnToGallery => GalleryReturn != null && _galleryReturnOffered && !_ended;
    internal void ReturnToGalleryByShortcut() { if (CanReturnToGallery) GalleryReturn?.Invoke(); }
    internal void ShowGalleryReturn(bool visible) { _galleryReturnOffered = visible; ArchiveGalleryReturn.IsVisible = visible; }
    private void OnGalleryReturnClick(object? sender, RoutedEventArgs e) => GalleryReturn?.Invoke();
    /// <summary>Back to the Medien page after its "show in thread"; the shell owns the return point and shows the button.</summary>
    internal Action? MediaReturn { get; set; }
    // F5: the same command names the page it goes back to ("Back to Tagging" after the Tagging page's jump); null is the catalog's "Back to media".
    private string? _mediaReturnLabelKey;
    internal void ShowMediaReturn(bool visible, string? labelKey = null)
    {
        ArchiveMediaReturn.IsVisible = visible;
        if (_mediaReturnLabelKey == labelKey) return;
        _mediaReturnLabelKey = labelKey;
        ApplyMediaReturnLabel();
    }
    private void ApplyMediaReturnLabel()
    {
        if (_mediaReturnLabelKey is { } key)
            MeowChan.Desktop.Commands.CommandBindings.ApplyLabeled(ArchiveMediaReturn, "media.return", MeowChan.Desktop.Localization.Texts.T(key));
        else MeowChan.Desktop.Commands.CommandBindings.Apply(ArchiveMediaReturn, "media.return", iconWithLabel: true);
    }
    internal string MediaReturnLabel => _mediaReturnLabelKey is { } key
        ? MeowChan.Desktop.Localization.Texts.T(key) : MeowChan.Desktop.Commands.CommandCatalog.Label("media.return");
    private void OnMediaReturnClick(object? sender, RoutedEventArgs e) => MediaReturn?.Invoke();
    private static GalleryEntry[] BuildVisibleGalleryEntries(ArchivedThreadView thread,
        ArchiveAssetRow[] assets, ArchiveAssetRow[] visible)
    {
        ResourceId threadId = thread.Summary.Id;
        GalleryThreadResult associated = GallerySelection.Build(thread,
            assets.Select(asset => asset.Manifest).DistinctBy(manifest => manifest.Directory), true, includeOtherMedia: true);
        // Prefer a post-associated entry; otherwise keep the selection's own unassigned entry, which carries
        // the loose-file (local tag) eligibility a hand-made entry never has.
        Dictionary<GalleryAssetKey, GalleryEntry> postByAsset = associated.Entries
            .GroupBy(entry => entry.Key)
            .ToDictionary(group => group.Key, group => group.FirstOrDefault(entry => entry.Post != null) ?? group.First());
        return visible.Select(asset =>
        {
            GalleryAssetKey identity = new(threadId, asset.Manifest.Directory, asset.Asset.AssetId);
            return postByAsset.TryGetValue(identity, out GalleryEntry? match)
                ? match : new GalleryEntry(identity, asset.Manifest, asset.Asset, null, null, !asset.Asset.IsCurrent, true)
                { ThreadUrl = thread.Summary.Url };
        }).ToArray();
    }

    // The shell already snapshots this guard for thread-gallery handoff and rechecks it
    // after native-media cleanup. Catalog readability alone never authorizes coverage.
    private async Task OpenThreadGalleryAsync(ResourceId id, GalleryEntryKey? start, Control? origin)
    {
        if (!_available || _ended || _lifetime.IsCancellationRequested || _busy || IsReaderDialogOpen || OpenGallery == null) return;
        if (_readerMediaActivationCancellation != null) return;
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _readerMediaActivationCancellation = cancellation;
        var open = OpenGallery;
        int content = _contentRequest, request = _gallerySelectionRequest;
        Func<bool> current = () => _available && !_ended && !cancellation.IsCancellationRequested
            && !_busy && !IsReaderDialogOpen && content == _contentRequest && request == _gallerySelectionRequest
            && ReferenceEquals(open, OpenGallery);
        ReaderMediaActivationGuard = current;
        try { await open(id, start, origin); }
        finally
        {
            if (ReferenceEquals(ReaderMediaActivationGuard, current)) ReaderMediaActivationGuard = null;
            if (ReferenceEquals(_readerMediaActivationCancellation, cancellation)) _readerMediaActivationCancellation = null;
        }
    }

    private async Task OpenAssetGalleryAsync(ArchiveAssetRow row, Control? origin)
    {
        if (!_available || _ended || _lifetime.IsCancellationRequested || _busy || IsReaderDialogOpen
            || _selectedThread == null || OpenMediaGallery == null || _thread == null) return;
        CancelPendingGalleryOpen();
        int request = _gallerySelectionRequest;
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _gallerySelectionCancellation = loading;
        CancellationToken token = loading.Token;
        var open = OpenMediaGallery;
        int content = _contentRequest;
        ResourceId threadId = _selectedThread;
        ArchivedThreadView thread = _thread;
        ArchiveAssetRow[] visible = VisibleAssets().ToArray();
        ArchiveAssetRow[] assets = _assets.ToArray();
        GalleryAssetKey key = new(threadId, row.Manifest.Directory, row.Asset.AssetId);
        // Freeze the visible sequence on the UI thread; matching a large archive runs off it.
        try
        {
            GalleryEntry[] entries = await Task.Run(() => BuildVisibleGalleryEntries(thread, assets, visible), token);
            bool Current() => _available && !_ended && !_busy && !IsReaderDialogOpen
                && !token.IsCancellationRequested && request == _gallerySelectionRequest && content == _contentRequest
                && _selectedThread == threadId && ReferenceEquals(_thread, thread) && _showReader
                && ReferenceEquals(open, OpenMediaGallery);
            if (!Current()) return;
            GalleryEntry? selected = entries.FirstOrDefault(entry => entry.Key == key);
            if (selected != null)
            {
                Func<bool> current = Current;
                GallerySelectionGuard = current;
                try { await open(entries, selected.EntryKey, origin); }
                finally { if (ReferenceEquals(GallerySelectionGuard, current)) GallerySelectionGuard = null; }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { if (ReferenceEquals(_gallerySelectionCancellation, loading)) _gallerySelectionCancellation = null; }
    }

    internal Action CaptureGalleryOrigin(bool restoreReaderFocus = true)
    {
        ArchivedThreadView? thread = _thread;
        ResourceId? selected = _selectedThread;
        ArchiveAssetRow[] assets = _assets.ToArray();
        bool reader = _showReader;
        int offset = _postOffset;
        int tab = ArchiveContentTabs.SelectedIndex;
        ReaderAnchor? readerAnchor = reader && tab == 0 ? CaptureReaderAnchor() : null;
        ArchiveSearchSpec query = _query;
        ArchiveSearchRequest? appliedSearch = _appliedSearch;
        var appliedWork = _appliedWork;
        Action restoreDraft = CaptureSearchDraft();
        int listOffset = _listOffset, listTotal = _listTotal, workFilter = _workFilter;
        int mediaSort = _mediaSort;
        bool allVersions = _allVersions;
        var listRows = (ArchiveThreads.ItemsSource as IEnumerable<ArchiveThreadRow>)?.ToArray() ?? Array.Empty<ArchiveThreadRow>();
        ResourceId[] listSelection = FrozenArchiveSelection();
        ResourceId? listLead = _archiveLeadId;
        ArchiveAssetRow[] mediaSelection = SelectedMediaRows().ToArray();
        MediaPosition? mediaPosition = CaptureMediaPosition();
        string? postSelection = (ArchivePosts.SelectedItem as ArchivePostRow)?.Id;
        InlineReplyState[] inlineReplies = CaptureInlineReplyState();
        var history = _history.ToArray();
        ReaderAnchor? backlinkOrigin = _backlinkOriginAnchor;
        ArchivePostId? backlinkPost = _backlinkPost;
        int referenceOffset = _referenceOffset;
        int referenceTotal = _referenceTotal;
        ArchiveQuoteRow[] references = (ArchiveReferences.ItemsSource as IEnumerable<ArchiveQuoteRow>)?.ToArray()
            ?? Array.Empty<ArchiveQuoteRow>();
        object? referenceSelection = ArchiveReferences.SelectedItem;
        string? referenceTitle = ArchiveReferencesTitle.Text;
        string? referencePage = ArchiveReferencesPage.Text;
        int? referencesCount = _referencesCount;
        var scroll = this.GetVisualDescendants().OfType<ScrollViewer>()
            .Select(view => (View: view, Offset: view.Offset)).ToArray();
        return () =>
        {
            InvalidateFilenameSearch();
            InvalidatePendingReaderGroupRead();
            ++_contentRequest;
            _contentCancellation?.Cancel();
            CancelReferences();
            CancelMediaLoad();
            _busy = false;
            _query = query; _appliedSearch = appliedSearch; _appliedWork = appliedWork; UpdateAppliedFilters(); _listOffset = listOffset; _listTotal = listTotal; _workFilter = workFilter;
            _mediaSort = mediaSort; _allVersions = allVersions;
            restoreDraft();
            _history.Clear();
            foreach (ReaderHistoryEntry position in history) _history.AddLast(position);
            _backlinkOriginAnchor = backlinkOrigin;
            _backlinkPost = backlinkPost;
            _referenceOffset = referenceOffset;
            _referenceTotal = referenceTotal;
            ArchiveReferences.ItemsSource = references;
            ArchiveReferences.SelectedItem = referenceSelection;
            ArchiveReferencesTitle.Text = referenceTitle;
            ArchiveReferencesPage.Text = referencePage;
            ShowReferencesTab(referencesCount, present: backlinkPost != null && referencesCount == null);
            PreviousReferences.IsEnabled = backlinkPost != null && referenceOffset > 0;
            NextReferences.IsEnabled = backlinkPost != null && referenceOffset + PageSize < referenceTotal;
            CloseThreadFind();
            _thread = thread; _selectedThread = selected; _assets = assets;
            ReaderSnapshotLoaded();
            _postOffset = offset; _showReader = reader;
            _mediaReady.TrySetResult(assets);
            _updatingList = true;
            try
            {
                ArchiveThreads.ItemsSource = listRows;
                ArchiveThreadTable.ItemsSource = listRows;
                _archiveSelectionIds.Clear();
                foreach (ResourceId id in listSelection) _archiveSelectionIds.Add(id);
                _archiveLeadId = listLead;
                RestoreArchiveSelection(listRows);
            }
            finally { _updatingList = false; }
            RenderThread(); UpdateLayoutMode();
            RestoreInlineReplyState(inlineReplies);
            ArchiveContentTabs.SelectedIndex = tab;
            SetMediaSelection(mediaSelection);
            if (ArchivePosts.ItemsSource is IEnumerable<ArchivePostRow> posts)
                ArchivePosts.SelectedItem = posts.FirstOrDefault(post => post.Id == postSelection);
            foreach (var item in scroll) item.View.Offset = item.Offset;
            RestoreMediaPosition(mediaPosition);
            if (readerAnchor != null) _ = RestoreReaderAnchorAsync(readerAnchor, restoreReaderFocus);
        };
    }

    private ArchiveThreadRow? EventThread(object? sender, object? source)
    {
        if (sender is MenuItem { DataContext: ArchiveThreadRow menuRow }) return menuRow;
        if (sender is MenuItem && _contextThread != null) return _contextThread;
        if (source is Control control)
        {
            if (control.DataContext is ArchiveThreadRow direct) return direct;
            return control.FindAncestorOfType<ListBoxItem>()?.DataContext as ArchiveThreadRow;
        }
        return null;
    }

    private async void OnThreadDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsFilenameSearchOpen) return;
        if (e.Source is Avalonia.Visual tapped &&
            (tapped is Button or TextBox || tapped.FindAncestorOfType<Button>() != null)) return;
        ArchiveThreadRow? row = EventThread(sender, e.Source);
        if (row == null || OpenGallery == null || FrozenArchiveSelection().Length != 1) return;
        e.Handled = true;
        Control? origin = e.Source as Control;
        await OpenActions.RunAsync(() => OpenReaderOnThreadAsync(row.Summary.Id, () => OpenReaderRequested?.Invoke(origin)), "archive.list.doubleClick");
    }

    private void OnThreadTableSelected(object? sender, SelectionChangedEventArgs e) => ArchiveSelectionChanged(sender);

    private async void OnThreadTableDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsFilenameSearchOpen) return;
        if (e.Source is Avalonia.Visual tapped &&
            (tapped is Button or TextBox || tapped.FindAncestorOfType<Button>() != null)) return;
        DataGridRow? target = (e.Source as Avalonia.Visual)?.FindAncestorOfType<DataGridRow>();
        if (target?.DataContext is not ArchiveThreadRow row || OpenGallery == null
            || FrozenArchiveSelection().Length != 1) return;
        e.Handled = true;
        await OpenActions.RunAsync(() => OpenReaderOnThreadAsync(row.Summary.Id, () => OpenReaderRequested?.Invoke(target)), "archive.table.doubleClick");
    }

    private async void OnThreadGalleryMenu(object? sender, RoutedEventArgs e)
    {
        if (!_available || _ended || _lifetime.IsCancellationRequested || _busy || IsFilenameSearchOpen) return;
        if (EventThread(sender, e.Source) is ArchiveThreadRow row && OpenGallery != null)
            await OpenActions.RunAsync(() => OpenThreadGalleryAsync(row.Summary.Id, null, ThreadOrigin(row)), "archive.menu.gallery");
    }

    private Control ThreadOrigin(ArchiveThreadRow row) =>
        this.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => (control is DataGridRow or ListBoxItem)
            && control.DataContext is ArchiveThreadRow found && found.Summary.Id == row.Summary.Id)
        ?? ArchiveThreadTable;

    private async void OnCurrentGalleryClick(object? sender, RoutedEventArgs e)
    {
        if (!_available || _ended || _lifetime.IsCancellationRequested || _busy || IsFilenameSearchOpen) return;
        if (_selectedThread is { } selected && OpenGallery != null)
            await OpenActions.RunAsync(() => OpenThreadGalleryAsync(selected, null, sender as Control), "archive.current.gallery");
    }

    private async void OnResultsGalleryClick(object? sender, RoutedEventArgs e)
    {
        await OpenActions.RunAsync(() => OpenResultsGalleryAsync(sender as Control), "archive.results.gallery");
    }

    private async Task OpenResultsGalleryAsync(Control? origin)
    {
        if (!_available || _ended || _lifetime.IsCancellationRequested || _busy || IsReaderDialogOpen || OpenResultsGallery == null) return;
        CancelPendingGalleryOpen();
        int request = _gallerySelectionRequest, content = _contentRequest;
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _gallerySelectionCancellation = loading;
        var open = OpenResultsGallery;
        ArchiveSearchRequest query = _appliedSearch ?? ArchiveSearchRequest.Create(_query);
        var applied = _appliedSearch;
        var draft = _query;
        int workFilter = _workFilter;
        Func<bool> current = () => _available && !_ended && !_busy && !IsReaderDialogOpen
            && !loading.IsCancellationRequested && request == _gallerySelectionRequest && content == _contentRequest
            && ReferenceEquals(applied, _appliedSearch) && Equals(draft, _query) && workFilter == _workFilter
            && ReferenceEquals(open, OpenResultsGallery);
        GallerySelectionGuard = current;
        try { await open(query, workFilter, origin); }
        finally
        {
            if (ReferenceEquals(GallerySelectionGuard, current)) GallerySelectionGuard = null;
            if (ReferenceEquals(_gallerySelectionCancellation, loading)) _gallerySelectionCancellation = null;
        }
    }

    private async void OnMediaDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (!_available || _ended || _lifetime.IsCancellationRequested || _busy || IsReaderDialogOpen) return;
        if (e.Source is Visual visual && visual.GetSelfAndVisualAncestors().Any(node => node is Button or TextBox or ComboBox or Slider)) return;
        ArchiveAssetRow? row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>()
            .Select(control => control.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault();
        if (row == null) return;
        e.Handled = true;
        await OpenActions.RunAsync(() => OpenAssetGalleryAsync(row, e.Source as Control), "archive.media.open");
    }

    private async void OnMediaKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !_available || _ended || _lifetime.IsCancellationRequested || _busy || IsReaderDialogOpen || sender is not Control list) return;
        if (TryRouteFavoriteKey(list, e) || TryRouteArchiveMediaShortcut(list, e)) return;
        if (!MeowChan.Desktop.Commands.CommandBindings.Matches(e, "media.open") || !MainWindow.IsListCommandSource(e.Source, list)) return;
        ArchiveAssetRow? row = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Control>()
            .Select(control => control.DataContext).OfType<ArchiveAssetRow>().FirstOrDefault() ?? SelectedMediaRow();
        if (row == null) return;
        e.Handled = true;
        await OpenActions.RunAsync(() => OpenAssetGalleryAsync(row, e.Source as Control), "archive.media.open");
    }

    /// <summary>
    /// "Large reader" (reader.expand) of the reader's toolbar acts on the thread the reader shows, whatever the list shows or selects: a thread
    /// opened by a jump (Media, Tagging, a quote) may be on no page of the list, and a list selection of several threads still leaves the reader
    /// with its one thread (16.11.2026: the gate used to demand exactly one selected list row).
    /// </summary>
    internal bool CanExpandReader => CanRead && !_busy && !_ended && !IsFilenameSearchOpen && _selectedThread != null;

    private async void OnReadPostsClick(object? sender, RoutedEventArgs e)
    {
        if (!CanExpandReader || _selectedThread is not { } selectedThread) return;
        Control? origin = sender as Control;
        await OpenActions.RunAsync(() => OpenReaderOnThreadAsync(selectedThread, () => OpenReaderRequested?.Invoke(origin)), "archive.readPosts");
        // Enlarging is no reading: it writes no reading position (the entry anchor already kept the place), and a thread without posts opens on its media.
        if (_thread != null && DownloadReaderGate.OpensOnMedia(_thread.PostCount, _thread.Summary.LastCapturedAt)) ArchiveContentTabs.SelectedItem = ArchiveMediaTab;
        else ArchiveContentTabs.SelectedIndex = 0;
    }

    private void OnVersionScopeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_localizingMediaChoices || (!IsInitialized && !_largeReader)) return;
        _allVersions = ArchiveVersionScope.SelectedIndex == 1;
        RenderMedia();
    }

    private void OnMediaSortChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_localizingMediaChoices || (!IsInitialized && !_largeReader)) return;
        _mediaSort = Math.Max(0, ArchiveMediaSort.SelectedIndex);
        RenderMedia();
    }

    internal void ShowPost(string postId) => ShowPost(postId, null);

    // An explicit jump (find, filename search, gallery): a post the view hides is revealed first.
    private void ShowPost(string postId, FilenameSearchOwner? filenameNavigation) => ShowPost(postId, filenameNavigation, reveal: true);

    // scroll: false when the caller aligns the post itself (FocusPostAsync); a minimal ScrollIntoView here would be a competing write.
    // background: the Media tab stays selected and the media list is left alone (it does not depend on the page of posts); the selection is still made.
    private void ShowPost(string postId, FilenameSearchOwner? filenameNavigation, bool reveal, bool scroll = true, bool background = false)
    {
        if (!ReferenceEquals(_filenameSearchOwner, filenameNavigation)) InvalidateFilenameSearch();
        if (_thread == null) return;
        if (_thread.FindPostIndex(postId) < 0) return;
        if (reveal) RevealPostNow(postId);
        // Pages are cut from the shown order; a view still being prepared puts its page at the post when it is ready.
        if (Projection is not { } projection) { _projectionPagePost = postId; return; }
        int index = projection.NearestPosition(postId);
        if (index < 0) return;
        postId = projection[index].Post.PostId;
        int offset = index / ReaderPageSize * ReaderPageSize;
        // Moving within a page keeps realized controls, pending previews and their geometry.
        if (offset != _postOffset || ArchivePosts.ItemsSource is not IEnumerable<ArchivePostRow> current
            || !current.Any(post => post.Id == postId))
        {
            _postOffset = offset;
            RenderThread(refreshMedia: !background);
        }
        if (!background)
        {
            _postsAlignOnShow = null; // an explicit jump aligns the list itself
            ArchiveContentTabs.SelectedIndex = 0;
        }
        ListBox list = ArchivePosts;
        if (list.ItemsSource is System.Collections.Generic.IEnumerable<ArchivePostRow> posts)
        {
            list.SelectedItem = posts.FirstOrDefault(post => post.Id == postId);
            if (scroll && list.SelectedItem != null) list.ScrollIntoView(list.SelectedItem);
        }
    }
}
