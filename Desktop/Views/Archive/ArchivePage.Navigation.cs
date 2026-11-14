using MeowChan.Desktop.WorkViews;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
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
        (ArchivePosts.ItemsSource as IEnumerable<ArchivePostRow>)?
            .Where(row => row.RepliesExpanded || row.RepliesLoaded || row.HasRepliesError)
            .Select(row => new InlineReplyState(row.Id, row.RepliesExpanded, row.RepliesLoaded,
                row.BacklinkCount, row.RepliesOffset, Array.AsReadOnly(row.Replies.ToArray()), row.RepliesError))
            .ToArray() ?? Array.Empty<InlineReplyState>();

    private void RestoreInlineReplyState(IReadOnlyList<InlineReplyState> states)
    {
        if (ArchivePosts.ItemsSource is not IEnumerable<ArchivePostRow> rows) return;
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
        ArchiveSearchText.PlaceholderText = Texts.T("archive.searchHint");
        // Short placeholders; the visible label above each field names what it filters.
        ArchiveSourceFilter.PlaceholderText = Texts.T("archive.filter.all");
        ArchiveBoardFilter.PlaceholderText = Texts.T("archive.filter.all");
        foreach ((string field, string key) in new[] {
            ("ArchiveSourceFilter", "archive.filter.source"), ("ArchiveBoardFilter", "archive.filter.board"),
            ("ArchiveStateFilter", "archive.filter.state"), ("ArchiveTagChoice", "tags.filter.label"),
            ("ArchiveTagMode", "tags.filter.matchLabel"), ("ArchiveCaptureFilter", "archive.filter.capture"),
            ("ArchivePostsFilter", "archive.filter.content"), ("ArchiveWorkFilter", "archive.filter.work"),
            ("ArchiveSort", "archive.filter.sort") })
        {
            Control<TextBlock>(field + "Label").Text = Texts.T(key);
            AutomationProperties.SetName(Control<Control>(field), Texts.T(key));
        }
        ArchiveFilters.Header = Texts.T("archive.filters");
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
        _localizingSearchOptions = true;
        try
        {
            combo.ItemsSource = keys.Select(key => Texts.T(key)).ToArray();
            combo.SelectedIndex = index;
        }
        finally { _localizingSearchOptions = false; }
    }
    private async void OnSearchClick(object? sender, RoutedEventArgs e)
    {
        // AP2: in the File name and SHA-256 scopes the one Search button opens that scope's results.
        if (ArchiveScope != MeowChan.Desktop.Controls.SearchScope.Text) { RunArchiveScope(ArchiveScopedBox); return; }
        await ApplySearchAsync();
    }
    private async void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        // Enter in a source or board field with text adds it; the user sees the chip before searching.
        // Enter on an empty field or in the text field searches.
        if (sender is IdentitySuggestionBox editor && !string.IsNullOrWhiteSpace(editor.Text))
        { await AddIdentityAsync(ReferenceEquals(editor, ArchiveBoardFilter)); return; }
        await ApplySearchAsync();
    }
    internal async Task ApplySearchAsync()
    {
        if (IsFilenameSearchOpen || _busy || _ended || !CanRead) return;
        long validation = ++_searchValidationGeneration;
        // Leftover state, source or board input is added first, exactly like Enter in its field. Only an invalid
        // value stops the search; its message stays under that field alone while the field can be shown.
        AddChosenState();
        foreach (IdentitySuggestionBox editor in new[] { ArchiveSourceFilter, ArchiveBoardFilter })
        {
            if (await AddIdentityAsync(ReferenceEquals(editor, ArchiveBoardFilter))) continue;
            if (validation != _searchValidationGeneration || !DataValidationErrors.GetHasErrors(editor)) return;
            _invalidSearchEditor = editor;
            ShowSearchError(DataValidationErrors.GetErrors(editor)?.OfType<object>().FirstOrDefault()?.ToString() ?? string.Empty, validation);
            return;
        }
        if (validation != _searchValidationGeneration || _busy || _ended || !CanRead) return;
        try
        {
            ArchiveSearchRequest request = CreateSearchRequest(ReadSearchDraft());
            SearchEditorError(string.Empty);
            _showReader = false; UpdateLayoutMode();
            await LoadListAsync(NewListLoad(request));
        }
        catch (Exception exception) { ShowSearchError(exception.Message, validation); }
    }
    // A field error stands once, directly under its field. The line above the toolbar carries it only when
    // the field cannot show it (filter surface closed, field hidden) and for errors without a field.
    private void ShowSearchError(string message, long validation)
    {
        RevealSearchValidationError(validation); // Opens the filter surface for a field error.
        Control? field = _invalidSearchEditor == null ? null : ValidationFocusTarget(_invalidSearchEditor);
        bool underField = field != null && IsFilterSurfaceOpen && field.IsEffectivelyVisible
            && (DataValidationErrors.GetHasErrors(field) || DataValidationErrors.GetHasErrors(_invalidSearchEditor!));
        SearchEditorError(underField ? string.Empty : message);
    }
    private void CancelReferences()
    {
        ++_referenceRequest;
        _referenceCancellation?.Cancel();
        _backlinkPost = null;
        ArchiveReferences.ItemsSource = null;
        ArchiveReferencesTitle.Text = string.Empty;
        ShowReferencesTab(null);
        PreviousReferences.IsEnabled = false;
        NextReferences.IsEnabled = false;
    }
    private async void OnQuoteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: ArchiveQuoteRow row }) await NavigateQuoteAsync(row);
    }
    internal async Task NavigateQuoteAsync(ArchiveQuoteRow row)
    {
        if (_ended || IsFilenameSearchOpen || _busy || !CanRead || _openReader == null) return;
        ReaderAnchor? originAnchor = row.Backlink && ArchiveContentTabs.SelectedIndex == 2
            ? _backlinkOriginAnchor ?? CaptureReaderAnchor() : CaptureReaderAnchor();
        CancelReferences();
        CloseQuotePreview();
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
            if (!_ended && request == _referenceRequest) _ = FlashPostAsync(resolved.Post.PostId);
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _referenceRequest) OperationMessage("reference", exception.Message, true); }
        finally { if (ReferenceEquals(_referenceCancellation, loading)) _referenceCancellation = null; }
    }
    private void ShowCapturedView(ArchivedThreadView view, int offset, string? postId = null)
    {
        InvalidateFilenameSearch();
        if (_unreadMark?.Thread != view.Summary.Id) _unreadMark = null;
        ++_contentRequest;
        BeginContentFeedback();
        _contentCancellation?.Cancel();
        CancelMediaLoad();
        ClosePreview();
        _selectedThread = view.Summary.Id;
        CloseThreadFind();
        _thread = view;
        ReaderSnapshotLoaded();
        _postOffset = offset;
        // With a post, the page is the one that holds it in the shown order (the offset is the thread's own page).
        _projectionPagePost = postId;
        _assets = Array.Empty<ArchiveAssetRow>();
        _showReader = true;
        RenderThread();
        UpdateLayoutMode();
        ArchiveContentTabs.SelectedIndex = 0;
        ListBox posts = ArchivePosts;
        if (posts.ItemsSource is IEnumerable<ArchivePostRow> rows && postId != null)
        {
            posts.SelectedItem = rows.FirstOrDefault(post => post.Id == postId);
            posts.ScrollIntoView(posts.SelectedItem!);
        }
        Message(string.Empty);
        MediaLoadTask = LoadMediaAsync(view);
    }
    // The large reader names where Close goes ("Back to Archive", "Back to Downloads"); without a known target it stays "Close".
    // Presentation only: the command, Escape and the way back are those of reader.close.
    private string? _readerCloseTargetKey;

    internal string ReaderCloseLabel => _readerCloseTargetKey is { } key ? Texts.T(key) : CommandCatalog.Label("reader.close");

    internal void SetReaderCloseTarget(string? textKey)
    {
        if (_readerCloseTargetKey == textKey) return;
        _readerCloseTargetKey = textKey;
        ApplyReaderCloseLabel();
    }

    private void ApplyReaderCloseLabel()
    {
        if (_readerCloseTargetKey is { } key) CommandBindings.ApplyLabeled(ArchiveCloseReader, "reader.close", Texts.T(key));
        else CommandBindings.Apply(ArchiveCloseReader, "reader.close", iconWithLabel: true);
    }

    internal bool CanGoHistoryBack => ArchiveHistoryBack.IsVisible && ArchiveHistoryBack.IsEnabled && !_busy && !_ended
        && !IsFilenameSearchOpen && _history.Last != null;
    internal void GoHistoryBackByShortcut() { if (CanGoHistoryBack) OnHistoryBack(null, new RoutedEventArgs()); }
    private async void OnHistoryBack(object? sender, RoutedEventArgs e)
    {
        if (IsFilenameSearchOpen) return;
        if (_busy || _ended || _history.Last == null) return;
        ReaderHistoryEntry previous = _history.Last.Value;
        _history.RemoveLast();
        CancelReferences();
        ShowCapturedView(previous.View, previous.Offset);
        RestoreInlineReplyState(previous.Replies);
        if (previous.Anchor != null) await RestoreReaderAnchorAsync(previous.Anchor);
        ReaderPositionChanged(deliberate: true);
    }
    /// <summary>
    /// The References tab exists only while captured replies are listed. A known count above zero shows it as "References (n)";
    /// null hides it (or, with present, keeps it shown while the list loads); zero hides it.
    /// </summary>
    private void ShowReferencesTab(int? total, bool present = false)
    {
        _referencesCount = total;
        bool show = present || total is > 0;
        if (!show && ArchiveContentTabs.SelectedItem == ArchiveReferencesTab) ArchiveContentTabs.SelectedIndex = 0;
        WorkViewGate.Apply(ArchiveReferencesTab, ArchiveWorkView, show); // data access: shown in every view whenever it has replies
        ArchiveReferencesTab.Header = ReferencesTabHeader();
    }
    private int? _referencesCount;
    private string ReferencesTabHeader() => _referencesCount is > 0 and var count
        ? Texts.T("archive.referencesCount", count.ToString(Texts.Culture)) : Texts.T("archive.references");
    private async Task LoadBacklinksAsync()
    {
        if (_busy || _ended || !CanRead || _openReader == null || _backlinkPost == null) return;
        int request = ++_referenceRequest;
        _referenceCancellation?.Cancel();
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _referenceCancellation = loading;
        ClearOperationError("reference");
        ArchivePostId post = _backlinkPost;
        ShowReferencesTab(null, present: true); // present while loading, so the selection below is a visible tab
        ArchiveContentTabs.SelectedIndex = 2;
        ArchiveReferencesTitle.Text = Texts.T("archive.loading");
        try
        {
            await using ArchiveReader reader = await _openReader(loading.Token);
            ArchiveQuotePage result = await reader.GetBacklinksAsync(post, _referenceOffset, PageSize, loading.Token);
            if (_ended || request != _referenceRequest) return;
            _referenceTotal = result.TotalCount;
            ArchiveReferences.ItemsSource = result.Items.Select(quote => new ArchiveQuoteRow(quote, true)).ToArray();
            ArchiveReferencesTitle.Text = Texts.T(result.TotalCount == 0 ? "archive.noBacklinks" : "archive.backlinks", post.PostId);
            ArchiveReferencesPage.Text = PageLabel(_referenceOffset, _referenceTotal);
            PreviousReferences.IsEnabled = _referenceOffset > 0;
            NextReferences.IsEnabled = _referenceOffset + PageSize < _referenceTotal;
            // A post nobody replied to has nothing to list: the tab goes away and the reader shows the posts again.
            ShowReferencesTab(_referenceTotal);
            if (_referenceTotal == 0) ArchiveContentTabs.SelectedIndex = 0;
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
