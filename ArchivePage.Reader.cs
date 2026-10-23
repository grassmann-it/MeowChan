using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using MeowChan.Archive;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Data;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

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
    private IDisposable? _readerTabScope;
    internal Task ReaderNavigationTask { get; private set; } = Task.CompletedTask;
    private void InitializeReaderInteraction()
    {
        foreach (Control media in new Control[] { Control<ListBox>("ArchiveMediaTiles"), Control<DataGrid>("ArchiveMediaGrid") })
            media.AddHandler(KeyDownEvent, OnMediaKeyDown, RoutingStrategies.Tunnel);
        Control<Grid>("ReaderContentSurface").LayoutUpdated += (_, _) => UpdateReaderNavigationLayout();
        ListBox posts = Control<ListBox>("ArchivePosts");
        posts.SelectionChanged += (_, _) =>
        {
            if (posts.SelectedItem is ArchivePostRow selected) _readerPostId = selected.Id;
            UpdateReaderNavigation();
        };
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
        // A disposable override preserves the original value source (including
        // styles, bindings and an unset local value) when the pane is reattached.
        _readerTabScope?.Dispose();
        _readerTabScope = pane.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Cycle, BindingPriority.Animation);
        pane.SetValue(ReaderPostLayout.LargeReaderProperty, true);
        pane.AddHandler(KeyDownEvent, OnReaderKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        pane.AddHandler(ContextRequestedEvent, OnArchiveMediaContextRequested, RoutingStrategies.Tunnel);
        pane.Margin = new Thickness(20);
        Control<Button>("ArchiveCloseReader").IsVisible = true;
        Control<Button>("ArchiveReadPosts").IsVisible = false;
        Control<ListBox>("ArchivePosts").MaxWidth = 1120;
        UpdateReaderNavigation();
        UpdateLayoutMode();
        if (_thread != null) RenderThread();
        return pane;
    }

    internal void AttachReader(Control pane)
    {
        ++_readerFocusRequest;
        _largeReader = false;
        _readerTabScope?.Dispose();
        _readerTabScope = null;
        pane.ClearValue(ReaderPostLayout.LargeReaderProperty);
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
        UpdateReaderNavigation();
        Control<Grid>("ArchiveSplit").Children.Add(pane);
        UpdateLayoutMode();
        if (_thread != null) RenderThread();
    }

    internal Task FocusPostAsync(string postId) => FocusPostAsync(postId, null);
    private async Task FocusPostAsync(string postId, int? interactionRevision, bool restoreFocus = true)
    {
        // A live continuation can already be stale before its first dispatcher
        // yield. Validate before ShowPost changes the page or selection.
        if (_ended || _thread?.FindPostIndex(postId) is not >= 0
            || (interactionRevision.HasValue && interactionRevision != _readerInteractionRevision)) return;
        int focusRequest = ++_readerFocusRequest;
        ResourceId? threadId = _thread?.Summary.Id;
        int request = _contentRequest;
        _readerPostId = postId;
        ShowPost(postId);
        UpdateReaderNavigation();
        ListBox posts = Control<ListBox>("ArchivePosts");
        bool IsCurrent() => !_ended && focusRequest == _readerFocusRequest && request == _contentRequest
            && _thread?.Summary.Id == threadId
            && (!interactionRevision.HasValue || interactionRevision == _readerInteractionRevision);
        // ScrollIntoView realizes a virtualized target. Its measured height can
        // replace an estimate on the next layout; retry only while the request
        // remains current, and correct offsets only after layout has settled.
        for (int pass = 0; pass < 4; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!IsCurrent()) return;
            ArchivePostRow? row = posts.ItemsSource?.Cast<ArchivePostRow>().FirstOrDefault(post => post.Id == postId);
            if (row == null) return;
            posts.SelectedItem = row;
            posts.UpdateLayout();
            if (pass == 0 || posts.ContainerFromItem(row) == null) posts.ScrollIntoView(row);
            posts.UpdateLayout();
            if (posts.ContainerFromItem(row) is not Control container) continue;
            ScrollViewer? scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            ScrollContentPresenter? viewport = scroll?.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
            if (viewport == null || container.TranslatePoint(default, viewport) is not Point position) continue;
            double offset = Math.Clamp(scroll!.Offset.Y + position.Y - 8, 0,
                Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
            if (Math.Abs(offset - scroll.Offset.Y) > 1)
            {
                scroll.Offset = new Vector(scroll.Offset.X, offset);
                continue;
            }
            // Focus must not cause a second competing BringIntoView request.
            if (restoreFocus) container.Focus();
            ReaderPositionChanged(deliberate: !interactionRevision.HasValue);
            return;
        }
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
            string? selected = (Control<ListBox>("ArchivePosts").SelectedItem as ArchivePostRow)?.Id;
            int retained = _readerPostId == null ? -1 : _thread.FindPostIndex(_readerPostId);
            return _thread.FindPostIndex(selected
                ?? (retained >= _postOffset && retained < _postOffset + PageSize ? _readerPostId : null)
                ?? _thread.GetPosts(_postOffset, 1).FirstOrDefault()?.PostId ?? string.Empty);
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
        UpdateReaderNavigationLayout();
    }

    private void UpdateReaderNavigationLayout()
    {
        TabControl tabs = Control<TabControl>("ArchiveContentTabs");
        bool postsSelected = tabs.SelectedIndex == 0;
        Grid surface = Control<Grid>("ReaderContentSurface");
        ListBox posts = Control<ListBox>("ArchivePosts");
        // Use the spare horizontal space beside the capped reading width when
        // available. At smaller sizes reserve a shallow band outside the scroll
        // viewport; a final spacer would still let other posts pass underneath.
        bool sideSpace = _largeReader && surface.Bounds.Width >= posts.MaxWidth + 186;
        posts.Margin = !_largeReader ? default : sideSpace ? new Thickness(0, 0, 186, 0) : new Thickness(0, 0, 0, 42);
        tabs.Margin = _largeReader && !postsSelected ? new Thickness(0, 0, 0, 66) : default;
        if (!_largeReader) return;
        Grid pageBar = Control<Grid>("ArchivePostNavigation");
        double bottom = 16;
        if (postsSelected && pageBar.IsEffectivelyVisible && pageBar.TranslatePoint(default, surface) is Point position)
            bottom = Math.Max(bottom, surface.Bounds.Height - position.Y + 8);
        Control<WrapPanel>("ReaderNavigation").Margin = new Thickness(0, 0, 24, bottom);
    }

    private async void OnReaderNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        await (ReaderNavigationTask = NavigateReaderAsync(button.Name switch { "ReaderPreviousPost" => -1, "ReaderNextPost" => 1, _ => 0 },
            button.Name switch { "ReaderPreviousThread" => -1, "ReaderNextThread" => 1, _ => 0 }));
    }

    private async void OnReaderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !_largeReader || IsReaderDialogOpen) return;
        if (e.Source is Visual source)
        {
            if (source.GetSelfAndVisualAncestors().Any(control => control is TextBox or ComboBox or NumericUpDown or MenuItem or Slider or ScrollBar)) return;
            if (source.GetVisualAncestors().Prepend(source).OfType<SelectableTextBlock>()
                .Any(text => text.SelectionStart != text.SelectionEnd)) return;
        }
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (e.KeyModifiers == primary && e.Key == Key.E && _thread != null)
        {
            if (_busy || _ended) return;
            e.Handled = true;
            await OpenExportForThreadsAsync(new[] { _thread.Summary.Id }, e.Source as Control);
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Shift && e.Key == Key.Enter && _thread != null && OpenGallery != null)
        {
            if (_busy || _ended) return;
            // Buttons and native editors retain their activation gestures.
            if (e.Source is Visual visual && visual.GetSelfAndVisualAncestors().Any(node => node is Button or Slider or TabItem)) return;
            e.Handled = true;
            await OpenGallery(_thread.Summary.Id, null, e.Source as Control);
            return;
        }
        if (e.KeyModifiers != KeyModifiers.None) return;
        if (e.Key == Key.Escape) { e.Handled = true; CloseReaderRequested?.Invoke(); return; }
        if (ReaderArrowBelongsToNativeControl(e.Source)) return;
        int post = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        int thread = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
        if (post == 0 && thread == 0) return;
        e.Handled = true;
        await (ReaderNavigationTask = NavigateReaderAsync(post, thread));
    }

    internal bool ReaderArrowBelongsToNativeControl(object? source)
    {
        if (source is not Visual visual) return false;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, Control<ListBox>("ArchivePosts"))) return false;
            if (node is TabItem or TabStrip or DataGrid or DataGridColumnHeader or Slider or ScrollBar
                or TextBox or ComboBox or NumericUpDown or MenuItem || node is SelectingItemsControl and not TabControl) return true;
        }
        return false;
    }

    internal async Task NavigateReaderAsync(int postDelta, int threadDelta)
    {
        if (!_largeReader || _busy || _ended) return;
        ++_readerInteractionRevision;
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
            .Where(item => item.Item.DataContext is ArchivePostRow && item.Top < list.Bounds.Height && item.Top + item.Item.Bounds.Height > 0)
            .OrderBy(item => item.Top).FirstOrDefault();
        if (visible.Item?.DataContext is ArchivePostRow row)
            return new ReaderAnchor(_thread.Summary.Id, row.Id, visible.Top, selected);
        string? post = (list.SelectedItem as ArchivePostRow)?.Id ?? _thread.GetPosts(_postOffset, 1).FirstOrDefault()?.PostId;
        return post == null ? null : new ReaderAnchor(_thread.Summary.Id, post, 0, selected);
    }

    private async Task RestoreReaderAnchorAsync(ReaderAnchor anchor, bool restoreFocus = true)
    {
        if (_thread?.Summary.Id != anchor.Thread) return;
        if (_thread.FindPostIndex(anchor.PostId) < 0)
        {
            string? fallback = _thread.GetPosts(Math.Min(_postOffset, Math.Max(0, _thread.PostCount - 1)), 1).FirstOrDefault()?.PostId;
            Message(Texts.T(fallback == null ? "archive.noPosts" : "reader.anchorUnavailable"));
            if (fallback != null) await FocusPostAsync(fallback, _readerInteractionRevision, restoreFocus);
            return;
        }
        int request = _contentRequest;
        int interactionRevision = _readerInteractionRevision;
        int focusRequest = _readerFocusRequest + 1;
        await FocusPostAsync(anchor.PostId, interactionRevision, restoreFocus);
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
