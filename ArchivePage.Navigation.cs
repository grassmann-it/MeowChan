using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;

namespace MeowChan;

public partial class ArchivePage
{
    private ArchiveSearchSpec _query = new ArchiveSearchSpec();
    private sealed record InlineReplyState(string PostId, bool Expanded, bool Loaded, int? Count,
        int Offset, IReadOnlyList<ArchiveQuoteRow> Replies, string? Error);
    private sealed record ReaderHistoryEntry(ArchivedThreadView View, int Offset, ReaderAnchor? Anchor,
        IReadOnlyList<InlineReplyState> Replies);

    private InlineReplyState[] CaptureInlineReplyState() =>
        (Control<ListBox>("ArchivePosts").ItemsSource as IEnumerable<ArchivePostRow>)?
            .Where(row => row.RepliesExpanded || row.RepliesLoaded || row.HasRepliesError)
            .Select(row => new InlineReplyState(row.Id, row.RepliesExpanded, row.RepliesLoaded,
                row.BacklinkCount, row.RepliesOffset, Array.AsReadOnly(row.Replies.ToArray()), row.RepliesError))
            .ToArray() ?? Array.Empty<InlineReplyState>();

    private void RestoreInlineReplyState(IReadOnlyList<InlineReplyState> states)
    {
        if (Control<ListBox>("ArchivePosts").ItemsSource is not IEnumerable<ArchivePostRow> rows) return;
        Dictionary<string, InlineReplyState> byId = states.ToDictionary(state => state.PostId, StringComparer.Ordinal);
        foreach (ArchivePostRow row in rows)
        {
            if (!byId.TryGetValue(row.Id, out InlineReplyState? state)) continue;
            // Restore all loaded pages before expanding so materialization cannot
            // replace a 20+ reply origin with a freshly loaded first page.
            if (state.Loaded) row.SetReplies(state.Replies, state.Count ?? state.Replies.Count, state.Offset);
            else if (state.Count.HasValue) row.SetBacklinkCount(state.Count.Value);
            row.SetRepliesError(state.Error);
            row.RepliesExpanded = state.Expanded;
        }
    }
    private readonly LinkedList<ReaderHistoryEntry> _history = new();
    private ReaderAnchor? _backlinkOriginAnchor;
    private CancellationTokenSource? _referenceCancellation;
    private int _referenceRequest;
    private ArchivePostId? _backlinkPost;
    private int _referenceOffset;
    private int _referenceTotal;

