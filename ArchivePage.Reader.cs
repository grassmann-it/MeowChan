using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using MeowChan.Archive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;

namespace MeowChan;

public partial class ArchivePage
{
    internal Action? FullscreenRequested { get; set; }
    internal bool IsReaderDialogOpen => Control<Border>("ArchiveExportDialog").IsVisible;
    internal bool TryDismissOverlay()
    {
        if (!IsReaderDialogOpen) return false;
        OnExportClose(this, new RoutedEventArgs());
        return true;
    }
    private void OnReaderFullscreenClick(object? sender, RoutedEventArgs e) => FullscreenRequested?.Invoke();
    private double _readingFontSizePreference = 15;
    internal double ReadingFontSizePreference
    {
        get => _readingFontSizePreference;
        set { _readingFontSizePreference = value; Control<ListBox>("ArchivePosts").FontSize = value; }
    }
    private bool _largeReader;
    private IReadOnlyList<ResourceId> _readerThreads = Array.Empty<ResourceId>();
    private int _readerScopeRequest;
    private string? _readerPostId;
    private int _readerFocusRequest;
    private int _readerInteractionRevision;
    private void InitializeReaderInteraction()
    {
        ListBox posts = Control<ListBox>("ArchivePosts");
        posts.AddHandler(PointerWheelChangedEvent, (_, _) => ++_readerInteractionRevision, RoutingStrategies.Tunnel, handledEventsToo: true);
        posts.AddHandler(PointerPressedEvent, (_, _) => ++_readerInteractionRevision, RoutingStrategies.Tunnel, handledEventsToo: true);
        posts.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.PageDown or Key.PageUp or Key.Home or Key.End or Key.Space) ++_readerInteractionRevision;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    internal bool ReaderShowsPosts => _showReader && Control<TabControl>("ArchiveContentTabs").SelectedIndex == 0;
    internal Action<Control?>? OpenReaderRequested { get; set; }
    internal Action? CloseReaderRequested { get; set; }
    private void OnCloseReaderClick(object? sender, RoutedEventArgs e) => CloseReaderRequested?.Invoke();

