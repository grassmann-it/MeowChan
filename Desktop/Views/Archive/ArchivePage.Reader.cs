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
using MeowChan.Desktop.Commands;

namespace MeowChan;

public partial class ArchivePage
{
    internal Action? FullscreenRequested { get; set; }
    internal bool IsReaderDialogOpen => Control<Border>("ArchiveExportDialog").IsVisible || _cleanupDialog != null;
    internal bool TryDismissOverlay()
    {
        if (_cleanupDialog != null) { _cleanupDialog.RequestClose(); return true; }
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
    private readonly ReaderPostTailSpace _readerPostTailSpace = new();
    private double _readerPostTailInset;
    private IReadOnlyList<ResourceId> _readerThreads = Array.Empty<ResourceId>();
    private int _readerScopeRequest;
    private string? _readerPostId;
    private int _readerFocusRequest;
    private int _readerInteractionRevision;
    private int _readerLayoutRevision;
    private IDisposable? _readerTabScope;
    internal Task ReaderNavigationTask { get; private set; } = Task.CompletedTask;
    internal Task ReaderLocalizationTask { get; private set; } = Task.CompletedTask;
    // A controlled boundary for the smoke fixture; normal operations have no extra wait.
    internal Func<Task>? BeforePassiveReaderFocusAsync { get; set; }

    // Permission belongs to one passive operation, not to the reader pane. In particular,
    // removing our original post container may clear focus; any newer target revokes it.
    private sealed class ReaderFocusIntent : IDisposable
    {
        private readonly ArchivePage _page;
        private readonly Control _pane;
        private readonly Visual[] _visibilityOwners;
        private readonly Window? _owner;
        private readonly ResourceId? _thread;
        private readonly long _coverage;
        private readonly int _interaction;
        private readonly bool _requireRefreshAllowed;
        private int _content;
        private int _focus;
        private Control? _ownedFocus;
        private Control? _destination;
        private bool _invalid;

        internal ReaderFocusIntent(ArchivePage page, bool requireRefreshAllowed = false)
        {
            _page = page;
            _pane = page.Control<Grid>("ArchiveReaderPane");
            _visibilityOwners = _pane.GetSelfAndVisualAncestors().ToArray();
            _owner = TopLevel.GetTopLevel(_pane) as Window;
            _thread = page._thread?.Summary.Id;
            _coverage = page._readerCoverageRevision;
            _interaction = page._readerInteractionRevision;
            _content = page._contentRequest;
            _focus = page._readerFocusRequest;
            _requireRefreshAllowed = requireRefreshAllowed;
            _ownedFocus = _owner?.FocusManager?.GetFocusedElement() as Control;
            ListBox posts = page.Control<ListBox>("ArchivePosts");
            // Headers and other surviving reader controls never need replacement focus.
            _invalid = _ownedFocus == null || !_ownedFocus.GetSelfAndVisualAncestors()
                .OfType<ListBoxItem>().Any(item => item.DataContext is ArchivePostRow
                    && item.GetVisualAncestors().Contains(posts));
            if (_owner != null)
            {
                _owner.AddHandler(GotFocusEvent, OnNewFocus, RoutingStrategies.Bubble, handledEventsToo: true);
                _owner.Deactivated += OnDeactivated;
            }
            _page.PropertyChanged += OnCoverageChanged;
            _pane.DetachedFromVisualTree += OnDetached;
            foreach (Visual visual in _visibilityOwners) visual.PropertyChanged += OnVisibilityChanged;
            if (!MayRestore) _invalid = true;
        }

        private bool MayRestore => !_invalid && !_page._ended && _page.ReaderShowsPosts
            && _page._thread?.Summary.Id == _thread && _coverage == _page._readerCoverageRevision
            && _interaction == _page._readerInteractionRevision && _content == _page._contentRequest
            && _focus == _page._readerFocusRequest
            && !_page._obscuredByGallery && !ArchiveAttachmentView.GetLoadingSuspended(_page)
            && !_page.IsReaderDialogOpen && (!_requireRefreshAllowed || (_page.ReaderRefreshAllowed?.Invoke() ?? true))
            && _pane.IsEffectivelyVisible && _pane.IsEffectivelyEnabled
            && _owner is { IsActive: true, IsVisible: true }
            && !_owner.OwnedWindows.Any(window => window.IsVisible)
            && ReferenceEquals(TopLevel.GetTopLevel(_pane), _owner)
            && (_owner.FocusManager?.GetFocusedElement() is not { } current || ReferenceEquals(current, _ownedFocus));

        private void OnNewFocus(object? sender, FocusChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, _destination)) _invalid = true;
        }
        private void OnDeactivated(object? sender, EventArgs e) => _invalid = true;
        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e) => _invalid = true;
        private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if ((e.Property == Visual.IsVisibleProperty || e.Property == InputElement.IsEnabledProperty)
                && e.NewValue is false) _invalid = true;
        }
        private void OnCoverageChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == ArchiveAttachmentView.LoadingSuspendedProperty) _invalid = true;
        }

        internal void BeginContent(int request)
        {
            if (request != _content + 1) _invalid = true;
            _content = request;
        }
        internal void BeginFocus(int request)
        {
            if (request != _focus + 1) _invalid = true;
            _focus = request;
        }

        internal void Restore(Control target)
        {
            if (!MayRestore) return;
            _destination = target;
            try
            {
                if (target.Focus() && ReferenceEquals(_owner!.FocusManager?.GetFocusedElement(), target))
                    _ownedFocus = target;
            }
            finally { _destination = null; }
        }

        public void Dispose()
        {
            _invalid = true;
            if (_owner != null)
            {
                _owner.RemoveHandler(GotFocusEvent, OnNewFocus);
                _owner.Deactivated -= OnDeactivated;
            }
            _page.PropertyChanged -= OnCoverageChanged;
            _pane.DetachedFromVisualTree -= OnDetached;
            foreach (Visual visual in _visibilityOwners) visual.PropertyChanged -= OnVisibilityChanged;
        }
    }
    private void InitializeReaderInteraction()
    {
        foreach (Control media in new Control[] { Control<ListBox>("ArchiveMediaTiles"), Control<DataGrid>("ArchiveMediaGrid") })
        {
            CommandBindings.BindExisting(media, "media.open");
            media.AddHandler(KeyDownEvent, OnMediaKeyDown, RoutingStrategies.Tunnel);
        }
        Control<Grid>("ReaderContentSurface").LayoutUpdated += (_, _) => UpdateReaderNavigationLayout();
        ListBox posts = Control<ListBox>("ArchivePosts");
        posts.ContainerPrepared += (_, e) => UpdateReaderPostTailSpace();
        posts.ContainerIndexChanged += (_, e) => UpdateReaderPostTailSpace();
        posts.ContainerClearing += (_, e) => _readerPostTailSpace.Release(e.Container);
        posts.DetachedFromVisualTree += (_, _) => _readerPostTailSpace.Update(null, 0);
        posts.SelectionChanged += (_, _) =>
        {
            if (posts.SelectedItem is ArchivePostRow selected) _readerPostId = selected.Id;
            UpdateReaderNavigation();
        };
        posts.AddHandler(KeyDownEvent, (_, e) => TryRouteArchiveMediaShortcut(posts, e), RoutingStrategies.Bubble);
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
    private async Task FocusPostAsync(string postId, int? interactionRevision, bool restoreFocus = true,
        ReaderFocusIntent? focusIntent = null)
    {
        // A live continuation can already be stale before its first dispatcher
        // yield. Validate before ShowPost changes the page or selection.
        if (_ended || _thread?.FindPostIndex(postId) is not >= 0
            || (interactionRevision.HasValue && interactionRevision != _readerInteractionRevision)) return;
        int focusRequest = ++_readerFocusRequest;
        focusIntent?.BeginFocus(focusRequest);
        ResourceId? threadId = _thread?.Summary.Id;
        int request = _contentRequest;
        _readerPostId = postId;
        ShowPost(postId);
        UpdateReaderNavigation();
        ListBox posts = Control<ListBox>("ArchivePosts");
        bool IsCurrent() => !_ended && focusRequest == _readerFocusRequest && request == _contentRequest
            && _thread?.Summary.Id == threadId
            && (!interactionRevision.HasValue || interactionRevision == _readerInteractionRevision);
        if (focusIntent != null && BeforePassiveReaderFocusAsync is { } beforeFocus) await beforeFocus();
        // ScrollIntoView realizes a virtualized target. Its measured height can
        // replace an estimate on the next layout; retry only while the request
        // remains current, and correct offsets only after layout has settled.
        for (int pass = 0; pass < 4; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!IsCurrent()) return;
            ArchivePostRow? row = posts.ItemsSource?.Cast<ArchivePostRow>().FirstOrDefault(post => post.Id == postId);
            if (row == null) return;
            ++_readerLayoutRevision;
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
            if (restoreFocus)
            {
                if (focusIntent != null) focusIntent.Restore(container);
                else container.Focus();
            }
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
        ArchiveSearchRequest query = _appliedSearch ?? ArchiveSearchRequest.Create(_query);
        int filter = _workFilter;
        IReadOnlySet<string>? eligibleUrls = CaptureExportWorkUrls(filter,
            GetWorkSnapshots?.Invoke() ?? Array.Empty<ThreadWorkSnapshot>());
        try
        {
            if (_openReader == null) return;
            IReadOnlyList<ResourceId> ids = await Task.Run(async () =>
            {
                await using ArchiveReader reader = await _openReader(_lifetime.Token);
                return (IReadOnlyList<ResourceId>)await ResolveExportResultIdsAsync(reader, query, eligibleUrls, _lifetime.Token);
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
        Control<Button>("ReaderPreviousThread").IsEnabled = !_busy && thread > 0 && (!_readerGroupScope || ReaderGroupTargetIndex(-1) >= 0);
        Control<Button>("ReaderNextThread").IsEnabled = !_busy && thread >= 0 && thread + 1 < _readerThreads.Count
            && (!_readerGroupScope || ReaderGroupTargetIndex(1) < _readerThreads.Count);
        Control<WrapPanel>("ReaderNavigation").IsVisible = _largeReader;
        UpdateReaderNavigationLayout();
    }

    private void UpdateReaderNavigationLayout()
    {
        TabControl tabs = Control<TabControl>("ArchiveContentTabs");
        Control<WrapPanel>("ReaderStatusLine").IsVisible = !_largeReader || tabs.SelectedIndex == 0;
        Grid surface = Control<Grid>("ReaderContentSurface");
        ListBox posts = Control<ListBox>("ArchivePosts");
        ReaderNavigationLayout layout = ReaderNavigationLayout.Resolve(_largeReader, surface.Bounds.Width, posts.MaxWidth);
        posts.Margin = layout.PostMargin;
        _readerPostTailInset = layout.TailInset;
        UpdateReaderPostTailSpace();
        // Secondary tabs keep full-width filters and footers. Reserve only
        // their scrolling surface, so the cross cannot consume their height
        // or force another line of media filters in a narrow window.
        tabs.Margin = default;
        Thickness secondaryLane = _largeReader ? new Thickness(0, 0, ReaderNavigationLayout.SideReserve, 0) : default;
        Control<Grid>("ArchiveMediaSurface").Margin = secondaryLane;
        Control<ListBox>("ArchiveReferences").Margin = secondaryLane;
        if (!_largeReader) return;
        Control footer = tabs.SelectedIndex switch
        {
            1 => Control<StackPanel>("ArchiveMediaFooter"),
            2 => Control<Grid>("ArchiveReferencesNavigation"),
            _ => Control<Grid>("ArchivePostNavigation")
        };
        double bottom = 16;
        if (footer.IsEffectivelyVisible && footer.TranslatePoint(default, surface) is Point position)
            bottom = Math.Max(bottom, surface.Bounds.Height - position.Y + 8);
        double right = 24;
        // MaxWidth can center the post list even just below the free-side-lane
        // threshold. Anchor inside its right edge, leaving the scrollbar clear.
        if (tabs.SelectedIndex == 0 && posts.IsEffectivelyVisible && posts.Bounds.Width > 0
            && posts.TranslatePoint(default, surface) is Point postPosition)
            right = layout.RightInset(surface.Bounds.Width, postPosition.X + posts.Bounds.Width);
        Control<WrapPanel>("ReaderNavigation").Margin = new Thickness(0, 0, right, bottom);
    }

    private void UpdateReaderPostTailSpace()
    {
        ListBox posts = Control<ListBox>("ArchivePosts");
        Control? tail = posts.ItemCount > 0 ? posts.ContainerFromIndex(posts.ItemCount - 1) : null;
        _readerPostTailSpace.Update(tail, _readerPostTailInset);
    }

    private async void OnReaderNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        await (ReaderNavigationTask = NavigateReaderAsync(button.Name switch { "ReaderPreviousPost" => -1, "ReaderNextPost" => 1, _ => 0 },
            button.Name switch { "ReaderPreviousThread" => -1, "ReaderNextThread" => 1, _ => 0 }));
    }

    private async void OnReaderKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || !_largeReader || IsReaderDialogOpen) return;
        if (e.Source is Visual source)
        {
            if (source.GetSelfAndVisualAncestors().Any(control => control is TextBox or ComboBox or NumericUpDown or MenuItem or Slider or ScrollBar)) return;
            if (source.GetVisualAncestors().Prepend(source).OfType<SelectableTextBlock>()
                .Any(text => text.SelectionStart != text.SelectionEnd)) return;
        }
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (CommandBindings.Matches(e, "export.open-html-dialog") && _thread != null)
        {
            if (_busy || _ended) return;
            e.Handled = true;
            await OpenExportForThreadsAsync(new[] { _thread.Summary.Id }, e.Source as Control);
            return;
        }
        if (CommandBindings.Matches(e, "gallery.open") && _thread != null && OpenGallery != null)
        {
            if (_busy || _ended) return;
            // Buttons and native editors retain their activation gestures.
            if (e.Source is Visual visual && visual.GetSelfAndVisualAncestors().Any(node => node is Button or Slider or TabItem)) return;
            e.Handled = true;
            await OpenGallery(_thread.Summary.Id, null, e.Source as Control);
            return;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None || CommandBindings.Matches(e, "reader.close")) { e.Handled = true; CloseReaderRequested?.Invoke(); return; }
        if (ReaderArrowBelongsToNativeControl(e.Source, e.Key)) return;
        // The reader guard deliberately permits its post list and tab content.
        // A generic native-input check would also reject Home/End/Page remaps
        // from ordinary tab content because TabControl is a SelectingItemsControl.
        // The local guard retains those keys in the actual post list and headers.
        // Enter/Space still use the generic activation guard for buttons etc.
        bool MatchesNavigation(string id) => e.Key is Key.Up or Key.Down or Key.Left or Key.Right
            or Key.Home or Key.End or Key.PageUp or Key.PageDown
            ? ShortcutService.Current.Matches(e, id) : CommandBindings.Matches(e, id);
        int post = MatchesNavigation("reader.previous-post") ? -1 : MatchesNavigation("reader.next-post") ? 1 : 0;
        int thread = MatchesNavigation("reader.previous-thread") ? -1 : MatchesNavigation("reader.next-thread") ? 1 : 0;
        if (post == 0 && thread == 0) return;
        e.Handled = true;
        await (ReaderNavigationTask = NavigateReaderAsync(post, thread));
    }

    internal bool ReaderArrowBelongsToNativeControl(object? source, Key key = Key.Down)
    {
        if (source is not Visual visual) return false;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, Control<ListBox>("ArchivePosts")))
                return key is Key.Home or Key.End or Key.PageUp or Key.PageDown;
            if (node is TabControl && ReferenceEquals(node, visual)) return true;
            if (node is TabItem or TabStrip or DataGrid or DataGridColumnHeader or Slider or ScrollBar
                or TextBox or ComboBox or NumericUpDown or MenuItem or Ursa.Controls.AutoCompleteBox
                || node is SelectingItemsControl and not TabControl) return true;
        }
        return false;
    }

    internal async Task NavigateReaderAsync(int postDelta, int threadDelta)
    {
        if (!_largeReader || _busy || _ended) return;
        ++_readerInteractionRevision;
        if (threadDelta != 0)
        {
            // URSA-12 shared navigation hook; only explicitly skipped missing members are omitted.
            int index = _readerGroupScope ? ReaderGroupTargetIndex(threadDelta) : ReaderThreadIndex + threadDelta;
            if (ReaderThreadIndex < 0 || index < 0 || index >= _readerThreads.Count) return;
            ResourceId target = _readerThreads[index];
            if (_readerGroupScope)
            {
                _readerGroupFailedDirection = threadDelta;
                if (!await LoadReaderGroupThreadAsync(target)) return;
            }
            else await LoadThreadAsync(target);
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

    private Func<Task>? CaptureReaderCountLayoutCorrection()
    {
        Grid pane = Control<Grid>("ArchiveReaderPane");
        bool Presented() => pane.IsEffectivelyVisible && pane.IsEffectivelyEnabled
            && TopLevel.GetTopLevel(pane) is Window { IsVisible: true };
        if (!MayRefreshReaderNow || !Presented() || _thread == null) return null;
        ListBox posts = Control<ListBox>("ArchivePosts");
        if (posts.SelectedItem is not ArchivePostRow row || posts.ContainerFromItem(row) is not Control container)
            return null;
        ScrollViewer? scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        ScrollContentPresenter? viewport = scroll?.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
        if (viewport == null || container.TranslatePoint(default, viewport) is not Point anchor
            || anchor.Y >= viewport.Bounds.Height || anchor.Y + container.Bounds.Height <= 0) return null;
        ResourceId thread = _thread.Summary.Id;
        int content = _contentRequest, focus = _readerFocusRequest, interaction = _readerInteractionRevision;
        int layout = _readerLayoutRevision;
        long coverage = _readerCoverageRevision;
        bool Current() => MayRefreshReaderNow && Presented() && _thread?.Summary.Id == thread && content == _contentRequest
            && focus == _readerFocusRequest && interaction == _readerInteractionRevision
            && layout == _readerLayoutRevision && coverage == _readerCoverageRevision
            && ReferenceEquals(posts.SelectedItem, row);
        return async () =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Publishing counts swaps unknown reply controls for their final labels.
                // The virtualizer can then revise estimated heights after navigation has
                // completed. Preserve the visible target without changing selection/focus.
                for (int pass = 0; pass < 4 && Current(); pass++)
                {
                    posts.UpdateLayout();
                    if (!Current() || posts.ContainerFromItem(row) is not Control target
                        || target.TranslatePoint(default, viewport) is not Point position) return;
                    double offset = Math.Clamp(scroll!.Offset.Y + position.Y - anchor.Y, 0,
                        Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                    if (Math.Abs(offset - scroll.Offset.Y) <= 1) return;
                    scroll.Offset = new Vector(scroll.Offset.X, offset);
                }
                if (Current()) posts.UpdateLayout();
            }, DispatcherPriority.Background);
        };
    }

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

    private async Task RestoreReaderAnchorAsync(ReaderAnchor anchor, bool restoreFocus = true,
        ReaderFocusIntent? focusIntent = null)
    {
        if (_ended || _thread?.Summary.Id != anchor.Thread) return;
        if (_thread.FindPostIndex(anchor.PostId) < 0)
        {
            string? fallback = _thread.GetPosts(Math.Min(_postOffset, Math.Max(0, _thread.PostCount - 1)), 1).FirstOrDefault()?.PostId;
            Message(Texts.T(fallback == null ? "archive.noPosts" : "reader.anchorUnavailable"));
            if (fallback != null) await FocusPostAsync(fallback, _readerInteractionRevision, restoreFocus, focusIntent);
            return;
        }
        int request = _contentRequest;
        int interactionRevision = _readerInteractionRevision;
        int focusRequest = _readerFocusRequest + 1;
        ListBox list = Control<ListBox>("ArchivePosts");
        // RenderThread replaced the virtualizing panel. Realize its new index
        // space from the origin before ScrollIntoView uses the retained target;
        // the predecessor's offset can otherwise leave negative item positions
        // at offset zero, where the final anchor delta cannot be applied.
        ScrollViewer? initialScroll = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        ++_readerLayoutRevision;
        if (initialScroll != null) initialScroll.Offset = new Vector(initialScroll.Offset.X, 0);
        list.UpdateLayout();
        if (_ended || request != _contentRequest || focusRequest != _readerFocusRequest + 1
            || interactionRevision != _readerInteractionRevision || _thread?.Summary.Id != anchor.Thread) return;
        await FocusPostAsync(anchor.PostId, interactionRevision, restoreFocus, focusIntent);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_ended || request != _contentRequest || focusRequest != _readerFocusRequest
                || interactionRevision != _readerInteractionRevision || _thread?.Summary.Id != anchor.Thread) return;
            ++_readerLayoutRevision;
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