    private void LocalizeSearch()
    {
        UpdateSuggestionHint();
        Control<TextBox>("ArchiveSearchText").PlaceholderText = Texts.T("archive.searchHint");
        Control<IdentitySuggestionBox>("ArchiveSourceFilter").PlaceholderText = Texts.T("archive.sourceFilter");
        Control<IdentitySuggestionBox>("ArchiveBoardFilter").PlaceholderText = Texts.T("archive.boardFilter");
        Control<Expander>("ArchiveFilters").Header = Texts.T("archive.filters");
        SetOptions("ArchiveStateFilter", "archive.allStates", "archive.sourceActive", "archive.sourceArchived", "archive.sourceMissing");
        SetOptions("ArchiveCaptureFilter", "archive.allCapture", "archive.captureEnabled", "archive.captureDisabled");
        SetOptions("ArchivePostsFilter", "archive.allContent", "archive.withPosts", "archive.withoutPosts");
        SetOptions("ArchiveWorkFilter", "gallery.allWork", "gallery.watching", "gallery.followup",
            "gallery.intervention", "gallery.completed", "gallery.stopped");
        SetOptions("ArchiveSort", "archive.latest", "archive.oldest", "archive.titleSort");
        LocalizeSearchEditors(); UpdateAppliedFilters(); UpdateSuggestionHint();
    }
    private void SetOptions(string name, params string[] keys)
    {
        ComboBox combo = Control<ComboBox>(name);
        int index = Math.Max(0, combo.SelectedIndex);
        combo.ItemsSource = keys.Select(key => Texts.T(key)).ToArray();
        combo.SelectedIndex = index;
    }
    private async void OnSearchClick(object? sender, RoutedEventArgs e) => await ApplySearchAsync();
    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await ApplySearchAsync();
    }
    internal async Task ApplySearchAsync()
    {
        if (_busy || _ended || !_available) return;
        long validation = ++_searchValidationGeneration;
        try
        {
            ArchiveSearchRequest request = ArchiveSearchRequest.Create(ReadSearchDraft());
            SearchEditorError(string.Empty);
            _showReader = false; UpdateLayoutMode();
            await LoadListAsync(NewListLoad(request));
        }
        catch (Exception exception)
        {
            SearchEditorError(exception.Message);
            RevealSearchValidationError(validation);
        }
    }
    private void CancelReferences()
    {
        ++_referenceRequest;
        _referenceCancellation?.Cancel();
        _backlinkPost = null;
        Control<ListBox>("ArchiveReferences").ItemsSource = null;
        Control<TextBlock>("ArchiveReferencesTitle").Text = string.Empty;
        Control<Button>("PreviousReferences").IsEnabled = false;
        Control<Button>("NextReferences").IsEnabled = false;
    }
    private async void OnQuoteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ArchiveQuoteRow row }) await NavigateQuoteAsync(row);
    }
    internal async Task NavigateQuoteAsync(ArchiveQuoteRow row)
    {
        if (_ended || _busy || !_available || _openReader == null) return;
        ReaderAnchor? originAnchor = row.Backlink && Control<TabControl>("ArchiveContentTabs").SelectedIndex == 2
            ? _backlinkOriginAnchor ?? CaptureReaderAnchor() : CaptureReaderAnchor();
        CancelReferences();
        int request = _referenceRequest;
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _referenceCancellation = loading;
        ClearOperationError("reference");
        try
        {
            await using ArchiveReader reader = await _openReader(loading.Token);
            ArchiveQuote quote = row.Backlink ? new ArchiveQuote(row.Quote.Origin, row.Quote.Origin, row.Label, null) : row.Quote;
            ArchiveQuoteResolution resolved = await reader.ResolveQuoteAsync(quote, loading.Token);
            if (_ended || request != _referenceRequest) return;
            if (resolved.State != ArchiveQuoteState.Captured || resolved.View == null || resolved.PostIndex == null)
            {
                Message(Texts.T("archive.quote" + resolved.State) + (row.Quote.Url == null ? "" : "\n" + row.Quote.Url));
                return;
            }
            if (_thread != null)
            {
                // Keep a bounded immutable navigation history, independent of later captures.
                if (_history.Count == 32) _history.RemoveFirst();
                _history.AddLast(new ReaderHistoryEntry(_thread, _postOffset, originAnchor, CaptureInlineReplyState()));
            }
            ShowCapturedView(resolved.View, resolved.PostIndex.Value / PageSize * PageSize, resolved.Post!.PostId);
            await FocusPostAsync(resolved.Post.PostId);
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _referenceRequest) OperationMessage("reference", exception.Message, true); }
        finally { if (ReferenceEquals(_referenceCancellation, loading)) _referenceCancellation = null; }
    }
    private void ShowCapturedView(ArchivedThreadView view, int offset, string? postId = null)
    {
        ++_contentRequest;
        BeginContentFeedback();
        _contentCancellation?.Cancel();
        CancelMediaLoad();
        ClosePreview();
        _selectedThread = view.Summary.Id;
        _thread = view;
        ReaderSnapshotLoaded();
        _postOffset = offset;
        _assets = Array.Empty<ArchiveAssetRow>();
        _showReader = true;
        RenderThread();
        UpdateLayoutMode();
        Control<TabControl>("ArchiveContentTabs").SelectedIndex = 0;
        ListBox posts = Control<ListBox>("ArchivePosts");
        if (posts.ItemsSource is IEnumerable<ArchivePostRow> rows && postId != null)
        {
            posts.SelectedItem = rows.FirstOrDefault(post => post.Id == postId);
            posts.ScrollIntoView(posts.SelectedItem!);
        }
        Message(string.Empty);
        MediaLoadTask = LoadMediaAsync(view);
    }
    private async void OnHistoryBack(object? sender, RoutedEventArgs e)
    {
        if (_busy || _ended || _history.Last == null) return;
        ReaderHistoryEntry previous = _history.Last.Value;
        _history.RemoveLast();
        CancelReferences();
        ShowCapturedView(previous.View, previous.Offset);
        RestoreInlineReplyState(previous.Replies);
        if (previous.Anchor != null) await RestoreReaderAnchorAsync(previous.Anchor);
        ReaderPositionChanged(deliberate: true);
    }
    private async void OnBacklinksClick(object? sender, RoutedEventArgs e)
    {
        if (_thread == null || sender is not Button { DataContext: ArchivePostRow row }) return;
        _backlinkOriginAnchor = CaptureReaderAnchor();
        CancelReferences();
        _backlinkPost = new ArchivePostId(_thread.Summary.Id, row.Id);
        _referenceOffset = 0;
        await LoadBacklinksAsync();
    }
    private async Task LoadBacklinksAsync()
    {
        if (_busy || _ended || !_available || _openReader == null || _backlinkPost == null) return;
        int request = ++_referenceRequest;
        _referenceCancellation?.Cancel();
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _referenceCancellation = loading;
        ClearOperationError("reference");
        ArchivePostId post = _backlinkPost;
        Control<TabControl>("ArchiveContentTabs").SelectedIndex = 2;
        Control<TextBlock>("ArchiveReferencesTitle").Text = Texts.T("archive.loading");
        try
        {
            await using ArchiveReader reader = await _openReader(loading.Token);
            ArchiveQuotePage result = await reader.GetBacklinksAsync(post, _referenceOffset, PageSize, loading.Token);
            if (_ended || request != _referenceRequest) return;
            _referenceTotal = result.TotalCount;
            Control<ListBox>("ArchiveReferences").ItemsSource = result.Items.Select(quote => new ArchiveQuoteRow(quote, true)).ToArray();
            Control<TextBlock>("ArchiveReferencesTitle").Text = Texts.T(result.TotalCount == 0 ? "archive.noBacklinks" : "archive.backlinks", post.PostId);
            Control<TextBlock>("ArchiveReferencesPage").Text = PageLabel(_referenceOffset, _referenceTotal);
            Control<Button>("PreviousReferences").IsEnabled = _referenceOffset > 0;
            Control<Button>("NextReferences").IsEnabled = _referenceOffset + PageSize < _referenceTotal;
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _referenceRequest) OperationMessage("reference", exception.Message, true); }
        finally { if (ReferenceEquals(_referenceCancellation, loading)) _referenceCancellation = null; }
    }
    private async void OnPreviousReferences(object? sender, RoutedEventArgs e)
    { _referenceOffset = Math.Max(0, _referenceOffset - PageSize); await LoadBacklinksAsync(); }
    private async void OnNextReferences(object? sender, RoutedEventArgs e)
    { if (_referenceOffset + PageSize >= _referenceTotal) return; _referenceOffset += PageSize; await LoadBacklinksAsync(); }
}