    internal Control DetachReader()
    {
        ++_readerFocusRequest;
        Grid pane = Control<Grid>("ArchiveReaderPane");
        Control<Grid>("ArchiveSplit").Children.Remove(pane);
        Border export = Control<Border>("ArchiveExportDialog");
        Control<Grid>("ArchiveRoot").Children.Remove(export);
        pane.Children.Add(export);
        _largeReader = true;
        KeyboardNavigation.SetTabNavigation(pane, KeyboardNavigationMode.Cycle);
        pane.AddHandler(KeyDownEvent, OnReaderKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(ContextRequestedEvent, OnArchiveMediaContextRequested, RoutingStrategies.Tunnel);
        pane.Margin = new Thickness(20);
        Control<Button>("ArchiveCloseReader").IsVisible = true;
        Control<Button>("ArchiveReadPosts").IsVisible = false;
        Control<ListBox>("ArchivePosts").MaxWidth = 1120;
        UpdateLayoutMode();
        if (_thread != null) RenderThread();
        return pane;
    }

    internal void AttachReader(Control pane)
    {
        ++_readerFocusRequest;
        _largeReader = false;
        Border export = Control<Border>("ArchiveExportDialog");
        ((Grid)pane).Children.Remove(export);
        Control<Grid>("ArchiveRoot").Children.Add(export);
        ++_readerScopeRequest;
        _readerThreads = Array.Empty<ResourceId>();
        pane.RemoveHandler(KeyDownEvent, OnReaderKeyDown);
        pane.RemoveHandler(ContextRequestedEvent, OnArchiveMediaContextRequested);
        pane.Margin = default;
        Control<Button>("ArchiveCloseReader").IsVisible = false;
        Control<Button>("ArchiveReadPosts").IsVisible = true;
        Control<ListBox>("ArchivePosts").MaxWidth = double.PositiveInfinity;
        Control<Grid>("ArchiveSplit").Children.Add(pane);
        UpdateLayoutMode();
        if (_thread != null) RenderThread();
    }

    internal Task FocusPostAsync(string postId) => FocusPostAsync(postId, null);
    private async Task FocusPostAsync(string postId, int? interactionRevision)
    {
        int focusRequest = ++_readerFocusRequest;
        ResourceId? threadId = _thread?.Summary.Id;
        int request = _contentRequest;
        _readerPostId = postId;
        ShowPost(postId);
        UpdateReaderNavigation();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_ended || focusRequest != _readerFocusRequest || request != _contentRequest || _thread?.Summary.Id != threadId
                || (interactionRevision.HasValue && interactionRevision != _readerInteractionRevision)
                || _thread?.FindPostIndex(postId) is not >= 0) return;
            ListBox posts = Control<ListBox>("ArchivePosts");
            if (posts.ItemsSource is not System.Collections.Generic.IEnumerable<ArchivePostRow> rows) return;
            ArchivePostRow? row = rows.FirstOrDefault(post => post.Id == postId);
            if (row == null) return;
            posts.SelectedItem = row;
            posts.UpdateLayout();
            posts.ScrollIntoView(row);
            posts.UpdateLayout();
            if (posts.ContainerFromItem(row) is Control container)
            {
                ScrollViewer? scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                Point? position = container.TranslatePoint(default, posts);
                if (scroll != null && position != null)
                    scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + position.Value.Y - 8));
                container.Focus();
            }
            else posts.Focus();
        }, DispatcherPriority.Loaded);
    }
    internal void SetReaderScope(IReadOnlyList<ResourceId> threads)
    {
        ++_readerScopeRequest;
        _readerThreads = threads.ToArray();
        UpdateReaderNavigation();
    }

    internal async Task FreezeReaderScopeAsync()
    {
        int request = ++_readerScopeRequest;
        ArchiveThreadQuery query = _query;
        int filter = _workFilter;
        var work = GetWorkSnapshots?.Invoke().GroupBy(item => item.Url, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        try
        {
            if (_openReader == null) return;
            IReadOnlyList<ResourceId> ids = await Task.Run(async () =>
            {
                await using ArchiveReader reader = await _openReader(_lifetime.Token);
                if (filter == 0) return await reader.SelectThreadIdsAsync(query, _lifetime.Token);
                var summaries = await reader.SelectThreadSummariesAsync(query, _lifetime.Token);
                return (IReadOnlyList<ResourceId>)summaries.Where(summary => WorkMatches(work?.GetValueOrDefault(summary.Url), filter))
                    .Select(summary => summary.Id).ToArray();
            }, _lifetime.Token);
            if (_largeReader && request == _readerScopeRequest) { _readerThreads = ids; UpdateReaderNavigation(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception exception) { if (_largeReader && request == _readerScopeRequest) Message(exception.Message, true); }
    }

    private int ReaderPostIndex
    {
        get
        {
            if (_thread == null) return -1;
            return _thread.FindPostIndex((Control<ListBox>("ArchivePosts").SelectedItem as ArchivePostRow)?.Id
                ?? _readerPostId ?? _thread.GetPosts(_postOffset, 1).FirstOrDefault()?.PostId ?? string.Empty);
        }
    }
    private int ReaderThreadIndex => _thread == null ? -1 : Array.IndexOf(_readerThreads.ToArray(), _thread.Summary.Id);

    private void UpdateReaderNavigation()
    {
        // The reader can be shown before its hidden ArchivePage is attached for the first time.
        if (!IsInitialized && !_largeReader) return;
        int post = ReaderPostIndex;
        int thread = ReaderThreadIndex;
        Control<Button>("ReaderPreviousPost").IsEnabled = !_busy && post > 0;
        Control<Button>("ReaderNextPost").IsEnabled = !_busy && post >= 0 && post + 1 < (_thread?.PostCount ?? 0);
        Control<Button>("ReaderPreviousThread").IsEnabled = !_busy && thread > 0;
        Control<Button>("ReaderNextThread").IsEnabled = !_busy && thread >= 0 && thread + 1 < _readerThreads.Count;
        Control<WrapPanel>("ReaderNavigation").IsVisible = _largeReader;
    }

    private async void OnReaderNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        await NavigateReaderAsync(button.Name switch { "ReaderPreviousPost" => -1, "ReaderNextPost" => 1, _ => 0 },
            button.Name switch { "ReaderPreviousThread" => -1, "ReaderNextThread" => 1, _ => 0 });
    }

    private async void OnReaderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !_largeReader || e.KeyModifiers != KeyModifiers.None || Control<Border>("ArchiveExportDialog").IsVisible) return;
        if (e.Source is Visual source)
        {
            if (source.GetVisualAncestors().Prepend(source).Any(control => control is TextBox or ComboBox)) return;
            if (source.GetVisualAncestors().Prepend(source).OfType<SelectableTextBlock>()
                .Any(text => text.SelectionStart != text.SelectionEnd)) return;
        }
        if (e.Key == Key.Escape) { e.Handled = true; CloseReaderRequested?.Invoke(); return; }
        int post = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        int thread = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
        if (post == 0 && thread == 0) return;
        e.Handled = true;
        await NavigateReaderAsync(post, thread);
    }

    internal async Task NavigateReaderAsync(int postDelta, int threadDelta)
    {
        if (!_largeReader || _busy || _ended) return;
        if (threadDelta != 0)
        {
            int index = ReaderThreadIndex + threadDelta;
            if (ReaderThreadIndex < 0 || index < 0 || index >= _readerThreads.Count) return;
            ResourceId target = _readerThreads[index];
            await LoadThreadAsync(target);
            if (!_largeReader || _ended || _thread?.Summary.Id != target) return;
            _readerPostId = null;
            string? first = _thread?.GetPosts(0, 1).FirstOrDefault()?.PostId;
            if (first != null) await FocusPostAsync(first);
            else Control<Button>("ArchiveCloseReader").Focus();
        }
        else if (_thread != null)
        {
            int index = ReaderPostIndex + postDelta;
            if (ReaderPostIndex < 0 || index < 0 || index >= _thread.PostCount) return;
            await FocusPostAsync(_thread.GetPosts(index, 1).Single().PostId);
        }
        UpdateReaderNavigation();
    }

    private sealed record ReaderAnchor(ResourceId Thread, string PostId, double Top, string? SelectedPostId);
    private ReaderAnchor? CaptureReaderAnchor()
    {
        if (_thread == null) return null;
        ListBox list = Control<ListBox>("ArchivePosts");
        string? selected = (list.SelectedItem as ArchivePostRow)?.Id;
        var visible = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(item => (Item: item, Top: item.TranslatePoint(default, list)?.Y ?? double.NaN))
            .Where(item => item.Item.DataContext is ArchivePostRow && item.Top + item.Item.Bounds.Height > 0)
            .OrderBy(item => item.Top).FirstOrDefault();
        if (visible.Item?.DataContext is ArchivePostRow row)
            return new ReaderAnchor(_thread.Summary.Id, row.Id, visible.Top, selected);
        string? post = (list.SelectedItem as ArchivePostRow)?.Id ?? _thread.GetPosts(_postOffset, 1).FirstOrDefault()?.PostId;
        return post == null ? null : new ReaderAnchor(_thread.Summary.Id, post, 0, selected);
    }

    private async Task RestoreReaderAnchorAsync(ReaderAnchor anchor)
    {
        if (_thread?.Summary.Id != anchor.Thread) return;
        int request = _contentRequest;
        int interactionRevision = _readerInteractionRevision;
        int focusRequest = _readerFocusRequest + 1;
        await FocusPostAsync(anchor.PostId, interactionRevision);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_ended || request != _contentRequest || focusRequest != _readerFocusRequest
                || interactionRevision != _readerInteractionRevision || _thread?.Summary.Id != anchor.Thread) return;
            ListBox list = Control<ListBox>("ArchivePosts");
            if (anchor.SelectedPostId != null && list.ItemsSource is IEnumerable<ArchivePostRow> rows)
            {
                ArchivePostRow? selected = rows.FirstOrDefault(row => row.Id == anchor.SelectedPostId);
                if (selected != null) { list.SelectedItem = selected; _readerPostId = selected.Id; }
            }
            ScrollViewer? scroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            ArchivePostRow? anchored = list.ItemsSource?.Cast<ArchivePostRow>().FirstOrDefault(row => row.Id == anchor.PostId);
            if (scroll != null && anchored != null && list.ContainerFromItem(anchored) is Control container
                && container.TranslatePoint(default, list) is Point position)
                scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Offset.Y + position.Y - anchor.Top));
        }, DispatcherPriority.Loaded);
    }

}
