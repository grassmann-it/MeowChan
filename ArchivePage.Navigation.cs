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
    private ArchiveThreadQuery _query = new ArchiveThreadQuery();
    private readonly Stack<(ArchivedThreadView View, int Offset)> _history = new();
    private CancellationTokenSource? _referenceCancellation;
    private int _referenceRequest;
    private ArchivePostId? _backlinkPost;
    private int _referenceOffset;
    private int _referenceTotal;

    private void LocalizeSearch()
    {
        Control<TextBox>("ArchiveSearchText").PlaceholderText = Texts.T("archive.searchHint");
        Control<TextBox>("ArchiveSourceFilter").PlaceholderText = Texts.T("archive.sourceFilter");
        Control<TextBox>("ArchiveBoardFilter").PlaceholderText = Texts.T("archive.boardFilter");
        Control<Expander>("ArchiveFilters").Header = Texts.T("archive.filters");
        SetOptions("ArchiveStateFilter", "archive.allStates", "archive.sourceActive", "archive.sourceArchived", "archive.sourceMissing");
        SetOptions("ArchiveCaptureFilter", "archive.allCapture", "archive.captureEnabled", "archive.captureDisabled");
        SetOptions("ArchivePostsFilter", "archive.allContent", "archive.withPosts", "archive.withoutPosts");
        SetOptions("ArchiveWorkFilter", "gallery.allWork", "gallery.watching", "gallery.followup",
            "gallery.intervention", "gallery.completed", "gallery.stopped");
        SetOptions("ArchiveSort", "archive.latest", "archive.oldest", "archive.titleSort");
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
        string? Filter(string name) => string.IsNullOrWhiteSpace(Control<TextBox>(name).Text)
            ? null : Control<TextBox>(name).Text!.Trim();
        bool? Choice(string name) => Control<ComboBox>(name).SelectedIndex switch { 1 => true, 2 => false, _ => null };
        _query = new ArchiveThreadQuery(Control<TextBox>("ArchiveSearchText").Text ?? string.Empty,
            Filter("ArchiveSourceFilter"), Filter("ArchiveBoardFilter"),
            Control<ComboBox>("ArchiveStateFilter").SelectedIndex switch
            { 1 => ThreadSourceState.Active, 2 => ThreadSourceState.Archived, 3 => ThreadSourceState.NotFound, _ => null },
            Choice("ArchiveCaptureFilter"), Choice("ArchivePostsFilter"),
            (ArchiveThreadSort)Math.Max(0, Control<ComboBox>("ArchiveSort").SelectedIndex));
        _workFilter = Math.Max(0, Control<ComboBox>("ArchiveWorkFilter").SelectedIndex);
        _listOffset = 0;
        _showReader = false;
        UpdateLayoutMode();
        await LoadListAsync();
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
        CancelReferences();
        int request = _referenceRequest;
        using CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _referenceCancellation = loading;
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
                if (_history.Count == 32) _history.Clear();
                _history.Push((_thread, _postOffset));
            }
            ShowCapturedView(resolved.View, resolved.PostIndex.Value / PageSize * PageSize, resolved.Post!.PostId);
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested) { }
        catch (Exception exception) { if (!_ended && request == _referenceRequest) Message(exception.Message, true); }
        finally { if (ReferenceEquals(_referenceCancellation, loading)) _referenceCancellation = null; }
    }
    private void ShowCapturedView(ArchivedThreadView view, int offset, string? postId = null)
    {
        ++_contentRequest;
        _contentCancellation?.Cancel();
        CancelMediaLoad();
        ClosePreview();
        _selectedThread = view.Summary.Id;
        _thread = view;
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
    private void OnHistoryBack(object? sender, RoutedEventArgs e)
    {
        if (_busy || _ended || !_history.TryPop(out (ArchivedThreadView View, int Offset) previous)) return;
        CancelReferences();
        ShowCapturedView(previous.View, previous.Offset);
    }
    private async void OnBacklinksClick(object? sender, RoutedEventArgs e)
    {
        if (_thread == null || sender is not Button { DataContext: ArchivePostRow row }) return;
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
        catch (Exception exception) { if (!_ended && request == _referenceRequest) Message(exception.Message, true); }
        finally { if (ReferenceEquals(_referenceCancellation, loading)) _referenceCancellation = null; }
    }
    private async void OnPreviousReferences(object? sender, RoutedEventArgs e)
    { _referenceOffset = Math.Max(0, _referenceOffset - PageSize); await LoadBacklinksAsync(); }
    private async void OnNextReferences(object? sender, RoutedEventArgs e)
    { if (_referenceOffset + PageSize >= _referenceTotal) return; _referenceOffset += PageSize; await LoadBacklinksAsync(); }
}
