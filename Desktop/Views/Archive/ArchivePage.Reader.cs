using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using MeowChan.Archive;
using MeowChan.Gallery;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
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
using MeowChan.Desktop.Controls;

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
        // Escape inside the media sidebar only leaves it (R8): the focus goes back to the selected post, before any reader-wide dismissal. Whether the
        // sidebar is on is a stored layout choice that the × button, the View menu and Primary+Shift+M change; Escape never switches it off (RD-01).
        if (!_ended && !IsReaderDialogOpen && IsMediaSidebarSource(source)) { FocusSelectedPostOrList(); return true; }
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
        if (!_ended && (ReaderFindBar.IsVisible || ReaderTagBar.IsVisible) && IsThreadToolsCloseSource(source))
        {
            CloseThreadTools();
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
    // The reader jumps (FocusPostAsync) own the list's offset while they align their target (ReaderScrollOwner).
    private readonly ReaderScrollOwner _readerScrollOwner = new();
    private bool ReaderJumpOwnsScroll => _readerScrollOwner.JumpOwns(_readerInteractionRevision);
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
            && !_owner.OwnedWindows.Any(window => window.IsVisible && !MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.IsPassive(window))
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
        InitializeReaderThreadKeys();
        foreach (Control media in new Control[] { ArchiveMediaTiles, ArchiveMediaGrid })
        {
            CommandBindings.BindExisting(media, "gallery.open");
            media.AddHandler(KeyDownEvent, OnMediaKeyDown, RoutingStrategies.Tunnel);
        }
        bool subscribed = false;
        void OnReaderNavigationLayoutUpdated(object? sender, EventArgs e) => UpdateReaderNavigationLayout();
        void SubscribeLayout(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
        {
            if (subscribed) return;
            ReaderContentSurface.LayoutUpdated += OnReaderNavigationLayoutUpdated;
            subscribed = true;
        }
        void UnsubscribeLayout(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
        {
            if (!subscribed) return;
            ReaderContentSurface.LayoutUpdated -= OnReaderNavigationLayoutUpdated;
            subscribed = false;
        }
        ReaderContentSurface.AttachedToVisualTree += SubscribeLayout;
        ReaderContentSurface.DetachedFromVisualTree += UnsubscribeLayout;
        // The navigation bar floats over the content and (mode "Auto") hides while the pointer is idle (see ReaderNavigationLayout).
        ReaderContentSurface.AddHandler(PointerMovedEvent, OnReaderSurfacePointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        TransientHover.Attach(ReaderNavigation, over => { _readerNavigationPointerOver = over; UpdateReaderNavigationShown(); });
        TransientHover.Attach(ReaderContentSurface, over =>
        {
            if (over) return;
            _readerSurfacePointer = null;
            _readerNavigationPointerOver = false;
            // A shortcut can scroll the posts and release hover while its reveal is still fresh.
            if (_readerNavigationActivityFromPointer || !TransientHover.WindowUsable(ReaderContentSurface))
            {
                _readerNavigationActive = false;
                _readerNavigationIdleTimer?.Stop();
            }
            UpdateReaderNavigationShown();
        });
        ReaderNavigation.GotFocus += (_, _) => UpdateReaderNavigationShown();
        // Leaving the bar counts as activity: it stays for one more idle delay and then hides (the focus flag is still set inside this event).
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
        ArchiveContentTabs.SelectionChanged += (_, _) => { CancelReaderMediaActivation(); AlignPostsOnShow(); };
        // M / U / F on the selected post's medium (the shared resolver) first, then the media menu's other keys.
        // These are page-owned shortcuts, not list type-ahead: enter the route before a ListBoxItem can consume a letter.
        // Each resolver keeps native editors, text selection, popups and foreign top-levels out of the route.
        posts.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!CommandRouter.IsRoutingContextActive(posts, e.Source)) return;
            if (TryOpenFocusedReaderMedium(e)) return;
            // The embedded reader has no OnReaderKeyDown route. Playback must
            // precede the post list's typeahead there as it does in the large reader.
            // Its own native-control guard keeps buttons and media activators local.
            if (TryRouteReaderVideoKey(e)) return;
            if (ReaderSelectionOwnsActivationKey(e)) return;
            if (!TryRouteFavoriteKey(posts, e)) TryRouteArchiveMediaShortcut(posts, e);
        }, RoutingStrategies.Tunnel);
        posts.AddHandler(PointerWheelChangedEvent, (_, _) => ++_readerInteractionRevision, RoutingStrategies.Tunnel, handledEventsToo: true);
        posts.AddHandler(PointerPressedEvent, (_, _) => ++_readerInteractionRevision, RoutingStrategies.Tunnel, handledEventsToo: true);
        posts.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key is Key.PageDown or Key.PageUp or Key.Home or Key.End or Key.Space) ++_readerInteractionRevision;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // The theme's list template does not pass ScrollViewer.BringIntoViewOnFocusChange (set on the list in the XAML) on to its
        // scroll viewer: focusing a card must not scroll the list (FocusPostAsync aligns it), so it is set on the parts themselves.
        posts.TemplateApplied += (_, _) => KeepFocusFromScrolling(posts);
        KeepFocusFromScrolling(posts);
        // The automatic scroll stays off for jumps and the pointer; a control inside a realized card that the keyboard focuses
        // (Tab to a link below the view) is brought in explicitly, so keyboard users never lose sight of the focus.
        posts.AddHandler(GotFocusEvent, OnReaderPostsGotFocus, RoutingStrategies.Bubble);
    }

    private void OnReaderPostsGotFocus(object? sender, FocusChangedEventArgs e)
    {
        if (!_readerScrollOwner.FocusMayScroll(e.NavigationMethod, _readerInteractionRevision)
            || e.Source is not Control source || source is ListBoxItem) return;
        // The reader moves the list itself now: a jump or a pending re-fit stands back (they end at a revision change).
        ++_readerInteractionRevision;
        source.BringIntoView();
    }

    private static void KeepFocusFromScrolling(ListBox posts)
    {
        if (posts.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } scroll) return;
        scroll.BringIntoViewOnFocusChange = false;
        // A named handler: this runs for every template application of the list, and a lambda would add one more each time.
        scroll.TemplateApplied -= OnReaderScrollTemplateApplied;
        scroll.TemplateApplied += OnReaderScrollTemplateApplied;
        KeepPresenterFromScrolling(scroll);
    }

    private static void OnReaderScrollTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is ScrollViewer scroll) KeepPresenterFromScrolling(scroll);
    }

    private static void KeepPresenterFromScrolling(ScrollViewer scroll)
    {
        if (scroll.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault() is { } presenter)
            ScrollViewer.SetBringIntoViewOnFocusChange(presenter, false);
    }
    internal bool ReaderShowsPosts => _showReader && ArchiveContentTabs.SelectedIndex == 0;
    // The Media tab is shown: the posts list is not on screen, but paging and the post keys still move through the posts (the current post, the page,
    // the reading position); the tab and the focus stay where they are, and the list is aligned to the current post when the user switches back.
    internal bool ReaderPostsInBackground => _showReader && ArchiveContentTabs.SelectedIndex == 1;
    /// <summary>The reader's current post (the identity the position, the counters and the next step start from); null without a page of posts.</summary>
    internal string? CurrentReaderPostId => _readerPostId;
    // The post the posts list has to be aligned to on its next appearance (a background move selected it while the list was hidden).
    private string? _postsAlignOnShow;
    private void AlignPostsOnShow()
    {
        if (_postsAlignOnShow is not { } post || _ended || !ReaderShowsPosts) return;
        _postsAlignOnShow = null;
        int request = _readerFocusRequest;
        Dispatcher.UIThread.Post(() =>
        {
            // The same alignment as a jump, without taking the focus from the tab header the user just used. A jump of any kind in between
            // (it bumps the request) has its own target and wins.
            if (!_ended && ReaderShowsPosts && request == _readerFocusRequest && _thread?.FindPostIndex(post) >= 0)
                OpenActions.Start(() => FocusPostAsync(post, null, restoreFocus: false), "archive.reader.alignPosts");
        }, DispatcherPriority.Loaded);
    }
    internal Action<Control?>? OpenReaderRequested { get; set; }
    internal Action? CloseReaderRequested { get; set; }
    private void OnCloseReaderClick(object? sender, RoutedEventArgs e) { if (IsFilenameSearchOpen) return; CloseThreadFind(); CloseReaderRequested?.Invoke(); }

    // The large reader's pane lives in the shell's ReaderHost, outside this page: everything the page itself listens to for the reader
    // (events bubbling up past the pane, the Menu key and the media menu) has to be on the pane while it is there. One place wires
    // both, so a handler added to the page for the reader cannot be forgotten for the large reader (a test pins this list against
    // the page's own AddHandler calls).
    private void WireReaderSurface(Control pane)
    {
        pane.AddHandler(KeyDownEvent, OnReaderKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
        ReaderHostingStep("wire.01");
        pane.AddHandler(ContextRequestedEvent, OnArchiveMediaContextRequested, RoutingStrategies.Tunnel);
        ReaderHostingStep("wire.02");
        pane.AddHandler(KeyDownEvent, OnArchiveMediaContextKey, RoutingStrategies.Tunnel);
        ReaderHostingStep("wire.03");
        pane.AddHandler(ArchiveMediaTileView.RateAndTagRequestedEvent, OnTileRateAndTagRequested);
        ReaderHostingStep("wire.04");
        // Keys inside the pane run in the "reader" context: the page's own host only sees them while the pane is inside the page, and the large reader's
        // pane is not. Installed after the handlers above on purpose: Avalonia runs the Tunnel handlers of one element in reverse registration order,
        // so the context is entered before OnReaderKeyDown and the media context key run (KeyContextHostTests pins both facts).
        _readerKeyContext?.Dispose();
        ReaderHostingStep("wire.05");
        _readerKeyContext = MeowChan.Desktop.Controls.KeyContextHost.Install(pane, _ => "reader");
        ReaderHostingStep("wire.06");
    }

    private IDisposable? _readerKeyContext;

    private void UnwireReaderSurface(Control pane)
    {
        _readerKeyContext?.Dispose();
        _readerKeyContext = null;
        pane.RemoveHandler(KeyDownEvent, OnReaderKeyDown);
        pane.RemoveHandler(ContextRequestedEvent, OnArchiveMediaContextRequested);
        pane.RemoveHandler(KeyDownEvent, OnArchiveMediaContextKey);
        pane.RemoveHandler(ArchiveMediaTileView.RateAndTagRequestedEvent, OnTileRateAndTagRequested);
    }

    internal Control DetachReader()
    {
        Action rollback = CaptureReaderHostingRollback();
        try
        {
            _ = ReleaseInlineVideos(); // R10: a player never moves between hosts
            ReaderHostingStep("detach.01");
            InvalidateFilenameSearch();
            ReaderHostingStep("detach.02");
            ++_readerFocusRequest;
            ReaderHostingStep("detach.03");
            Grid pane = ArchiveReaderPane;
            ReaderHostingStep("detach.04");
            ArchiveSplit.Children.Remove(pane);
            ReaderHostingStep("detach.05");
            Border export = ArchiveExportDialog;
            ReaderHostingStep("detach.06");
            ArchiveRoot.Children.Remove(export);
            ReaderHostingStep("detach.07");
            pane.Children.Add(export);
            ReaderHostingStep("detach.08");
            _largeReader = true;
            ReaderHostingStep("detach.09");
            // A disposable override preserves the original value source (including
            // styles, bindings and an unset local value) when the pane is reattached.
            _readerTabScope?.Dispose();
            ReaderHostingStep("detach.10");
            _readerTabScope = pane.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Cycle, BindingPriority.Animation);
            ReaderHostingStep("detach.11");
            pane.SetValue(ReaderPostLayout.LargeReaderProperty, true);
            ReaderHostingStep("detach.12");
            WireReaderSurface(pane);
            ReaderHostingStep("detach.13");
            pane.Margin = new Thickness(16);
            ReaderHostingStep("detach.14");
            ArchiveCloseReader.IsVisible = true;
            ReaderHostingStep("detach.15");
            ArchiveReadPosts.IsVisible = false;
            ReaderHostingStep("detach.16");
            UpdateReaderNavigation();
            ReaderHostingStep("detach.17");
            UpdateLayoutMode();
            ReaderHostingStep("detach.18");
            if (_thread != null) RenderThread();
            ReaderHostingStep("detach.19");
            // R8: the large reader's content is the pane in a frame that can hold the media sidebar beside it.
            Control content = WrapLargeReader(pane);
            ReaderHostingStep("detach.wrapped");
            return content;
        }
        catch (Exception error)
        {
            Exception failure = RollBackReaderHosting(error, rollback);
            if (ReferenceEquals(failure, error)) throw;
            throw failure;
        }
    }

    internal void AttachReader(Control content)
    {
        _ = ReleaseInlineVideos(); // R10: the large reader closes
        Grid pane = UnwrapLargeReader(content);
        InvalidateFilenameSearch();
        CloseThreadFind();
        StopObservingReaderMediaKeys();
        ++_readerFocusRequest;
        _largeReader = false;
        _readerTabScope?.Dispose();
        _readerTabScope = null;
        pane.ClearValue(ReaderPostLayout.LargeReaderProperty);
        Border export = ArchiveExportDialog;
        pane.Children.Remove(export);
        ArchiveRoot.Children.Add(export);
        ++_readerScopeRequest;
        _readerThreads = Array.Empty<ResourceId>();
        UnwireReaderSurface(pane);
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
    // passiveOpen: the first-post focus of a reader that was only opened. Opening is no reading: it never writes a reading position
    // (the saved place stays the one "Continue reading" offers); only the user's own jumps and input do.
    private async Task FocusPostAsync(string postId, int? interactionRevision, bool restoreFocus = true,
        ReaderFocusIntent? focusIntent = null, bool passiveOpen = false, Func<bool>? stillRequested = null)
    {
        // A live continuation can already be stale before its first dispatcher
        // yield. Validate before ShowPost changes the page or selection.
        if (_ended || IsFilenameSearchOpen || stillRequested?.Invoke() == false || _thread?.FindPostIndex(postId) is not >= 0
            || (interactionRevision.HasValue && interactionRevision != _readerInteractionRevision)) return;
        if (_projectionReady is { } preparing)
        {
            // A view that is still being prepared has no rows yet. The identity never waits here.
            ResourceId? preparingThread = _thread?.Summary.Id;
            int preparingContent = _contentRequest;
            await preparing.Task;
            if (_ended || IsFilenameSearchOpen || stillRequested?.Invoke() == false || preparingContent != _contentRequest || _thread?.Summary.Id != preparingThread
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
        // This jump owns the scroll until it settled (or was superseded): ShowPost only selects, the selection anchor
        // (AnchorFocusedCard) and the frame re-fit (OnFocusedFrameChanged) stand back, and the passes below are the only offset writes.
        using ReaderScrollOwner.Jump jump = _readerScrollOwner.BeginJump(_readerInteractionRevision);
        ListBox posts = ArchivePosts;
        // A jump the user did not ask to restore ends as soon as they take over: a wheel turn, a press or a paging key
        // (each bumps the revision) must not be pulled back by the next pass.
        int interactionBaseline = interactionRevision ?? _readerInteractionRevision;
        bool IsCurrent() => !_ended && focusRequest == _readerFocusRequest && request == _contentRequest
            && _thread?.Summary.Id == threadId
            && interactionBaseline == _readerInteractionRevision && stillRequested?.Invoke() != false;
        if (focusIntent != null && BeforePassiveReaderFocusAsync is { } beforeFocus)
        {
            await beforeFocus();
            if (!IsCurrent()) return;
        }
        // The selection and the first alignment share one dispatcher turn: a layout (and a rendered frame) between them would show
        // the grown target card with the presenter's scroll anchoring applied (the old card is held, the target jumps out of view),
        // and the alignment below would then bring it back: a bounce. Here that layout runs inside UpdateLayout, unrendered.
        ShowPost(postId, null, reveal: false, scroll: false);
        UpdateReaderNavigation();
        // ScrollIntoView realizes a virtualized target. Its measured height can
        // replace an estimate on the next layout; retry only while the request
        // remains current, and correct offsets only after layout has settled.
        // A position that is already right is confirmed by one more layout: the neighbours realized around a
        // freshly scrolled-to target (a new page, the last item of a list) can still move it afterwards.
        bool confirmed = false;
        double? requestedOffset = null;
        ArchivePostRow? targetRow = null;
        // Focus must not cause a second competing BringIntoView request: the list's scroll viewer does not bring a focused card
        // into view (BringIntoViewOnFocusChange is off in the XAML), so the card is focused as soon as it is realized in pass 0 and
        // the focus ring moves with the selection; the alignment below stays the only scroll of the jump.
        void FocusTarget(Control container)
        {
            if (!restoreFocus || container.IsFocused || !IsCurrent()) return;
            if (focusIntent != null) focusIntent.Restore(container);
            else container.Focus();
        }
        void Finish(Control? container)
        {
            if (container != null) FocusTarget(container);
            // A later frame change of the aligned card holds it here (HoldFocusedCard) instead of scrolling it anew: its top edge, or
            // its centre while the enlarged focused media makes the jump centre the card. The window of that re-fit starts now: the
            // decoded pictures of a real thread arrive after the jump, not after the selection.
            RememberFocusedCardTop();
            _alignedCardItem = FocusedMediaEnlarged && container != null ? posts.SelectedItem : null;
            if (_alignedCardItem != null)
                _readerScrollOwner.OpenFitWindow(Environment.TickCount64, _readerInteractionRevision, FocusFitWindowMilliseconds);
            ReaderPositionChanged(deliberate: !interactionRevision.HasValue && !passiveOpen);
        }
        int syncPasses = 0;
        for (int pass = 0; pass < 8; pass++)
        {
            // Pass 0 runs in the turn of the selection (above); the later passes re-measure after the layouts that followed. A pass
            // that just moved the list continues in the same turn (a few times at most): the virtualizer's estimates around a far
            // target settle in the layouts of this turn, and no frame is rendered between a wrong estimate and its correction.
            if (pass > 0 && syncPasses == 0) await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            else if (syncPasses > 0) --syncPasses;
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
            // A target already realized near the viewport (the neighbour of a keyboard step) is aligned from where it really is: the
            // virtualizer's ScrollIntoView would first move it by estimated heights (off the viewport and back on a step up).
            if ((pass == 0 && !RealizedNearViewport(posts, row)) || offsetLost || posts.ContainerFromItem(row) == null) posts.ScrollIntoView(row);
            posts.UpdateLayout();
            if (posts.ContainerFromItem(row) is not Control container) continue;
            FocusTarget(container);
            ScrollViewer? scroll = posts.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            ScrollContentPresenter? viewport = scroll?.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault();
            if (viewport == null || container.TranslatePoint(default, viewport) is not Point position) continue;
            // The post that carries the reading line scrolls to just below the line, which sits above its card. With the focused media
            // enlarged the card is centred in the viewport instead (ReaderMediaFit.JumpOffset), and it stays centred when its size
            // changes late (HoldFocusedCard).
            double offset = ReaderMediaFit.JumpOffset(scroll!.Offset.Y, position.Y, container.Bounds.Height, scroll.Viewport.Height,
                scroll.Extent.Height, row.HasUnreadLine ? ReaderUnreadLineHost.Reach : 0, FocusedMediaEnlarged);
            if (Math.Abs(offset - scroll.Offset.Y) > 1)
            {
                scroll.Offset = new Vector(scroll.Offset.X, offset);
                // Laid out in this turn: the anchoring of the layout after the write also stays unrendered.
                posts.UpdateLayout();
                requestedOffset = offset;
                confirmed = false;
                syncPasses = pass < 3 ? 1 : 0;
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
    // The data side of a post jump, for a list that is not on screen (the Media tab is shown): the page, the selected and current post, the reading position
    // and the navigation state follow exactly as for FocusPostAsync; the layout passes and the focus do not (nothing is realized), and the list is aligned to the
    // post when the Posts tab is shown again (AlignPostsOnShow).
    private async Task MoveReaderPostInBackgroundAsync(string postId)
    {
        if (_ended || IsFilenameSearchOpen || _thread?.FindPostIndex(postId) is not >= 0) return;
        if (_projectionReady is { } preparing)
        {
            ResourceId? preparingThread = _thread?.Summary.Id;
            int preparingContent = _contentRequest, interaction = _readerInteractionRevision;
            await preparing.Task;
            if (_ended || IsFilenameSearchOpen || preparingContent != _contentRequest || _thread?.Summary.Id != preparingThread
                || _thread?.FindPostIndex(postId) is not >= 0 || interaction != _readerInteractionRevision) return;
        }
        if (Projection is not { } shownPosts) return;
        int nearest = shownPosts.NearestPosition(postId);
        if (nearest < 0) return;
        postId = shownPosts[nearest].Post.PostId;
        if (!ReaderPostsInBackground)
        {
            // The user switched tabs while the view was being prepared: it is an ordinary jump now.
            await FocusPostAsync(postId, null);
            return;
        }
        ++_readerFocusRequest; // supersedes a jump or a passive focus that is still settling
        ShowPost(postId, null, reveal: false, scroll: false, background: true);
        _readerPostId = postId;
        _postsAlignOnShow = postId;
        UpdateReaderNavigation();
        ReaderPositionChanged(deliberate: true);
    }

    // A realized container within one viewport height above or below the visible part of the list.
    private static bool RealizedNearViewport(ListBox posts, object row)
    {
        if (posts.ContainerFromItem(row) is not Control container
            || posts.GetVisualDescendants().OfType<ScrollContentPresenter>().FirstOrDefault() is not { Bounds.Height: > 0 } viewport
            || container.TranslatePoint(default, viewport) is not Point at) return false;
        double height = viewport.Bounds.Height;
        return at.Y + container.Bounds.Height > -height && at.Y < 2 * height;
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
        UpdateReaderNavigationTexts(post, thread);
        bool barWasVisible = ReaderNavigation.IsVisible;
        ReaderNavigation.IsVisible = _largeReader && _readerNavigationMode != Config.ReaderNavigationModeOff;
        if (ReaderNavigation.IsVisible && !barWasVisible) ShowReaderNavigation();
        UpdateReaderNavigationLayout();
    }

    // The counters ("3 / 27" threads, "12 / 405" posts) and their tooltips / accessible names; rebuilt only when a number or the language changed.
    private (int Post, int PostCount, int Thread, int ThreadCount, string Language) _readerNavigationTextKey = (-2, -2, -2, -2, string.Empty);

    private void UpdateReaderNavigationTexts(int post, int thread)
    {
        (int, int, int, int, string) key = (post, ShownPostCount, thread, _readerThreads.Count, Texts.Language);
        if (key == _readerNavigationTextKey) return;
        _readerNavigationTextKey = key;
        AutomationProperties.SetName(ReaderNavigationBar, Texts.T("reader.nav.name"));
        SetReaderCounter(ReaderThreadPosition, post: false, thread, _readerThreads.Count);
        SetReaderCounter(ReaderPostPosition, post: true, post, key.Item2);
    }

    private static void SetReaderCounter(TextBlock counter, bool post, int index, int count)
    {
        counter.Text = ReaderNavigationLayout.CounterText(index, count);
        string sentence = Texts.T(post ? "reader.nav.postPosition" : "reader.nav.threadPosition",
            index >= 0 && index < count ? (index + 1).ToString(System.Globalization.CultureInfo.CurrentCulture) : "\u2013",
            count.ToString(System.Globalization.CultureInfo.CurrentCulture));
        ToolTip.SetTip(counter, sentence);
        AutomationProperties.SetName(counter, sentence);
    }

    private void UpdateReaderNavigationLayout()
    {
        TabControl tabs = ArchiveContentTabs;
        ReaderStatusLine.IsVisible = !_largeReader || tabs.SelectedIndex == 0;
        Grid surface = ReaderContentSurface;
        ListBox posts = ArchivePosts;
        // Content always uses the full width and height; the navigation bar is an overlay that hides itself while idle.
        posts.Margin = default;
        UpdateReaderNavigationShown();
        tabs.Margin = default;
        ArchiveMediaSurface.Margin = default;
        ArchiveReferences.Margin = default;
        if (!_largeReader) return;
        // Compact form: arrows only. Below the layout break of the reader surface, or with the position display switched off.
        bool compact = ReaderNavigationLayout.IsCompact(surface.Bounds.Width, _readerNavigationShowPosition);
        ReaderThreadPosition.IsVisible = ReaderPostPosition.IsVisible = !compact;
        ReaderThreadGroup.Spacing = ReaderPostGroup.Spacing = compact ? 2 : 0;
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

    private bool _readerNavigationPointerOver, _readerNavigationActive, _readerNavigationActivityFromPointer;
    private string _readerNavigationMode = Config.ReaderNavigationModeAuto;
    private bool _readerNavigationShowPosition = true;
    private Point? _readerSurfacePointer;
    private long _readerNavigationLastActivity;
    private DispatcherTimer? _readerNavigationIdleTimer;
    private int _readerNavigationIdleDelayMs = ReaderNavigationLayout.IdleHideDelayMsDefault;

    /// <summary>Idle time before the bar fades out; clamped to its minimum and maximum.</summary>
    internal int ReaderNavigationIdleDelayMs
    {
        get => _readerNavigationIdleDelayMs;
        set => _readerNavigationIdleDelayMs = ReaderNavigationLayout.ClampIdleHideDelayMs(value);
    }

    /// <summary>"Auto" (hides while idle), "Always" or "Off" (<see cref="Config.ReaderNavigationMode"/>); anything else reads as "Auto".</summary>
    internal string ReaderNavigationMode
    {
        get => _readerNavigationMode;
        set
        {
            string mode = Config.NormalizeReaderNavigationMode(value);
            if (mode == _readerNavigationMode) return;
            _readerNavigationMode = mode;
            if (mode != Config.ReaderNavigationModeAuto) _readerNavigationIdleTimer?.Stop();
            UpdateReaderNavigation();
            // Back to "Auto": the bar counts the change as activity and hides after one idle delay.
            if (mode == Config.ReaderNavigationModeAuto) ShowReaderNavigation();
        }
    }

    /// <summary>Whether the counters are shown (<see cref="Config.ReaderNavigationShowPosition"/>); off forces the compact form.</summary>
    internal bool ReaderNavigationShowPosition
    {
        get => _readerNavigationShowPosition;
        set
        {
            if (value == _readerNavigationShowPosition) return;
            _readerNavigationShowPosition = value;
            UpdateReaderNavigationLayout();
        }
    }

    /// <summary>True while the bar is shown: always in "Always", never in "Off"; in "Auto" with recent pointer movement, pointer over or near it, keyboard focus in it.</summary>
    internal bool IsLargeReader => _largeReader;
    internal string ReaderNavigationDebugState => $"large={_largeReader};mode={_readerNavigationMode};active={_readerNavigationActive};pointerOver={_readerNavigationPointerOver};visible={ReaderNavigation.IsVisible};focus={ReaderNavigation.IsKeyboardFocusWithin}";

    internal bool ReaderNavigationShown => _largeReader && _readerNavigationMode != Config.ReaderNavigationModeOff
        && (_readerNavigationMode == Config.ReaderNavigationModeAlways || _readerNavigationActive || _readerNavigationPointerOver
        || ReaderNavigation.IsKeyboardFocusWithin || PointerNearReaderNavigation())
        && (_readerNavigationMode == Config.ReaderNavigationModeAlways || TransientHover.WindowUsable(ReaderContentSurface));

    private bool PointerNearReaderNavigation()
    {
        if (_readerSurfacePointer is not Point pointer) return false;
        // Bounds are local to the navigation's parent. Convert the capsule origin to
        // ReaderContentSurface coordinates before comparing it with surface pointer input.
        Point? origin = ReaderNavigation.TranslatePoint(default, ReaderContentSurface);
        return origin is Point topLeft
            && new Rect(topLeft, ReaderNavigation.Bounds.Size)
                .Inflate(ReaderNavigationLayout.NearMargin)
                .Contains(pointer);
    }

    private void OnReaderSurfacePointerMoved(object? sender, PointerEventArgs e) => NotifyReaderPointerMoved(e.GetPosition(ReaderContentSurface));

    /// <summary>Pointer movement over the reader surface (position in surface coordinates).</summary>
    internal void NotifyReaderPointerMoved(Point position)
    {
        _readerSurfacePointer = position;
        // The smoke harness and non-native pointer paths report movement on the reader surface
        // without raising PointerExited on the capsule. Keep the hover flag in sync so a move
        // away from the capsule can actually arm auto-hide even when the hit host is present.
        _readerNavigationPointerOver = false;
        ShowReaderNavigation(fromPointer: true);
    }

    // Pointer activity (or a navigation shortcut) shows the bar and (re)arms the idle timer; the timer only re-checks the
    // elapsed time, so a stream of pointer moves costs one timestamp each. "Always" and "Off" need no timer.
    internal void ShowReaderNavigation(bool fromPointer = false)
    {
        if (!_largeReader || _readerNavigationMode != Config.ReaderNavigationModeAuto) return;
        _readerNavigationLastActivity = Environment.TickCount64;
        _readerNavigationActivityFromPointer = fromPointer;
        _readerNavigationActive = true;
        if (_readerNavigationIdleTimer == null)
        {
            _readerNavigationIdleTimer = new DispatcherTimer();
            _readerNavigationIdleTimer.Tick += (_, _) => OnReaderNavigationIdleTick();
        }
        // A tab/layout update can hide the capsule while activity is still valid.
        // Pointer/keyboard activity is an explicit request to show it again; do not
        // leave the overlay collapsed until a later layout pass happens to restore it.
        if (!ReaderNavigation.IsVisible) ReaderNavigation.IsVisible = true;
        // Keep a changed delay effective even when the existing timer is already running.
        // This matters for settings/smoke transitions that re-arm Auto without recreating it.
        // Restart rather than only changing the interval. A queued tick from an earlier,
        // shorter delay can otherwise fire immediately after a new pointer/keyboard reveal
        // and hide the capsule before the UI has observed the reveal.
        _readerNavigationIdleTimer.Stop();
        _readerNavigationIdleTimer.Interval = TimeSpan.FromMilliseconds(_readerNavigationIdleDelayMs);
        _readerNavigationIdleTimer.Start();
        // Refresh the hit-test state even when the timer was already armed. Layout/theme/tab
        // changes can hide the bar while the activity flag remains true; a fresh pointer move
        // or keyboard command must restore the whole capsule, including its gap surface.
        UpdateReaderNavigationShown();
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
    // motion); the buttons stay in the visual tree, so keyboard focus and automation still reach them and show the bar.
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
            if (KeyBelongsToNativeEditor(e)) return;
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
        if (CommandBindings.Matches(e, "rules.edit-scope") && _thread != null)
        {
            if (!_available || _busy || _ended || _lifetime.IsCancellationRequested) return;
            e.Handled = true;
            if (CanEditThreadRules) EditThreadRulesFromReader(); else Message(Texts.T("archive.command.noWatch"));
            return;
        }
        if (TryOpenFocusedReaderMedium(e)) return;
        if (!(e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None)
            && CommandBindings.Matches(e, "gallery.open") && _thread != null && OpenGallery != null)
        {
            if (!_available || _busy || _ended || _lifetime.IsCancellationRequested) return;
            // Buttons and native editors retain their activation gestures.
            if (e.Source is Visual visual && visual.GetSelfAndVisualAncestors().Any(node => node is Button or Slider or TabItem)) return;
            e.Handled = true;
            await OpenThreadGalleryAsync(_thread.Summary.Id, null, e.Source as Control);
            return;
        }
        // The selected post's video: Enter and the gallery's video keys (from the bindings) act on it before Enter or Space would open the gallery.
        if (TryRouteReaderVideoKey(e)) return;
        if (TryActivateReaderMedia(e))
        {
            await ReaderMediaActivationTask;
            return;
        }
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None || CommandBindings.Matches(e, "reader.close")) { e.Handled = true; CloseReaderRequested?.Invoke(); return; }
        if (TryPageByShortcut(e)) return;
        if (SidebarToggleGroup.IsSegmentArrow(e) || ReaderArrowBelongsToNativeControl(e.Source, e.Key)) return;
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

    /// <summary>
    /// The reader's navigation keys as the bindings give them (Up and Down between posts, Left and Right between threads), for a key pressed in
    /// a video's controls: the timeline slider would keep the arrows, so the video hands them over. True when the key moved the reader.
    /// </summary>
    internal bool TryNavigateReaderByKey(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || !_largeReader || _busy || _ended || IsReaderDialogOpen) return false;
        int post = ShortcutService.Current.Matches(e, "reader.previous-post") ? -1 : ShortcutService.Current.Matches(e, "reader.next-post") ? 1 : 0;
        int thread = ShortcutService.Current.Matches(e, "reader.previous-thread") ? -1 : ShortcutService.Current.Matches(e, "reader.next-thread") ? 1 : 0;
        if (post == 0 && thread == 0) return false;
        e.Handled = true;
        ReaderNavigationTask = NavigateReaderAsync(post, thread);
        return true;
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
    // It is decided from state (the selected tab, the offsets, the counts), never from whether a pager button is visible:
    // a collapsed pager row, a work view or another layout hides the buttons, not the command.
    private bool TryPageByShortcut(KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || _busy || _thread == null) return false;
        bool previous = ShortcutService.Current.Matches(e, "pagination.previous");
        if (!previous && !ShortcutService.Current.Matches(e, "pagination.next")) return false;
        bool readReady = CanRead && !_busy && !_ended && !IsFilenameSearchOpen;
        ReaderPageTarget target = ReaderPaging.Target(previous, ArchiveContentTabs.SelectedItem == ArchiveReferencesTab,
            readReady, _thread != null, _postOffset, ReaderPageSize, ShownPostCount, _backlinkPost != null, _referenceOffset, _referenceTotal, PageSize);
        if (target == ReaderPageTarget.None) return false;
        e.Handled = true;
        RoutedEventArgs click = new(Button.ClickEvent);
        if (target == ReaderPageTarget.References) { if (previous) OnPreviousReferences(this, click); else OnNextReferences(this, click); }
        else if (previous) OnPreviousPosts(this, click);
        else OnNextPosts(this, click);
        return true;
    }

    // The inline reader (and the beside-the-list preview) has no reader tunnel: the paging gesture is one tunnel handler on the content tabs, so it
    // reaches the posts list, the Media tile list and table and the References before their own key handling. A focused tile or row would
    // otherwise consume Alt+PageUp/PageDown (a ListBox and a DataGrid take PageUp/PageDown whatever the modifier). Paging is a reader command, not a
    // list key, as in the large reader (whose pane tunnel pages first); the keys the lists need (arrows, Home/End, plain and Shift/Ctrl PageUp/PageDown,
    // Space/Enter, Select All) are not among its default gestures. Native editors keep their keys (the media sort, the page-size field).
    private void InitializePostPageKeys() =>
        ArchiveContentTabs.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!_largeReader && !IsReaderDialogOpen && !KeyBelongsToNativeEditor(e)) TryPageByShortcut(e);
        }, RoutingStrategies.Tunnel);

    // Native editors and menus keep their keys in every reader: a key from a text box, a combo box, a number field, a menu item, a slider or a scroll bar.
    internal static bool KeyBelongsToNativeEditor(KeyEventArgs e) =>
        e.Source is Visual source && source.GetSelfAndVisualAncestors().Any(control => PointerFocusReclaim.IsTextInput(control) || control is MenuItem or Slider or ScrollBar);

    private void CancelReaderMediaActivation() => _readerMediaActivationCancellation?.Cancel();

    private void ObserveReaderMediaKeys(Control pane)
    {
        StopObservingReaderMediaKeys();
        if (TopLevel.GetTopLevel(pane) is not Window owner) return;
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

    // A key-up that goes to another top level (a menu, a flyout) never reaches the owner, so a focus change also ends every held key:
    // a latch must not outlive the focus it was set for. The activation in flight is cancelled with it.
    private void OnReaderMediaFocusChanged(object? sender, FocusChangedEventArgs e)
    {
        CancelReaderMediaActivation();
        _readerMediaHeldKeys.Clear();
    }
    private void OnReaderMediaKeyUp(object? sender, KeyEventArgs e) => _readerMediaHeldKeys.Remove(e.Key);
    private void OnReaderMediaOwnerDeactivated(object? sender, EventArgs e)
    {
        CancelReaderMediaActivation();
        _readerMediaHeldKeys.Clear();
    }

    // Fixed content activation is Space only: Enter is the command "Open medium of the post" (reader.media.activate, TryRouteReaderVideoKey), so
    // rebinding or unbinding it takes Enter away. Explicit effective Reader/shell commands keep their meaning, including Space remaps.
    internal static bool IsReaderMediaGesture(KeyEventArgs e, EffectiveShortcutBindings bindings) =>
        e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None
        && !bindings.Gestures.Keys.Any(id => bindings.Matches(e, id) && !ReaderVideoKeys.IsVideoCommand(id)
            && EffectiveShortcutBindings.ExecutionContexts(id).Any(context => context is "reader" or "shell"));

    internal static bool ReaderMediaBelongsToNativeControl(Visual source, ListBox posts)
    {
        foreach (Visual node in source.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, posts)) return false;
            if (node is SelectableTextBlock text
                && (text is not ReaderBodyTextBlock || text.SelectionStart != text.SelectionEnd)) return true;
            if (PointerFocusReclaim.IsTextInput(node) || node is Button or MenuItem or Menu
                or Slider or ScrollBar or TabItem or TabStrip or DataGrid or Expander
                or Ursa.Controls.ClosableTag or Ursa.Controls.Rating
                || node is SelectingItemsControl and not TabControl) return true;
        }
        return true; // Neither another tab nor the reader toolbar is post content.
    }

    private bool ReaderMediaContextIsCurrent(Control source, ArchivePostRow post, bool needsGallery = true, bool largeReaderOnly = true)
    {
        ListBox posts = ArchivePosts;
        Control pane = ArchiveReaderPane;
        return !_ended && !_lifetime.IsCancellationRequested && !_busy && _available && (!largeReaderOnly || _largeReader)
            && ReaderShowsPosts && !_obscuredByGallery && !ArchiveAttachmentView.GetLoadingSuspended(this)
            && !IsReaderDialogOpen && !ShortcutService.IsCapturing && (!needsGallery || OpenGallery != null)
            && pane.IsEffectivelyVisible && pane.IsEffectivelyEnabled
            && source.IsEffectivelyVisible && source.IsEffectivelyEnabled
            && CommandRouter.IsRoutingContextActive(pane, source)
            && TopLevel.GetTopLevel(pane) is Window owner && !owner.OwnedWindows.Any(window => window.IsVisible && !MeowChan.Desktop.Views.Shortcuts.ShortcutsWindow.IsPassive(window))
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

    // only: the one medium of the post a key chose (null: the first available medium of the post, in position order).
    private async Task ActivateReaderMediaAsync(Control origin, ArchivePostRow post, ResourceId resource,
        Task<IReadOnlyList<ArchiveAssetRow>> ready, CancellationTokenSource cancellation, ArchiveAttachmentRow? only = null, bool largeReaderOnly = true)
    {
        bool handedOff = false, refusalReported = false;
        void Refused(string reason = "reader.media.activation.contextChanged")
        {
            if (refusalReported) return;
            refusalReported = true;
            // The reader header's existing live status remains visible when the
            // large reader is detached; the archive-only overlay does not.
            if (!_ended) ShowReadingPositionMessage(Texts.T(reason));
        }
        int content = _contentRequest, media = _mediaRequest, focus = _readerFocusRequest;
        long coverage = _readerCoverageRevision, shortcuts = ShortcutService.Revision;
        var open = OpenGallery;
        bool Current() => !cancellation.IsCancellationRequested && content == _contentRequest
            && media == _mediaRequest && focus == _readerFocusRequest && coverage == _readerCoverageRevision
            && shortcuts == ShortcutService.Revision && ReferenceEquals(ready, _mediaReady.Task)
            && _thread?.Summary.Id == resource && ReferenceEquals(open, OpenGallery)
            && ReaderMediaContextIsCurrent(origin, post, largeReaderOnly: largeReaderOnly);
        bool GuardHandoff()
        {
            if (Current()) return true;
            Refused(); // The shell also rechecks the guard after its own awaits.
            return false;
        }
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
            ReaderMediaTarget? target = await Task.Run(() => only == null
                ? FindReaderMediaTarget(resource, post, assets, row => File.Exists(row.Asset.Path))
                : FindReaderMediumTarget(resource, post, only, assets, row => File.Exists(row.Asset.Path)), cancellation.Token);
            if (!Current()) return;
            if (target == null) { Refused("reader.media.activation.missing"); return; }
            // The routed Space event can originate at the posts ListBox itself. Resolve the
            // current realized selected row only now, after all awaits; never use a container
            // captured from another row. Keep the original source in Current() so selection,
            // cancellation, and stale-origin checks remain tied to the event that started this.
            Control? galleryOrigin = ResolveReaderMediaGalleryOrigin(origin, post);
            if (galleryOrigin == null || !Current()) return;
            ReaderMediaActivationGuard = GuardHandoff;
            try
            {
                await open!(resource, target.Key, galleryOrigin);
                handedOff = true;
            }
            finally { ReaderMediaActivationGuard = null; }
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            refusalReported = true;
            if (!_ended) ShowReadingPositionMessage(exception.Message, true);
        }
        finally
        {
            // Admission already consumed the key. A stale context, cancellation or
            // missing realized origin must not turn that into a silent no-op.
            if (!handedOff) Refused();
            if (ReferenceEquals(_readerMediaActivationCancellation, cancellation)) _readerMediaActivationCancellation = null;
            cancellation.Dispose();
        }
    }

    private Control? ResolveReaderMediaGalleryOrigin(Control source, ArchivePostRow post)
    {
        return ArchivePosts.ContainerFromItem(post) is Control selectedContainer
            ? ResolveReaderMediaGalleryOrigin(source, selectedContainer, ArchivePosts)
            : null;
    }

    // Kept separate from the handoff so the routed-event path can prove that a ListBox
    // source is translated to the selected row, while a source already inside that row
    // remains the focus-return origin.
    internal static Control? ResolveReaderMediaGalleryOrigin(Control source, Control selectedContainer, ListBox posts)
    {
        if (ReferenceEquals(source, posts)) return selectedContainer;
        return source.GetSelfAndVisualAncestors().Contains(selectedContainer) ? source : null;
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

    internal bool ReaderArrowBelongsToNativeControl(object? source, Key key = Key.Down) =>
        ReaderArrowBelongsToNativeControl(source, key, ArchivePosts);

    internal static bool ReaderArrowBelongsToNativeControl(object? source, Key key, ListBox posts)
    {
        if (source is not Visual visual) return false;
        foreach (Visual node in visual.GetSelfAndVisualAncestors())
        {
            if (ReferenceEquals(node, posts))
                return key is Key.Home or Key.End or Key.PageUp or Key.PageDown;
            if (node is TabControl && ReferenceEquals(node, visual)) return true;
            if (PointerFocusReclaim.IsTextInput(node) || node is TabItem or TabStrip or DataGrid or DataGridColumnHeader or Slider or ScrollBar
                or MenuItem
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
            // With the Media tab shown the move happens behind it: no tab switch, no focus change.
            if (ReaderPostsInBackground) await MoveReaderPostInBackgroundAsync(shown[index].Post.PostId);
            else await FocusPostAsync(shown[index].Post.PostId);
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

    /// <summary>The place the reader would retain now (the post and its offset in the list), for the checks of the anchor rules.</summary>
    internal (string PostId, double Top)? CurrentReaderAnchor => CaptureReaderAnchor() is { } anchor ? (anchor.PostId, anchor.Top) : null;

    private ReaderAnchor? CaptureReaderAnchor()
    {
        if (_thread == null) return null;
        ListBox list = ArchivePosts;
        string? selected = (list.SelectedItem as ArchivePostRow)?.Id;
        // The realized cards keep their scroll position behind another tab (Media, References) and stay the anchor there. They are stale only after
        // a background step or page is waiting for alignment (_postsAlignOnShow): then the anchor is the selected post or the page's first. A card
        // whose row is not in the current ItemsSource belongs to a page that was replaced behind the tab and is never the anchor.
        IEnumerable<ArchivePostRow>? current = list.ItemsSource as IEnumerable<ArchivePostRow>;
        var visible = _postsAlignOnShow != null ? default : list.GetVisualDescendants().OfType<ListBoxItem>()
            .Select(item => (Item: item, Top: item.TranslatePoint(default, list)?.Y ?? double.NaN))
            .Where(item => item.Item.DataContext is ArchivePostRow candidate && current?.Contains(candidate) == true
                && item.Top < list.Bounds.Height && item.Top + item.Item.Bounds.Height > 0)
            .OrderBy(item => item.Top).FirstOrDefault();
        if (visible.Item?.DataContext is ArchivePostRow row)
            return new ReaderAnchor(_thread.Summary.Id, row.Id, visible.Top, selected);
        string? post = (list.SelectedItem as ArchivePostRow)?.Id
            ?? (Projection is { } shown ? (_postOffset < shown.Count ? shown[_postOffset].Post.PostId : null)
                : _thread.GetPosts(_postOffset, 1).FirstOrDefault()?.PostId);
        return post == null ? null : new ReaderAnchor(_thread.Summary.Id, post, 0, selected);
    }

    private async Task RestoreReaderAnchorAsync(ReaderAnchor anchor, bool restoreFocus = true,
        ReaderFocusIntent? focusIntent = null, Func<bool>? stillRequested = null, Action? onRestored = null)
    {
        if (_ended || stillRequested?.Invoke() == false || _thread?.Summary.Id != anchor.Thread) return;
        if (_projectionReady is { } preparing)
        {
            Func<bool> preparingCurrent = CaptureReadingRequestGuard();
            await preparing.Task;
            if (!preparingCurrent() || stillRequested?.Invoke() == false || _thread?.Summary.Id != anchor.Thread) return;
        }
        ReaderPostProjection? shown = Projection;
        if (_thread.FindPostIndex(anchor.PostId) < 0)
        {
            string? fallback = shown is { Count: > 0 } ? shown[Math.Min(_postOffset, shown.Count - 1)].Post.PostId : null;
            Message(Texts.T(fallback != null ? "reader.anchorUnavailable" : _thread.PostCount == 0 ? "archive.noPosts" : "reader.view.empty"));
            if (fallback != null) await FocusPostAsync(fallback, _readerInteractionRevision, restoreFocus, focusIntent, stillRequested: stillRequested);
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
            || interactionRevision != _readerInteractionRevision || _thread?.Summary.Id != anchor.Thread
            || stillRequested?.Invoke() == false) return;
        await FocusPostAsync(anchor.PostId, interactionRevision, restoreFocus, focusIntent, stillRequested: stillRequested);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_ended || request != _contentRequest || focusRequest != _readerFocusRequest
                || interactionRevision != _readerInteractionRevision || _thread?.Summary.Id != anchor.Thread
                || stillRequested?.Invoke() == false) return;
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
            onRestored?.Invoke();
        }, DispatcherPriority.Loaded);
    }

}
