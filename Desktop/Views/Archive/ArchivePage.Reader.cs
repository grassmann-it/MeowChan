using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Gallery;
using System.IO;
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
    internal bool IsReaderDialogOpen => IsExternalLinkDialogOpen || IsFilenameSearchOpen || _urlTextExportBusy || ArchiveExportDialog.IsVisible || _cleanupDialog != null;
    internal bool TryDismissOverlay(object? source = null)
    {
        if (IsExternalLinkDialogOpen) { CloseExternalLinkDialog(); return true; }
        if (IsFilenameSearchOpen) { InvalidateFilenameSearch(); return true; }
        if (_urlTextExportBusy) return true; // Never route URLtext dismissal into HTML state.
        if (_cleanupDialog != null) { _cleanupDialog.RequestClose(); return true; }
        if (IsReaderDialogOpen)
        {
            OnExportClose(this, new RoutedEventArgs());
            return true;
        }
        // Shell tunnel reaches this before local handlers. Keep modal priority above
        // this strictly source-scoped dismissal; never treat Find as a global Escape.
        if (!_ended && IsQuotePreviewOpen)
        {
            CloseQuotePreview();
            return true;
        }
        if (!_ended && ReaderFindBar.IsVisible && IsThreadFindSource(source))
        {
            CloseThreadFind();
            return true;
        }
        if (!_ended && ReaderTagBar.IsVisible && IsTagBarSource(source))
        {
            CloseTagBar(focusOpen: true);
            return true;
        }
        return false;
    }
    private void OnReaderFullscreenClick(object? sender, RoutedEventArgs e) { if (!IsFilenameSearchOpen) FullscreenRequested?.Invoke(); }
    private double _readingFontSizePreference = 15;
    internal double ReadingFontSizePreference
    {
        get => _readingFontSizePreference;
        set { _readingFontSizePreference = value; ArchivePosts.FontSize = value; }
    }
    // "Fit": a post card is as wide as its content, left-aligned, up to ReaderNavigationLayout.CardMaxWidth (a style on the
    // list sets alignment and maximum on the containers, so a change is one class switch and costs no extra layout pass per
    // scroll). "Column": cards stretch up to the same cap and the column is centred. "Full": cards use the whole list.
    private string _readerPostWidthPreference = Config.ReaderPostWidthFit;
    internal string ReaderPostWidthPreference
    {
        get => _readerPostWidthPreference;
        set
        {
            string width = value is Config.ReaderPostWidthFull or Config.ReaderPostWidthColumn ? value : Config.ReaderPostWidthFit;
            _readerPostWidthPreference = width;
            ArchivePosts.Classes.Set("reader-fit-width", width == Config.ReaderPostWidthFit);
            ArchivePosts.Classes.Set("reader-column-width", width == Config.ReaderPostWidthColumn);
        }
    }
    private bool _largeReader;
    private IReadOnlyList<ResourceId> _readerThreads = Array.Empty<ResourceId>();
    private int _readerScopeRequest;
    private string? _readerPostId;
    private int _readerFocusRequest;
    private int _readerInteractionRevision;
    private int _readerLayoutRevision;
    private IDisposable? _readerTabScope;
    private readonly HashSet<Key> _readerMediaHeldKeys = new();
    private CancellationTokenSource? _readerMediaActivationCancellation;
    private Window? _readerMediaKeyOwner;
    internal Task ReaderMediaActivationTask { get; private set; } = Task.CompletedTask;
    // The existing OpenGallery callback can snapshot this during handoff and
    // recheck after its own cleanup await, before committing gallery coverage.
    internal Func<bool>? ReaderMediaActivationGuard { get; private set; }
    // Controlled await boundary for routed/native smoke; production adds no delay.
    internal Func<Task>? BeforeReaderMediaActivationAsync { get; set; }
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
        foreach (Control media in new Control[] { ArchiveMediaTiles, ArchiveMediaGrid })
        {
            CommandBindings.BindExisting(media, "media.open");
            media.AddHandler(KeyDownEvent, OnMediaKeyDown, RoutingStrategies.Tunnel);
        }
        ReaderContentSurface.LayoutUpdated += (_, _) => UpdateReaderNavigationLayout();
        // The cross floats over the content and hides while the pointer is idle (see ReaderNavigationLayout).
        ReaderContentSurface.AddHandler(PointerMovedEvent, OnReaderSurfacePointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        ReaderNavigation.PointerEntered += (_, _) => { _readerNavigationPointerOver = true; UpdateReaderNavigationShown(); };
        ReaderNavigation.PointerExited += (_, _) => { _readerNavigationPointerOver = false; UpdateReaderNavigationShown(); };
        ReaderNavigation.GotFocus += (_, _) => UpdateReaderNavigationShown();
        // Leaving the cross counts as activity: it stays for one more idle delay and then hides (the focus flag is still set inside this event).
        ReaderNavigation.LostFocus += (_, _) => ShowReaderNavigation();
        ReaderNavigation.DetachedFromVisualTree += (_, _) => _readerNavigationIdleTimer?.Stop();
        ListBox posts = ArchivePosts;
        posts.SelectionChanged += (_, _) =>
        {
            CancelReaderMediaActivation();
            if (posts.SelectedItem is ArchivePostRow selected) _readerPostId = selected.Id;
            UpdateReaderNavigation();
        };
        Grid pane = ArchiveReaderPane;
        pane.AttachedToVisualTree += (_, _) => ObserveReaderMediaKeys(pane);
        pane.DetachedFromVisualTree += (_, _) => { CloseThreadFind(); StopObservingReaderMediaKeys(); };
        pane.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == InputElement.IsEnabledProperty)
                CancelReaderMediaActivation();
        };
        ArchiveExportDialog.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty) CancelReaderMediaActivation();
        };
        ArchiveContentTabs.SelectionChanged += (_, _) => CancelReaderMediaActivation();
        posts.AddHandler(KeyDownEvent, (_, e) => TryRouteArchiveMediaShortcut(posts, e), RoutingStrategies.Bubble);
        posts.AddHandler(PointerWheelChangedEvent, (_, _) => ++_readerInteractionRevision, RoutingStrategies.Tunnel, handledEventsToo: true);
        posts.AddHandler(PointerPressedEvent, (_, _) => ++_readerInteractionRevision, RoutingStrategies.Tunnel, handledEventsToo: true);
        posts.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.PageDown or Key.PageUp or Key.Home or Key.End or Key.Space) ++_readerInteractionRevision;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }
    internal bool ReaderShowsPosts => _showReader && ArchiveContentTabs.SelectedIndex == 0;
    internal Action<Control?>? OpenReaderRequested { get; set; }
    internal Action? CloseReaderRequested { get; set; }
    private void OnCloseReaderClick(object? sender, RoutedEventArgs e) { if (IsFilenameSearchOpen) return; CloseThreadFind(); CloseReaderRequested?.Invoke(); }

    internal Control DetachReader()
    {
        InvalidateFilenameSearch();
        ++_readerFocusRequest;
        Grid pane = ArchiveReaderPane;
        ArchiveSplit.Children.Remove(pane);
        Border export = ArchiveExportDialog;
        ArchiveRoot.Children.Remove(export);
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
        ArchiveCloseReader.IsVisible = true;
        ArchiveReadPosts.IsVisible = false;
        UpdateReaderNavigation();
        UpdateLayoutMode();
        if (_thread != null) RenderThread();
        return pane;
    }

    internal void AttachReader(Control pane)
    {
        InvalidateFilenameSearch();
        CloseThreadFind();
        StopObservingReaderMediaKeys();
        ++_readerFocusRequest;
        _largeReader = false;
        _readerTabScope?.Dispose();
        _readerTabScope = null;
        pane.ClearValue(ReaderPostLayout.LargeReaderProperty);
        Border export = ArchiveExportDialog;
        ((Grid)pane).Children.Remove(export);
        ArchiveRoot.Children.Add(export);
        ++_readerScopeRequest;
        _readerThreads = Array.Empty<ResourceId>();
        pane.RemoveHandler(KeyDownEvent, OnReaderKeyDown);
        pane.RemoveHandler(ContextRequestedEvent, OnArchiveMediaContextRequested);
        pane.Margin = default;
        ArchiveCloseReader.IsVisible = false;
        ArchiveReadPosts.IsVisible = true;
        ArchivePosts.MaxWidth = double.PositiveInfinity;
        UpdateReaderNavigation();
        ArchiveSplit.Children.Add(pane);
        UpdateLayoutMode();
        if (_thread != null) RenderThread();
    }

    // The callers outside the page are explicit jumps (gallery "Show in thread", media return): reveal, then focus.
    internal async Task FocusPostAsync(string postId)
    {
        await RevealPostAsync(postId);
        await FocusPostAsync(postId, null);
    }
    private async Task FocusPostAsync(string postId, int? interactionRevision, bool restoreFocus = true,
        ReaderFocusIntent? focusIntent = null)
    {
        // A live continuation can already be stale before its first dispatcher
        // yield. Validate before ShowPost changes the page or selection.
        if (_ended || IsFilenameSearchOpen || _thread?.FindPostIndex(postId) is not >= 0
            || (interactionRevision.HasValue && interactionRevision != _readerInteractionRevision)) return;
        if (_projectionReady is { } preparing)
        {
            // A view that is still being prepared has no rows yet. The identity never waits here.
            ResourceId? preparingThread = _thread?.Summary.Id;
            int preparingContent = _contentRequest;
            await preparing.Task;
            if (_ended || IsFilenameSearchOpen || preparingContent != _contentRequest || _thread?.Summary.Id != preparingThread
                || _thread?.FindPostIndex(postId) is not >= 0
                || (interactionRevision.HasValue && interactionRevision != _readerInteractionRevision)) return;
        }
        // A post this view hides is no target of its own; the nearest shown post is (explicit jumps reveal first).
        if (Projection is { } shownPosts && shownPosts.PositionOf(postId) < 0)
        {
            int nearest = shownPosts.NearestPosition(postId);
            if (nearest < 0) return;
            postId = shownPosts[nearest].Post.PostId;
        }
        int focusRequest = ++_readerFocusRequest;
        focusIntent?.BeginFocus(focusRequest);
        ResourceId? threadId = _thread?.Summary.Id;
        int request = _contentRequest;
        _readerPostId = postId;
        ShowPost(postId, null, reveal: false);
        UpdateReaderNavigation();
        ListBox posts = ArchivePosts;
        // A jump the user did not ask to restore ends as soon as they take over: a wheel turn, a press or a paging key
        // (each bumps the revision) must not be pulled back by the next pass.
        int interactionBaseline = interactionRevision ?? _readerInteractionRevision;
        bool IsCurrent() => !_ended && focusRequest == _readerFocusRequest && request == _contentRequest
            && _thread?.Summary.Id == threadId
            && interactionBaseline == _readerInteractionRevision;
        if (focusIntent != null && BeforePassiveReaderFocusAsync is { } beforeFocus) await beforeFocus();
        // ScrollIntoView realizes a virtualized target. Its measured height can
        // replace an estimate on the next layout; retry only while the request
        // remains current, and correct offsets only after layout has settled.
        // A position that is already right is confirmed by one more layout: the neighbours realized around a
        // freshly scrolled-to target (a new page, the last item of a list) can still move it afterwards.
        bool confirmed = false;
        double? requestedOffset = null;
        ArchivePostRow? targetRow = null;
        // Focus must not cause a second competing BringIntoView request.
        void Finish(Control? container)
        {
            if (restoreFocus && container != null)
            {
                if (focusIntent != null) focusIntent.Restore(container);
                else container.Focus();
            }
            ReaderPositionChanged(deliberate: !interactionRevision.HasValue);
        }
        for (int pass = 0; pass < 8; pass++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            if (!IsCurrent()) return;
            ArchivePostRow? row = posts.ItemsSource?.Cast<ArchivePostRow>().FirstOrDefault(post => post.Id == postId);
            if (row == null) return;
            targetRow = row;
            ++_readerLayoutRevision;
            posts.SelectedItem = row;
            posts.UpdateLayout();
            // An offset the virtualizer did not keep (it re-estimates the extent around the realized items) is not retried
            // blindly: bring the target into view again so it is realized where the viewport is.
            // The request is checked once: an offset that matched is settled, and a later deviation is not "lost" (it is the
            // layout's or the user's own doing).
            bool offsetLost = requestedOffset is double requested
                && Math.Abs((posts.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset.Y ?? requested) - requested) > 1;
            requestedOffset = null;
            if (pass == 0 || offsetLost || posts.ContainerFromItem(row) == null) posts.ScrollIntoView(row);
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
                requestedOffset = offset;
                confirmed = false;
                continue;
            }
            if (!confirmed && pass < 7)
            {
                confirmed = true;
                continue;
            }
            Finish(container);
            return;
        }
        // The passes ran out while the heights still moved (a very long thread with strongly varying heights): the target
        // is selected, so focus and the reading position follow it where it is now instead of staying at the old place.
        if (!IsCurrent()) return;
        Finish(targetRow != null ? posts.ContainerFromItem(targetRow) as Control : null);
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

    // The position of the reader's current post in the shown order (the thread index under the identity).
    private int ReaderPostIndex
    {
        get
        {
            if (_thread == null || Projection is not { } shown) return -1;
            string? selected = (ArchivePosts.SelectedItem as ArchivePostRow)?.Id;
            int retained = _readerPostId == null ? -1 : shown.PositionOf(_readerPostId);
            return shown.PositionOf(selected
                ?? (retained >= _postOffset && retained < _postOffset + ReaderPageSize ? _readerPostId : null)
                ?? (_postOffset < shown.Count ? shown[_postOffset].Post.PostId : null) ?? string.Empty);
        }
    }
    private int ReaderThreadIndex => _thread == null ? -1 : Array.IndexOf(_readerThreads.ToArray(), _thread.Summary.Id);

    private void UpdateReaderNavigation()
    {
        // The reader can be shown before its hidden ArchivePage is attached for the first time.
        if (!IsInitialized && !_largeReader) return;
        int post = ReaderPostIndex;
        int thread = ReaderThreadIndex;
        ReaderPreviousPost.IsEnabled = !_busy && !IsFilenameSearchOpen && post > 0;
        ReaderNextPost.IsEnabled = !_busy && !IsFilenameSearchOpen && post >= 0 && post + 1 < ShownPostCount;
        ReaderPreviousThread.IsEnabled = !_busy && !IsFilenameSearchOpen && thread > 0 && (!_readerGroupScope || ReaderGroupTargetIndex(-1) >= 0);
        ReaderNextThread.IsEnabled = !_busy && !IsFilenameSearchOpen && thread >= 0 && thread + 1 < _readerThreads.Count
            && (!_readerGroupScope || ReaderGroupTargetIndex(1) < _readerThreads.Count);
        bool crossWasVisible = ReaderNavigation.IsVisible;
        ReaderNavigation.IsVisible = _largeReader;
        if (_largeReader && !crossWasVisible) ShowReaderNavigation();
        UpdateReaderNavigationLayout();
    }

    private void UpdateReaderNavigationLayout()
    {
        TabControl tabs = ArchiveContentTabs;
        ReaderStatusLine.IsVisible = !_largeReader || tabs.SelectedIndex == 0;
        Grid surface = ReaderContentSurface;
        ListBox posts = ArchivePosts;
        // Content always uses the full width and height; the cross is an overlay that hides itself while idle.
        posts.Margin = default;
        UpdateReaderNavigationShown();
        tabs.Margin = default;
        ArchiveMediaSurface.Margin = default;
        ArchiveReferences.Margin = default;
        if (!_largeReader) return;
        Control footer = tabs.SelectedIndex switch
        {
            1 => ArchiveMediaFooter,
            2 => ArchiveReferencesNavigation,
            _ => ArchivePostNavigation
        };
        double bottom = 16;
        if (footer.IsEffectivelyVisible && footer.TranslatePoint(default, surface) is Point position)
            bottom = Math.Max(bottom, surface.Bounds.Height - position.Y + 8);
        ReaderNavigation.Margin = new Thickness(0, 0, ReaderNavigationLayout.RightInset, bottom);
    }

    private bool _readerNavigationPointerOver, _readerNavigationActive;
    private Point? _readerSurfacePointer;
    private long _readerNavigationLastActivity;
    private DispatcherTimer? _readerNavigationIdleTimer;
    private int _readerNavigationIdleDelayMs = ReaderNavigationLayout.IdleHideDelayMsDefault;

    /// <summary>Idle time before the cross fades out; clamped to its minimum and maximum.</summary>
    internal int ReaderNavigationIdleDelayMs
    {
        get => _readerNavigationIdleDelayMs;
        set => _readerNavigationIdleDelayMs = ReaderNavigationLayout.ClampIdleHideDelayMs(value);
    }

    /// <summary>True while the cross is shown: recent pointer movement, pointer over or near it, keyboard focus in it.</summary>
    internal bool ReaderNavigationShown => _largeReader && (_readerNavigationActive || _readerNavigationPointerOver
        || ReaderNavigation.IsKeyboardFocusWithin || PointerNearReaderNavigation());

    private bool PointerNearReaderNavigation() => _readerSurfacePointer is Point pointer
        && ReaderNavigation.Bounds.Inflate(ReaderNavigationLayout.NearMargin).Contains(pointer);

    private void OnReaderSurfacePointerMoved(object? sender, PointerEventArgs e) => NotifyReaderPointerMoved(e.GetPosition(ReaderContentSurface));

    /// <summary>Pointer movement over the reader surface (position in surface coordinates).</summary>
    internal void NotifyReaderPointerMoved(Point position)
    {
        _readerSurfacePointer = position;
        ShowReaderNavigation();
    }

    // Pointer activity (or a navigation shortcut) shows the cross and (re)arms the idle timer; the timer only re-checks the
    // elapsed time, so a stream of pointer moves costs one timestamp each.
    internal void ShowReaderNavigation()
    {
        if (!_largeReader) return;
        _readerNavigationLastActivity = Environment.TickCount64;
        bool wasActive = _readerNavigationActive;
        _readerNavigationActive = true;
        if (_readerNavigationIdleTimer == null)
        {
            _readerNavigationIdleTimer = new DispatcherTimer();
            _readerNavigationIdleTimer.Tick += (_, _) => OnReaderNavigationIdleTick();
        }
        if (!_readerNavigationIdleTimer.IsEnabled)
        {
            _readerNavigationIdleTimer.Interval = TimeSpan.FromMilliseconds(_readerNavigationIdleDelayMs);
            _readerNavigationIdleTimer.Start();
        }
        if (!wasActive) UpdateReaderNavigationShown();
    }

    private void OnReaderNavigationIdleTick()
    {
        double remaining = _readerNavigationIdleDelayMs - (Environment.TickCount64 - _readerNavigationLastActivity);
        if (remaining > 1)
        {
            _readerNavigationIdleTimer!.Interval = TimeSpan.FromMilliseconds(remaining);
            return;
        }
        _readerNavigationIdleTimer!.Stop();
        _readerNavigationActive = false;
        UpdateReaderNavigationShown();
    }

    // Hidden means transparent and not hit-tested (the fade comes from the reader-cross style and is skipped under reduced
    // motion); the buttons stay in the visual tree, so keyboard focus and automation still reach them and show the cross.
    private void UpdateReaderNavigationShown()
    {
        bool shown = ReaderNavigationShown;
        ReaderNavigation.Opacity = shown ? 1 : 0;
        ReaderNavigation.IsHitTestVisible = shown;
    }

    private async void OnReaderNavigationClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        await (ReaderNavigationTask = NavigateReaderAsync(button.Name switch { "ReaderPreviousPost" => -1, "ReaderNextPost" => 1, _ => 0 },
            button.Name switch { "ReaderPreviousThread" => -1, "ReaderNextThread" => 1, _ => 0 }));
    }

    private async void OnReaderKeyDown(object? sender, KeyEventArgs e)
    {
        if (IsThreadFindSource(e.Source)) return;
        if (e.Handled || ShortcutService.IsCapturing) return;
        // The link confirmation is a dialog (IsReaderDialogOpen), but its Escape comes first and works in both readers.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && IsExternalLinkDialogOpen)
        {
            e.Handled = true;
            CloseExternalLinkDialog();
            return;
        }
        if (!_largeReader || IsReaderDialogOpen) return;
        // An open quote preview is dismissed first: Escape must not also close the reader.
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && IsQuotePreviewOpen)
        {
            e.Handled = true;
            CloseQuotePreview();
            return;
        }
        // Thread find is a Reader-only command (never a global find): it needs an open thread.
        if (_thread != null && CommandBindings.Matches(e, "reader.find"))
        {
            e.Handled = true;
            OnOpenThreadFind(null, e);
            return;
        }
        if (_thread != null && ReaderFindBar.IsVisible && TryMoveThreadFindByShortcut(e)) return;
        if (e.Source is Visual source)
        {
            if (source.GetSelfAndVisualAncestors().Any(control => control is TextBox or ComboBox or NumericUpDown or MenuItem or Slider or ScrollBar)) return;
            if (source.GetVisualAncestors().Prepend(source).OfType<SelectableTextBlock>()
                .Any(text => text.SelectionStart != text.SelectionEnd)) return;
        }
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (CommandBindings.Matches(e, "export.open-html-dialog") && _thread != null)
        {
            if (!_available || _busy || _ended || _lifetime.IsCancellationRequested) return;
            e.Handled = true;
            await OpenExportForThreadsAsync(new[] { _thread.Summary.Id }, e.Source as Control);
            return;
        }
        if (CommandBindings.Matches(e, "gallery.open") && _thread != null && OpenGallery != null)
        {
            if (!_available || _busy || _ended || _lifetime.IsCancellationRequested) return;
            // Buttons and native editors retain their activation gestures.
            if (e.Source is Visual visual && visual.GetSelfAndVisualAncestors().Any(node => node is Button or Slider or TabItem)) return;
            e.Handled = true;
            await OpenThreadGalleryAsync(_thread.Summary.Id, null, e.Source as Control);
            return;
        }
        if (TryActivateReaderMedia(e))
        {
            await ReaderMediaActivationTask;
            return;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None || CommandBindings.Matches(e, "reader.close")) { e.Handled = true; CloseReaderRequested?.Invoke(); return; }
        if (TryPageByShortcut(e)) return;
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

    // F3 / Ctrl+G (⌘G) and Shift+F3 / Ctrl+Shift+G (⇧⌘G): next and previous hit while the find bar is open,
    // also from inside the query box. Never opens the bar and never takes focus.
    private bool TryMoveThreadFindByShortcut(KeyEventArgs e)
    {
        bool forward = CommandBindings.Matches(e, "reader.find-next");
        if (!forward && !CommandBindings.Matches(e, "reader.find-previous")) return false;
        e.Handled = true;
        MoveThreadFind(forward);
        return true;
    }

    // The results-page gesture (Alt+PageUp/PageDown, Option-Command-arrows on macOS) also pages the thread's posts and the
    // backlinks inside the reader: pagination.previous/next share it and never apply where the results list has the focus.
    private bool TryPageByShortcut(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || _busy || _thread == null) return false;
        bool previous = ShortcutService.Current.Matches(e, "pagination.previous");
        if (!previous && !ShortcutService.Current.Matches(e, "pagination.next")) return false;
        bool references = PreviousReferences.IsEffectivelyVisible;
        Button button = references ? (previous ? PreviousReferences : NextReferences) : previous ? PreviousPostsButton : NextPostsButton;
        if (!button.IsEffectivelyVisible || !button.IsEnabled) return false;
        e.Handled = true;
        if (references) { if (previous) OnPreviousReferences(button, e); else OnNextReferences(button, e); }
        else if (previous) OnPreviousPosts(button, e);
        else OnNextPosts(button, e);
        return true;
    }

    // The beside-the-list preview has no reader tunnel: its post list passes the same gesture on from its own bubble.
    private void InitializePostPageKeys() => ArchivePosts.KeyDown += (_, e) =>
    {
        if (!_largeReader && !IsReaderDialogOpen) TryPageByShortcut(e);
    };

    private void CancelReaderMediaActivation() => _readerMediaActivationCancellation?.Cancel();

    private void ObserveReaderMediaKeys(Control pane)
    {
        StopObservingReaderMediaKeys();
        if (!_largeReader || TopLevel.GetTopLevel(pane) is not Window owner) return;
        _readerMediaKeyOwner = owner;
        // KeyUp may belong to the gallery that just covered the reader. Observe
        // release on this window, not globally, so a held key opens at most once.
        owner.AddHandler(KeyUpEvent, OnReaderMediaKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.Deactivated += OnReaderMediaOwnerDeactivated;
        owner.AddHandler(GotFocusEvent, OnReaderMediaFocusChanged, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    private void StopObservingReaderMediaKeys()
    {
        CancelReaderMediaActivation();
        _readerMediaHeldKeys.Clear();
        if (_readerMediaKeyOwner is not { } owner) return;
        owner.RemoveHandler(KeyUpEvent, OnReaderMediaKeyUp);
        owner.Deactivated -= OnReaderMediaOwnerDeactivated;
        owner.RemoveHandler(GotFocusEvent, OnReaderMediaFocusChanged);
        _readerMediaKeyOwner = null;
    }

    private void OnReaderMediaFocusChanged(object? sender, FocusChangedEventArgs e) => CancelReaderMediaActivation();
    private void OnReaderMediaKeyUp(object? sender, KeyEventArgs e) => _readerMediaHeldKeys.Remove(e.Key);
    private void OnReaderMediaOwnerDeactivated(object? sender, EventArgs e)
    {
        CancelReaderMediaActivation();
        _readerMediaHeldKeys.Clear();
    }

    // Fixed content activation is not a new global binding. Explicit effective
    // Reader/shell commands keep their meaning, including Enter/Space remaps.
    internal static bool IsReaderMediaGesture(KeyEventArgs e, EffectiveShortcutBindings bindings) =>
        e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None
        && !bindings.Gestures.Keys.Any(id => bindings.Matches(e, id)
            && EffectiveShortcutBindings.ExecutionContexts(id).Any(context => context is "reader" or "shell"));

    internal static bool ReaderMediaBelongsToNativeControl(Visual source, ListBox posts)
    {
        foreach (Visual node in source.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, posts)) return false;
            if (node is SelectableTextBlock text
                && (text is not ReaderBodyTextBlock || text.SelectionStart != text.SelectionEnd)) return true;
            if (node is Button or TextBox or ComboBox or NumericUpDown or MenuItem or Menu
                or Slider or ScrollBar or TabItem or TabStrip or DataGrid or Expander
                or Ursa.Controls.AutoCompleteBox or Ursa.Controls.ClosableTag or Ursa.Controls.Rating
                || node is SelectingItemsControl and not TabControl) return true;
        }
        return true; // Neither another tab nor the reader toolbar is post content.
    }

    private bool ReaderMediaContextIsCurrent(Control source, ArchivePostRow post)
    {
        ListBox posts = ArchivePosts;
        Control pane = ArchiveReaderPane;
        return !_ended && !_lifetime.IsCancellationRequested && !_busy && _available && _largeReader
            && ReaderShowsPosts && !_obscuredByGallery && !ArchiveAttachmentView.GetLoadingSuspended(this)
            && !IsReaderDialogOpen && !ShortcutService.IsCapturing && OpenGallery != null
            && pane.IsEffectivelyVisible && pane.IsEffectivelyEnabled
            && source.IsEffectivelyVisible && source.IsEffectivelyEnabled
            && CommandRouter.IsRoutingContextActive(pane, source)
            && TopLevel.GetTopLevel(pane) is Window owner && !owner.OwnedWindows.Any(window => window.IsVisible)
            && ReferenceEquals(posts.SelectedItem, post) && _thread?.FindPostIndex(post.Id) >= 0
            && !(posts.ContainerFromItem(post)?.GetVisualDescendants().OfType<SelectableTextBlock>()
                .Any(text => text.SelectionStart != text.SelectionEnd) ?? false)
            && !ReaderMediaBelongsToNativeControl(source, posts)
            // A stale/unselected realized row must not activate the selected row's media.
            && (ReferenceEquals(source, posts) || source.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
                .Any(item => ReferenceEquals(item.DataContext, post) && item.GetVisualAncestors().Contains(posts)));
    }

    private bool TryActivateReaderMedia(KeyEventArgs e)
    {
        // Enter or Space on a body with hidden spoilers shows them first (the body handles the key itself).
        if (e.Source is ReaderBodyTextBlock { HasHiddenSpoilers: true }) return false;
        if (e.Handled || e.Source is not Control source || !IsReaderMediaGesture(e, ShortcutService.Current)
            || ArchivePosts.SelectedItem is not ArchivePostRow post
            || !ReaderMediaContextIsCurrent(source, post)) return false;
        if (_readerMediaActivationCancellation != null || _readerMediaHeldKeys.Contains(e.Key))
        {
            _readerMediaHeldKeys.Add(e.Key);
            e.Handled = true;
            return true;
        }
        if (!post.HasActivatableMedia) return false;
        Task<IReadOnlyList<ArchiveAssetRow>> ready = _mediaReady.Task;
        ResourceId resource = _thread!.Summary.Id;
        if (ready.IsCompletedSuccessfully && FindReaderMediaTarget(resource, post, ready.Result) == null) return false;
        _readerMediaHeldKeys.Add(e.Key);
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _readerMediaActivationCancellation = cancellation;
        e.Handled = true;
        ReaderMediaActivationTask = ActivateReaderMediaAsync(source, post, resource, ready, cancellation);
        return true;
    }

    private async Task ActivateReaderMediaAsync(Control origin, ArchivePostRow post, ResourceId resource,
        Task<IReadOnlyList<ArchiveAssetRow>> ready, CancellationTokenSource cancellation)
    {
        int content = _contentRequest, media = _mediaRequest, focus = _readerFocusRequest;
        long coverage = _readerCoverageRevision, shortcuts = ShortcutService.Revision;
        var open = OpenGallery;
        bool Current() => !cancellation.IsCancellationRequested && content == _contentRequest
            && media == _mediaRequest && focus == _readerFocusRequest && coverage == _readerCoverageRevision
            && shortcuts == ShortcutService.Revision && ReferenceEquals(ready, _mediaReady.Task)
            && _thread?.Summary.Id == resource && ReferenceEquals(open, OpenGallery)
            && ReaderMediaContextIsCurrent(origin, post);
        try
        {
            // Await only the existing local manifest scan, never initiate a download.
            IReadOnlyList<ArchiveAssetRow> assets = await ready.WaitAsync(cancellation.Token);
            if (!Current()) return;
            if (BeforeReaderMediaActivationAsync is { } before) await before().WaitAsync(cancellation.Token);
            if (!Current()) return;
            // A stale manifest is not proof that an original still exists. Skip
            // missing originals in post order, not toward unrelated thread media.
            // The gallery retains byte integrity/decoding ownership after handoff.
            ReaderMediaTarget? target = await Task.Run(() => FindReaderMediaTarget(resource, post, assets,
                row => File.Exists(row.Asset.Path)), cancellation.Token);
            if (target == null || !Current()) return;
            ReaderMediaActivationGuard = Current;
            try { await open!(resource, target.Key, origin); }
            finally { ReaderMediaActivationGuard = null; }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (Current()) Message(exception.Message, true);
        }
        finally
        {
            if (ReferenceEquals(_readerMediaActivationCancellation, cancellation)) _readerMediaActivationCancellation = null;
            cancellation.Dispose();
        }
    }

    internal sealed record ReaderMediaTarget(ArchiveAssetRow Row, GalleryEntryKey Key);

    internal static ReaderMediaTarget? FindReaderMediaTarget(ResourceId resource, ArchivePostRow? post,
        IReadOnlyList<ArchiveAssetRow> assets, Func<ArchiveAssetRow, bool>? isAvailable = null)
    {
        if (post == null) return null;
        // Captured position is the existing preview association order. Shared
        // assets still get this selected post/position, never another post's key.
        foreach (ArchiveAttachmentRow row in post.Attachments.OrderBy(row => row.Attachment.Position))
        {
            CapturedAttachment attachment = row.Attachment;
            if (attachment.IsDeleted) continue;
            ArchiveAssetRow? match = FindMatchingAsset(resource, post.Id, attachment, assets);
            if (match == null || SavedMediaTypes.Classify(match.Asset.OriginalFileName ?? match.Asset.Path)
                is not (SavedMediaKind.Image or SavedMediaKind.Video)
                || isAvailable?.Invoke(match) == false) continue;
            return new(match, new GalleryEntryKey(new GalleryAssetKey(resource, match.Manifest.Directory, match.Asset.AssetId),
                post.Id, attachment.Position));
        }
        return null;
    }

    internal bool ReaderArrowBelongsToNativeControl(object? source, Key key = Key.Down)
    {
        if (source is not Visual visual) return false;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, ArchivePosts))
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
        if (!_largeReader || _busy || _ended || IsReaderDialogOpen) return;
        ShowReaderNavigation();
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
            await AwaitProjectionAsync();
            if (!_largeReader || _ended || _thread?.Summary.Id != target) return;
            string? first = Projection is { Count: > 0 } shown ? shown[0].Post.PostId : null;
            if (first != null) await FocusPostAsync(first);
            else ArchiveCloseReader.Focus();
        }
        else if (_thread != null && Projection is { } shown)
        {
            // Up/Down follow the shown order: hidden posts are skipped, a conversation goes through its tree.
            int index = ReaderPostIndex + postDelta;
            if (ReaderPostIndex < 0 || index < 0 || index >= shown.Count) return;
            await FocusPostAsync(shown[index].Post.PostId);
        }
        UpdateReaderNavigation();
    }

    private sealed record ReaderAnchor(ResourceId Thread, string PostId, double Top, string? SelectedPostId);

    private Func<Task>? CaptureReaderCountLayoutCorrection()
    {
        Grid pane = ArchiveReaderPane;
        bool Presented() => pane.IsEffectivelyVisible && pane.IsEffectivelyEnabled
            && TopLevel.GetTopLevel(pane) is Window { IsVisible: true };
        if (!MayRefreshReaderNow || !Presented() || _thread == null) return null;
        ListBox posts = ArchivePosts;
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
        ListBox list = ArchivePosts;
        string? selected = (list.SelectedItem as ArchivePostRow)?.Id;
        var visible = list.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(item => (Item: item, Top: item.TranslatePoint(default, list)?.Y ?? double.NaN))
            .Where(item => item.Item.DataContext is ArchivePostRow && item.Top < list.Bounds.Height && item.Top + item.Item.Bounds.Height > 0)
            .OrderBy(item => item.Top).FirstOrDefault();
        if (visible.Item?.DataContext is ArchivePostRow row)
            return new ReaderAnchor(_thread.Summary.Id, row.Id, visible.Top, selected);
        string? post = (list.SelectedItem as ArchivePostRow)?.Id
            ?? (Projection is { } shown ? (_postOffset < shown.Count ? shown[_postOffset].Post.PostId : null)
                : _thread.GetPosts(_postOffset, 1).FirstOrDefault()?.PostId);
        return post == null ? null : new ReaderAnchor(_thread.Summary.Id, post, 0, selected);
    }

    private async Task RestoreReaderAnchorAsync(ReaderAnchor anchor, bool restoreFocus = true,
        ReaderFocusIntent? focusIntent = null)
    {
        if (_ended || _thread?.Summary.Id != anchor.Thread) return;
        if (_projectionReady is { } preparing)
        {
            int preparingContent = _contentRequest;
            await preparing.Task;
            if (_ended || preparingContent != _contentRequest || _thread?.Summary.Id != anchor.Thread) return;
        }
        ReaderPostProjection? shown = Projection;
        if (_thread.FindPostIndex(anchor.PostId) < 0)
        {
            string? fallback = shown is { Count: > 0 } ? shown[Math.Min(_postOffset, shown.Count - 1)].Post.PostId : null;
            Message(Texts.T(fallback != null ? "reader.anchorUnavailable" : _thread.PostCount == 0 ? "archive.noPosts" : "reader.view.empty"));
            if (fallback != null) await FocusPostAsync(fallback, _readerInteractionRevision, restoreFocus, focusIntent);
            return;
        }
        // A retained place in a post this view hides moves to the nearest shown post (it is no explicit jump).
        if (shown != null && shown.PositionOf(anchor.PostId) < 0)
        {
            int nearest = shown.NearestPosition(anchor.PostId);
            if (nearest < 0) return;
            anchor = anchor with { PostId = shown[nearest].Post.PostId };
        }
        int request = _contentRequest;
        int interactionRevision = _readerInteractionRevision;
        int focusRequest = _readerFocusRequest + 1;
        ListBox list = ArchivePosts;
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
