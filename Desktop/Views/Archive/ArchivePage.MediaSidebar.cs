using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Favorites;
using MeowChan.Desktop.MediaActions;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan;

// The media sidebar of the large reader (R8) and, when chosen ("Always"), of the archive view. In the large reader the detached reader
// pane sits in a frame grid with the sidebar and a splitter beside it (left or right); in the archive view the reader pane itself holds
// them beside its posts card (the header row spans all three columns). One sidebar moves between the two hosts, always while hidden. It
// follows the selected post (throttled while ↑/↓ is held), decodes through the gallery's decode API and cache (GalleryImageCache: no
// second decoder, no memory outside its budget; posters through the reader's ThumbnailDecodeGate), preloads the neighbouring posts in
// the background and announces the medium politely. While it is shown, it is the one surface that presents the focused media enlarged
// (FocusedMediaPresentedElsewhere), so the inline enlargement of the card steps back.
public partial class ArchivePage
{
    private Grid? _largeReaderFrame;
    private ReaderMediaSidebar? _mediaSidebar;
    private GridSplitter? _mediaSidebarSplitter;
    // Where the sidebar sits now: the large reader's frame or the archive view's reader pane (null until it was first needed).
    private Grid? _mediaSidebarHost;
    private bool _mediaSidebarShown, _mediaSidebarRight, _mediaSidebarDirty, _movingMediaSidebar;
    private double _mediaSidebarAppliedWidth = double.NaN;
    private int _paneSplitColumn = 2;
    private IDisposable? _frameTabScope;
    private bool _tabScopeOnFrame;
    private readonly ReaderMediaFollowThrottle _sidebarThrottle = new();
    private readonly Stopwatch _sidebarClock = Stopwatch.StartNew();
    private DispatcherTimer? _sidebarFollowTimer, _sidebarAnnounceTimer;
    private CancellationTokenSource? _sidebarLoading;
    private CancellationTokenSource? _sidebarStripLoading;
    // The view owns request identities and decoded sizes; the page keeps the shown picture's detail line for a same-medium refresh.
    private string? _sidebarPostKey;
    private int _sidebarIndex;
    private (ArchivedThreadView Thread, ArchivePostRow Post, ArchiveAttachmentRow Attachment)? _displayedSidebarMedium;
    private string _sidebarShownDetail = string.Empty;
    private string _sidebarAnnouncement = string.Empty;
    private GalleryFitPreferences _sidebarFit = new();

    /// <summary>The gallery's fit limit, which the sidebar's picture follows (it never enlarges more than the gallery's Fit would).</summary>
    internal GalleryFitPreferences ReaderMediaSidebarFit
    {
        get => _sidebarFit;
        set
        {
            if (_sidebarFit == value) return;
            _sidebarFit = value;
            if (_mediaSidebarShown) RefreshMediaSidebar();
        }
    }

    /// <summary>The sidebar view (smoke seam); null until the large reader was opened once or the archive view showed it.</summary>
    internal ReaderMediaSidebar? MediaSidebar => _mediaSidebar;
    /// <summary>Whether the sidebar is on screen now (switched on for this view, the reader wide enough).</summary>
    internal bool IsMediaSidebarShown => _mediaSidebarShown;
    /// <summary>Whether the sidebar sits in the archive view's reader pane (smoke seam; false in the large reader).</summary>
    internal bool IsMediaSidebarInArchiveView => _mediaSidebarHost != null && ReferenceEquals(_mediaSidebarHost, ArchiveReaderPane);
    /// <summary>The latest sidebar load (smoke seam).</summary>
    internal Task MediaSidebarTask { get; private set; } = Task.CompletedTask;
    /// <summary>The latest load of the strip's thumbnails (smoke seam).</summary>
    internal Task MediaSidebarStripTask { get; private set; } = Task.CompletedTask;
    /// <summary>The medium of the focused post the sidebar shows (0-based).</summary>
    internal int MediaSidebarIndex => _sidebarIndex;

    private void InitializeMediaSidebar()
    {
        RegisterViewOption("reader.sidebar.toggle", () => ReaderMediaSidebarRules.IsOn(ReaderMedia, _largeReader), ToggleMediaSidebar, note: MediaSidebarHint);
        ReaderMediaSidebarToggle.Click += (_, e) => { e.Handled = true; ToggleMediaSidebar(); RefreshMediaSidebarToggle(); };
        // The toolbar travels with the reader. Refresh on layout as well as preference changes:
        // host, dialog, language and shortcut changes must not leave a stale On/Off action.
        bool subscribed = false;
        void OnSidebarToggleLayoutUpdated(object? sender, EventArgs e) => RefreshMediaSidebarToggle();
        void SubscribeLayout(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
        {
            if (subscribed) return;
            ReaderMediaSidebarToggle.LayoutUpdated += OnSidebarToggleLayoutUpdated;
            subscribed = true;
        }
        void UnsubscribeLayout(object? sender, Avalonia.VisualTreeAttachmentEventArgs e)
        {
            if (!subscribed) return;
            ReaderMediaSidebarToggle.LayoutUpdated -= OnSidebarToggleLayoutUpdated;
            subscribed = false;
        }
        ReaderMediaSidebarToggle.AttachedToVisualTree += SubscribeLayout;
        ReaderMediaSidebarToggle.DetachedFromVisualTree += UnsubscribeLayout;
        RefreshMediaSidebarToggle();
        ArchivePosts.SelectionChanged += (_, _) =>
        {
            if (!ArchiveMediaTab.IsSelected) OnMediaSidebarSelectionChanged();
        };
        // The archive view: the pane gets posts, splitter and sidebar columns.
        // Reader chrome stays over the posts only; the sidebar owns its full-height
        // column with a stable title line. Hidden, it returns the column to the posts.
        Grid pane = ArchiveReaderPane;
        // The shell's generic native-input guard reserves arrows for ListBox. These two reader-owned
        // commands must run before the post list/strip, in both the embedded and detached hosts.
        pane.AddHandler(KeyDownEvent, OnMediaSidebarStepKey, RoutingStrategies.Tunnel);
        pane.ColumnDefinitions = new ColumnDefinitions("*,0,0");
        pane.SizeChanged += (_, _) => { if (!_largeReader) LayoutMediaSidebar(); };
        pane.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && !_largeReader) LayoutMediaSidebar(); };
    }

    // ---- hosts ---------------------------------------------------------------------------------------------------------------

    /// <summary>The large reader's content: the pane in the frame with the (possibly hidden) sidebar.</summary>
    private Control WrapLargeReader(Grid pane)
    {
        Grid frame = _largeReaderFrame ??= CreateLargeReaderFrame();
        if (_largeReader) ReaderHostingStep("wrap.01");
        // From the archive view's pane (hidden first); the pane's columns give the posts card its whole width back.
        MoveMediaSidebarTo(frame);
        if (_largeReader) ReaderHostingStep("wrap.02");
        _paneSplitColumn = Grid.GetColumn(pane); // the embedded reader's column in ArchiveSplit, restored on the way back
        if (_largeReader) ReaderHostingStep("wrap.03");
        frame.Children.Add(pane);
        if (_largeReader) ReaderHostingStep("wrap.04");
        _mediaSidebarAppliedWidth = double.NaN;
        if (_largeReader) ReaderHostingStep("wrap.05");
        LayoutMediaSidebar(force: true);
        if (_largeReader) ReaderHostingStep("wrap.06");
        // The move kept the picture for this host; a large reader that does not show the sidebar gives it back.
        if (!_mediaSidebarShown) ReleaseKeptMediaSidebarPicture();
        if (_largeReader) ReaderHostingStep("wrap.07");
        return frame;
    }

    /// <summary>Takes the pane out of the frame again; the sidebar gives its picture back (the archive view lays it out once the pane is back).</summary>
    private Grid UnwrapLargeReader(Control content)
    {
        Grid pane = ArchiveReaderPane;
        if (_largeReaderFrame is { } frame && ReferenceEquals(content, frame))
        {
            frame.Children.Remove(pane);
            Grid.SetColumn(pane, _paneSplitColumn);
            _frameTabScope?.Dispose();
            _frameTabScope = null;
            _tabScopeOnFrame = false;
            // The picture stays for the archive view, which shows the same medium at once if it shows the sidebar; otherwise it is given back then.
            if (_mediaSidebarShown) SetMediaSidebarShown(false, keepPicture: true);
            Dispatcher.UIThread.Post(() =>
            {
                if (!_ended && !_largeReader) LayoutMediaSidebar(force: true);
                if (!_mediaSidebarShown) ReleaseKeptMediaSidebarPicture();
            }, DispatcherPriority.Loaded);
        }
        return pane;
    }

    private Grid CreateLargeReaderFrame()
    {
        Grid frame = new() { Name = "ReaderLargeFrame", ColumnDefinitions = new ColumnDefinitions("0,0,*") };
        frame.SizeChanged += (_, _) => { if (_largeReader) LayoutMediaSidebar(); };
        return frame;
    }

    private ReaderMediaSidebar EnsureMediaSidebar()
    {
        if (_mediaSidebar is { } existing) return existing;
        // Publish only a fully configured sidebar: a failed reader move must
        // not leave an incomplete cached sidebar poisoning the next attempt.
        ReaderMediaSidebar sidebar = new ReaderMediaSidebar { IsVisible = false, DecisionKeyContext = "reader" };
        if (_largeReader) ReaderHostingStep("sidebar.created");
        GridSplitter splitter = new GridSplitter
        {
            Name = "ReaderMediaSidebarSplitter", Width = ReaderMediaSidebarRules.SplitterWidth,
            ResizeDirection = GridResizeDirection.Columns, IsVisible = false
        };
        Avalonia.Automation.AutomationProperties.SetName(splitter, Texts.T("reader.sidebar.splitter"));
        splitter.DragCompleted += (_, _) => SaveMediaSidebarWidth();
        splitter.KeyUp += (_, _) => SaveMediaSidebarWidth();
        sidebar.Frame.SizeChanged += (_, _) => OnMediaSidebarFrameResized();
        sidebar.OpenTagsAsync = OpenSidebarTagsAsync;
        sidebar.SaveInfoMode = mode =>
        {
            ApplyReaderMediaPreferences(ReaderMedia with { MediaSidebarInfo = mode });
            if (SaveReaderMediaSwitches is { } save)
                OpenActions.Start(() => save(config => config.MediaSidebarInfo = mode), "media.sidebar.info");
        };
        sidebar.Open.Click += (_, e) => { e.Handled = true; OpenMediaSidebarMedia(); };
        sidebar.StripSelected += (_, index) => SelectMediaSidebarIndex(index);
        sidebar.StripActivated += (_, index) => { SelectMediaSidebarIndex(index); OpenMediaSidebarMedia(); };
        sidebar.PictureActivated += (_, _) => OnMediaSidebarPictureActivated();
        sidebar.GalleryRequested += (_, _) => OpenMediaSidebarMedia();
        // The "No." of its header line copies the post's reference like the post's own number does.
        CreateSidebarVideo(sidebar);
        sidebar.Close.Click += (_, e) => { e.Handled = true; CloseMediaSidebar(); };
        // The sidebar's keys reach the reader's view shortcuts too (the toggle, the reader's other switches), in either host.
        // M / U / F act on the medium it shows (revision 20), like the stars of its header.
        // R, the rating keys and the media menu's keys act on it too (audit 16.11.2026: in the large reader they reached nothing from here).
        // The strip is a ListBox: its type-ahead can consume a letter before a bubble
        // handler reaches the sidebar. Route the page-owned routes in the tunnel,
        // while every resolver preserves native input and the context guard preserves
        // popups and foreign top-levels. The strip's Space/Enter selection remains
        // local; only the other playback bindings are lifted before type-ahead.
        sidebar.AddHandler(KeyDownEvent, OnMediaSidebarStepKey, RoutingStrategies.Tunnel);
        sidebar.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!ReaderSelectionOwnsActivationKey(e)) RouteSidebarRatingKey(e);
        }, RoutingStrategies.Tunnel);
        sidebar.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!CommandRouter.IsRoutingContextActive(sidebar, e.Source)) return;
            TryRouteSidebarSelectionShortcut(sidebar, e);
        }, RoutingStrategies.Tunnel);
        sidebar.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (!TryRouteSidebarVideoKey(e) && !TryRouteFavoriteKey(sidebar, e) && !RouteSidebarRatingKey(e) && !TryRouteArchiveMediaShortcut(sidebar, e))
                OnReaderViewKeyDown(sidebar, e);
        }, RoutingStrategies.Bubble);
        sidebar.AddHandler(KeyDownEvent, OnSidebarNavigationKey, RoutingStrategies.Bubble);
        // The large reader's frame is not under the page (the shell hosts it), so the page's and the pane's media menu never see the sidebar: it opens the same
        // menu itself, on a right click anywhere in it (its picture also while a video plays, the header line, the card's edge), the Menu key and Shift+F10.
        sidebar.AddHandler(ContextRequestedEvent, OnArchiveMediaContextRequested, RoutingStrategies.Tunnel);
        sidebar.AddHandler(PointerPressedEvent, OnSidebarPointerPressed, RoutingStrategies.Tunnel);
        sidebar.AddHandler(PointerReleasedEvent, OnSidebarPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        // The middle click on its picture: the medium's folder, as for the posts' thumbnails (the pane's handlers skip the sidebar's own subtree).
        sidebar.AddHandler(PointerPressedEvent, OnSidebarMiddlePressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        sidebar.AddHandler(PointerReleasedEvent, OnSidebarMiddleReleased, RoutingStrategies.Tunnel);
        sidebar.AddHandler(KeyDownEvent, OnArchiveMediaContextKey, RoutingStrategies.Tunnel);
        // Its keys run in the reader's context in either host: in the large reader the sidebar sits beside the pane, outside the pane's own host, and its
        // keys (M / U / F, the media keys) asked without a context, so a default yielded elsewhere went dead there. Installed after its tunnel handlers
        // (Avalonia runs one element's tunnel handlers in reverse order), like the pane's (WireReaderSurface).
        _sidebarKeyContext?.Dispose();
        _sidebarKeyContext = MeowChan.Desktop.Controls.KeyContextHost.Install(sidebar, _ => "reader");
        // The sidebar can be materialized after the pane has already attached.
        // Give the reader's rating router an owner on this sibling as well; the
        // large-reader frame is not the pane's visual subtree.
        if (_ratingCommands != null) RegisterRatingCommands(sidebar);
        if (_largeReader) ReaderHostingStep("sidebar.configured");
        _mediaSidebarSplitter = splitter;
        _mediaSidebar = sidebar;
        return sidebar;
    }

    // A focused thumbnail is a selecting control: lift only the page-owned media/view routes before its type-ahead.
    // Space is deliberately left to ReaderMediaSidebar.OnStripKeyDown, and the actual inline-video surface keeps its own
    // slot handler. The sidebar can be a sibling of the pane, so it must route configured reader view options too.
    private bool TryRouteSidebarSelectionShortcut(ReaderMediaSidebar sidebar, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || _ended || _obscuredByGallery || IsReaderDialogOpen) return false;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None && sidebar.InvalidateTagEditor())
        {
            e.Handled = true;
            return true;
        }
        // Enter activates the focused medium; Shift+Enter is the legacy alias.
        // Scope the native gesture to the strip item, not the sidebar's buttons or editors.
        bool mediumKey = IsMediumGalleryKey(e) && e.Source is ListBoxItem focusedItem
            && focusedItem.GetVisualAncestors().Contains(sidebar.Strip);
        bool galleryShortcut = CommandBindings.Matches(e, "gallery.open") && !CommandRouter.IsNativeInput(e.Source, e.Key);
        if ((mediumKey || galleryShortcut) && e.Source is Visual gallerySource
            && gallerySource.GetSelfAndVisualAncestors().Any(node => ReferenceEquals(node, sidebar)))
        {
            // Focus identifies the file even before a pending strip selection
            // notification has updated the sidebar's displayed index.
            ListBoxItem? item = gallerySource.GetSelfAndVisualAncestors().OfType<ListBoxItem>()
                .FirstOrDefault(node => node.GetVisualAncestors().Contains(sidebar.Strip));
            if (item?.Tag is int index) SelectMediaSidebarIndex(index);
            e.Handled = true;
            OpenMediaSidebarMedia();
            return true;
        }
        if (ReaderSelectionOwnsActivationKey(e)) return false;
        OnPostFilterKeyDown(sidebar, e);
        if (e.Handled) return true;
        bool inStrip = e.Source is Visual source
            && source.GetSelfAndVisualAncestors().Any(node => ReferenceEquals(node, sidebar.Strip));
        if (inStrip && TryRouteSidebarVideoKey(e)) return true;
        if (TryRouteFavoriteKey(sidebar, e) || TryRouteArchiveMediaShortcut(sidebar, e)) return true;
        OnReaderViewKeyDown(sidebar, e);
        return e.Handled;
    }

    // Puts the hidden sidebar and splitter into either host. Both fill their host's
    // height; the neighbouring posts and reader chrome cannot cap the media column.
    private void MoveMediaSidebarTo(Grid host)
    {
        ReaderMediaSidebar sidebar = EnsureMediaSidebar();
        if (_largeReader) ReaderHostingStep("sidebar.01");
        GridSplitter splitter = _mediaSidebarSplitter!;
        if (_largeReader) ReaderHostingStep("sidebar.02");
        if (ReferenceEquals(_mediaSidebarHost, host)) return;
        _movingMediaSidebar = true;
        if (_largeReader) ReaderHostingStep("sidebar.03");
        try
        {
            // Hidden for the move, the picture stays: the new host shows the same medium without flashing it away (WrapLargeReader gives it back
            // when that host does not show the sidebar).
            if (_mediaSidebarShown) SetMediaSidebarShown(false, keepPicture: true);
            if (_largeReader) ReaderHostingStep("sidebar.04");
            Grid pane = ArchiveReaderPane;
            if (_largeReader) ReaderHostingStep("sidebar.05");
            if (_mediaSidebarHost is { } previous)
            {
                previous.Children.Remove(splitter);
                if (_largeReader) ReaderHostingStep("sidebar.06");
                previous.Children.Remove(sidebar);
                if (_largeReader) ReaderHostingStep("sidebar.07");
                if (ReferenceEquals(previous, pane)) CollapseArchiveViewColumns();
                if (_largeReader) ReaderHostingStep("sidebar.08");
            }
            bool archiveView = ReferenceEquals(host, pane);
            if (_largeReader) ReaderHostingStep("sidebar.09");
            sidebar.IsVisible = splitter.IsVisible = false;
            if (_largeReader) ReaderHostingStep("sidebar.10");
            Grid.SetRow(sidebar, 0);
            Grid.SetRowSpan(sidebar, archiveView ? 2 : 1);
            if (_largeReader) ReaderHostingStep("sidebar.11");
            Grid.SetRow(splitter, 0);
            Grid.SetRowSpan(splitter, archiveView ? 2 : 1);
            if (_largeReader) ReaderHostingStep("sidebar.12");
            // Span the embedded reader's header and content rows, just as the large
            // reader spans its frame. No negative lift or padding tied to the posts.
            sidebar.Margin = default;
            if (_largeReader) ReaderHostingStep("sidebar.13");
            if (archiveView) sidebar.BorderThickness = new Thickness(0);
            else sidebar[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.CardEdge");
            if (_largeReader) ReaderHostingStep("sidebar.15");
            host.Children.Add(sidebar);
            if (_largeReader) ReaderHostingStep("sidebar.16");
            host.Children.Add(splitter);
            if (_largeReader) ReaderHostingStep("sidebar.17");
            _mediaSidebarHost = host;
            if (_largeReader) ReaderHostingStep("sidebar.18");
            _mediaSidebarAppliedWidth = double.NaN;
            if (_largeReader) ReaderHostingStep("sidebar.19");
        }
        finally { _movingMediaSidebar = false; }
    }

    // The page's first media options apply before InitializeMediaSidebar gave the pane its three columns.
    private bool HasArchiveViewColumns => ArchiveReaderPane.ColumnDefinitions.Count == 3;

    // The archive view without the sidebar: the posts card alone in the pane's first (star) column, no gaps. The large reader's export
    // dialog, which joins the pane while it is detached, lands in that column too.
    private void CollapseArchiveViewColumns()
    {
        if (!HasArchiveViewColumns) return;
        Grid pane = ArchiveReaderPane;
        ColumnDefinitions columns = pane.ColumnDefinitions;
        columns[0].Width = new GridLength(1, GridUnitType.Star);
        columns[1].Width = columns[2].Width = new GridLength(0);
        foreach (ColumnDefinition column in columns) { column.MinWidth = 0; column.MaxWidth = double.PositiveInfinity; }
        pane.ColumnSpacing = 0;
        Grid.SetColumn(ArchiveReaderContent, 0);
        if (ReaderHeaderRows is { } header) Grid.SetColumn(header, 0);
    }

    // Columns, side, width, visibility and the Tab scope from the preferences and the reader's width, in the host of the current view.
    private void LayoutMediaSidebar(bool force = false)
    {
        RefreshMediaSidebarToggle();
        if (_movingMediaSidebar) return;
        Grid pane = ArchiveReaderPane;
        bool large = _largeReader;
        if ((large ? _largeReaderFrame : pane) is not { } host || !HasArchiveViewColumns) return;
        ReaderMediaPreferences media = ReaderMedia;
        // The archive view's pane hosts it only while it sits in the archive split and is on screen (not on its way to or from the large reader).
        bool ready = large ? host.Children.Contains(pane) : ReferenceEquals(pane.Parent, ArchiveSplit) && pane.IsVisible;
        double readerWidth = host.Bounds.Width > 0 ? host.Bounds.Width : large ? TopLevel.GetTopLevel(this)?.ClientSize.Width ?? 0 : 0;
        bool shown = ready && ReaderMediaSidebarRules.IsShown(media, large, readerWidth);
        if (!ReferenceEquals(_mediaSidebarHost, host))
        {
            // It moves into this view's host only when this view shows it; until then it waits, hidden, where it is.
            if (!shown) return;
            MoveMediaSidebarTo(host);
            force = true;
        }
        if (_mediaSidebar is not { } sidebar || _mediaSidebarSplitter is not { } splitter) return;
        sidebar.ConfigureInfo(media);
        double gaps = large ? 0 : 2 * ReaderMediaSidebarRules.ArchiveViewGap;
        double width = ReaderMediaSidebarRules.EffectiveWidth(media.SidebarWidth, readerWidth, gaps);
        if (!force && shown == _mediaSidebarShown && media.SidebarRight == _mediaSidebarRight && (!shown || width == _mediaSidebarAppliedWidth)) return;
        _mediaSidebarRight = media.SidebarRight;
        _mediaSidebarAppliedWidth = width;
        if (!large && !shown) CollapseArchiveViewColumns();
        else
        {
            // The posts: the whole pane in the large reader's frame, the posts card in the archive view.
            Control posts = large ? pane : ArchiveReaderContent;
            int sidebarColumn = _mediaSidebarRight ? 2 : 0, postsColumn = _mediaSidebarRight ? 0 : 2;
            Grid.SetColumn(sidebar, sidebarColumn);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(posts, postsColumn);
            // The archive view's header rows stand over the posts card's column, not over the sidebar's.
            if (!large && ReaderHeaderRows is { } headerRows) Grid.SetColumn(headerRows, postsColumn);
            ColumnDefinitions columns = host.ColumnDefinitions;
            ColumnDefinition side = columns[sidebarColumn], postsTrack = columns[postsColumn];
            side.Width = shown ? new GridLength(width) : new GridLength(0);
            side.MinWidth = shown ? ReaderDisplayPreferences.MediaSidebarWidthMinimum : 0;
            side.MaxWidth = shown ? ReaderDisplayPreferences.MediaSidebarWidthMaximum : double.PositiveInfinity;
            postsTrack.Width = new GridLength(1, GridUnitType.Star);
            postsTrack.MinWidth = shown ? ReaderMediaSidebarRules.PostsMinimumWidth : 0;
            postsTrack.MaxWidth = double.PositiveInfinity;
            columns[1].Width = shown ? new GridLength(ReaderMediaSidebarRules.SplitterWidth) : new GridLength(0);
            // The archive view keeps the archive split's gap on both sides of the splitter.
            if (!large) host.ColumnSpacing = shown ? ReaderMediaSidebarRules.ArchiveViewGap : 0;
        }
        splitter.IsVisible = shown;
        sidebar.IsVisible = shown;
        // In the large reader the sidebar's border faces the posts; in the archive view it is a card of its own.
        if (large) sidebar.BorderThickness = new Thickness(_mediaSidebarRight ? 1 : 0, 0, _mediaSidebarRight ? 0 : 1, 0);
        UpdateLargeReaderTabScope(shown);
        if (shown != _mediaSidebarShown) SetMediaSidebarShown(shown);
    }

    // The detached reader keeps one Tab cycle: around the pane alone, or around pane and sidebar while the sidebar is shown.
    private void UpdateLargeReaderTabScope(bool sidebarShown)
    {
        if (!_largeReader || _largeReaderFrame is not { } frame || sidebarShown == _tabScopeOnFrame) return;
        Grid pane = ArchiveReaderPane;
        _readerTabScope?.Dispose();
        _frameTabScope?.Dispose();
        _frameTabScope = null;
        if (sidebarShown)
        {
            _readerTabScope = pane.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Continue, BindingPriority.Animation);
            _frameTabScope = frame.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Cycle, BindingPriority.Animation);
        }
        else _readerTabScope = pane.SetValue(KeyboardNavigation.TabNavigationProperty, KeyboardNavigationMode.Cycle, BindingPriority.Animation);
        _tabScopeOnFrame = sidebarShown;
    }

    private void SetMediaSidebarShown(bool shown, bool keepPicture = false)
    {
        _mediaSidebarShown = shown;
        if (shown)
        {
            _sidebarThrottle.Reset();
            _sidebarPostKey = null;
            RefreshMediaSidebar();
        }
        else HideMediaSidebarContent(keepPicture);
        // Last: the setter re-applies the live media options, which lays the sidebar out again (now a no-op).
        FocusedMediaPresentedElsewhere = shown;
    }

    // Hidden: every load and timer stops. The picture is given back, unless the sidebar only moves to the other host (keepPicture): then it stays,
    // without its strip, until the new host shows the sidebar (the same medium then needs no placeholder) or ReleaseKeptMediaSidebarPicture.
    private void HideMediaSidebarContent(bool keepPicture = false)
    {
        CancelMediaSidebarLoad();
        _sidebarFollowTimer?.Stop();
        _sidebarAnnounceTimer?.Stop();
        _sidebarThrottle.Reset();
        _sidebarPostKey = null;
        SetDisplayedSidebarMedium(null);
        _mediaSidebarDirty = false;
        CancelSidebarStripLoad();
        OfferSidebarVideo(null, null);
        if (keepPicture) _mediaSidebar?.ClearStrip();
        else ReleaseKeptMediaSidebarPicture();
    }

    // The hidden sidebar keeps nothing (the picture goes back to its cache).
    private void ReleaseKeptMediaSidebarPicture()
    {
        if (_mediaSidebarShown) return;
        _sidebarShownDetail = string.Empty;
        _savedMediaSidebarRow = null;
        _savedSidebarTags = null;
        _mediaSidebar?.ShowEmpty();
    }

    private void SaveMediaSidebarWidth()
    {
        if (_ended || !_mediaSidebarShown || _mediaSidebarHost is not { } host) return;
        double actual = host.ColumnDefinitions[_mediaSidebarRight ? 2 : 0].ActualWidth;
        if (actual <= 0) return;
        int width = ReaderMediaSidebarRules.ClampWidth(actual);
        if (width == ReaderMedia.SidebarWidth) return;
        // Kept without a re-layout (the column already has this width) and written alone, like the View menu's switches.
        _readerDisplayPreferences.Media = ReaderMedia with { SidebarWidth = width };
        double gaps = ReferenceEquals(host, ArchiveReaderPane) ? 2 * ReaderMediaSidebarRules.ArchiveViewGap : 0;
        _mediaSidebarAppliedWidth = ReaderMediaSidebarRules.EffectiveWidth(width, host.Bounds.Width, gaps);
        if (SaveReaderMediaSwitches is { } save) _ = SaveMediaSwitchAsync(save(config => config.ReaderMediaSidebarWidth = width));
    }

    // ---- switch and commands -------------------------------------------------------------------------------------------------

    private (bool Enabled, string Content, string Icon, string Name, string Tip)? _mediaSidebarTogglePresentation;

    private void RefreshMediaSidebarToggle()
    {
        if (ReaderMediaSidebarToggle is not { } button) return;
        ReaderMediaPreferences media = ReaderMedia;
        bool enabled = CanToggleMediaSidebar;
        bool on = ReaderMediaSidebarRules.IsOn(media, _largeReader);
        string content = Texts.T("reader.sidebar.compact");
        string icon = CommandCatalog.IconForState("reader.sidebar.toggle", media.SidebarRight ? "right" : "left");
        string name = CommandCatalog.Label("reader.sidebar.toggle");
        string tip = CommandBindings.Tooltip("reader.sidebar.toggle", MediaSidebarHint());
        var presentation = (enabled, content, icon, name, tip);
        if (button.IsChecked != on) button.IsChecked = on;
        if (_mediaSidebarTogglePresentation == presentation) return;
        if (_mediaSidebarTogglePresentation?.Icon != icon) CommandBindings.ShowIconBeforeText(button, icon);
        _mediaSidebarTogglePresentation = presentation;
        button.IsEnabled = enabled;
        button.Content = content;
        ReaderSidebarToggles.Describe(button, name, tip);
    }

    private string? MediaSidebarHint()
    {
        ReaderMediaPreferences media = ReaderMedia;
        double width = (_largeReader ? _largeReaderFrame?.Bounds.Width : ArchiveReaderPane.Bounds.Width) ?? 0;
        bool on = ReaderMediaSidebarRules.IsOn(media, _largeReader);
        if (ReaderMediaSidebarRules.IsCollapsedByWidth(media, width))
            return Texts.T("reader.sidebar.collapsed", Math.Ceiling(ReaderMediaSidebarRules.MinimumReaderWidth(media)));
        // The archive view while the sidebar shows only in the large reader: it says where it is (the switch shows it here too).
        if (!_largeReader && media.Sidebar && !on) return Texts.T("reader.sidebar.largeOnly");
        // Off: the entry says what it does and which shortcut turns it on.
        return ReaderMediaMultiRules.DiscoveryHint(media with { Sidebar = on }, largeReader: true, width, CommandCatalog.ShortcutDisplay("reader.sidebar.toggle"));
    }

    internal bool CanToggleMediaSidebar => !_ended && !IsFilenameSearchOpen && !IsReaderDialogOpen;

    /// <summary>
    /// The View option and Primary+Shift+M: switches the sidebar of the view it is used in and stores the choice (large reader: on or off;
    /// archive view: Always or Large reader only, see <see cref="ReaderMediaSidebarRules.Toggled"/>).
    /// </summary>
    internal void ToggleMediaSidebar()
    {
        if (!CanToggleMediaSidebar || _ended) return;
        ReaderMediaPreferences media = ReaderMedia;
        if (_mediaSidebar?.IsKeyboardFocusWithin == true && ReaderMediaSidebarRules.IsOn(media, _largeReader)) FocusSelectedPostOrList();
        ReaderMediaPreferences next = ReaderMediaSidebarRules.Toggled(media, _largeReader);
        ApplyReaderMediaPreferences(next);
        // Only what the switch decides is written: another setting changed meanwhile (a width in Settings) is not overwritten.
        if (SaveReaderMediaSwitches is { } save)
            _ = SaveMediaSwitchAsync(save(config =>
            {
                config.ReaderMediaSidebar = next.Sidebar;
                config.ReaderMediaSidebarScope = next.SidebarAlways ? Config.ReaderMediaSidebarScopeAlways : Config.ReaderMediaSidebarScopeLargeReader;
            }));
    }

    /// <summary>The × of the sidebar: turns it off for this view (and stores the choice), the focus goes back to the selected post. Escape only moves the focus (RD-01).</summary>
    internal void CloseMediaSidebar()
    {
        if (!ReaderMediaSidebarRules.IsOn(ReaderMedia, _largeReader)) return;
        FocusSelectedPostOrList();
        ToggleMediaSidebar();
    }

    private bool IsMediaSidebarSource(object? source) =>
        _mediaSidebarShown && _mediaSidebar is { } sidebar && source is Visual visual && visual.GetSelfAndVisualAncestors().Contains(sidebar);

    private void FocusSelectedPostOrList()
    {
        if (ArchiveMediaTab.IsSelected)
        {
            VisibleMediaList.Focus();
            return;
        }
        ListBox posts = ArchivePosts;
        if (posts.SelectedItem is { } item && posts.ContainerFromItem(item) is Control container && container.Focus()) return;
        posts.Focus();
    }

    internal bool CanStepMediaSidebar(int delta) => !_ended && _mediaSidebarShown && !IsReaderDialogOpen && !ArchiveMediaTab.IsSelected
        && ArchivePosts.SelectedItem is ArchivePostRow { ShowsMedia: true } post
        && ReaderMediaSidebarRules.CanStep(_sidebarIndex, post.Attachments.Count, delta);

    private void OnMediaSidebarStepKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || _ended || _busy || _obscuredByGallery || IsReaderDialogOpen
            || !_mediaSidebarShown || sender is not Control owner || e.Source is not Visual source
            || !source.GetSelfAndVisualAncestors().Contains(owner)
            || !CommandRouter.IsRoutingContextActive(owner, source)) return;
        if (CanToggleMediaSidebar && _mediaSidebar?.RouteInfoKey(e) == true) return;
        if (ArchiveMediaTab.IsSelected) return;
        int direction = ReaderSidebarStepKeys.Direction(e, ArchivePosts, _mediaSidebar?.Strip,
            id => ShortcutService.Current.Matches(e, id));
        if (direction == 0) return;
        // At an edge the matching command is still ours; it must not fall through into list navigation.
        e.Handled = true;
        StepMediaSidebar(direction);
    }

    /// <summary>Alt+Shift+←/→: the previous or next medium of the focused post.</summary>
    internal void StepMediaSidebar(int delta)
    {
        if (!CanStepMediaSidebar(delta) || ArchivePosts.SelectedItem is not ArchivePostRow post) return;
        _sidebarIndex = ReaderMediaSidebarRules.Step(_sidebarIndex, post.Attachments.Count, delta);
        RefreshMediaSidebar();
    }

    /// <summary>A click, Enter or an arrow key on a thumbnail of the strip: that file of the focused post is shown.</summary>
    internal void SelectMediaSidebarIndex(int index)
    {
        if (_ended || !_mediaSidebarShown || IsReaderDialogOpen || ArchivePosts.SelectedItem is not ArchivePostRow { ShowsMedia: true } post) return;
        int target = ReaderMediaSidebarRules.ClampIndex(index, post.Attachments.Count);
        if (target == _sidebarIndex) return;
        _sidebarIndex = target;
        RefreshMediaSidebar();
    }

    // A click on a tile of the focused post (or of another post, which it then focuses) shows that medium here; a video plays here.
    private int? _sidebarPendingIndex;
    private bool _sidebarPlayRequested;

    private bool ShowTileInMediaSidebar(ArchiveAttachmentRow row)
    {
        if (!_mediaSidebarShown || _ended || IsReaderDialogOpen || _mediaSidebar == null) return false;
        ArchivePostRow? post = ArchivePosts.SelectedItem is ArchivePostRow selected && selected.Attachments.Contains(row)
            ? selected : ArchivePosts.Items.OfType<ArchivePostRow>().FirstOrDefault(candidate => candidate.Attachments.Contains(row));
        if (post == null || !post.ShowsMedia) return false;
        int index = -1;
        for (int i = 0; i < post.Attachments.Count; i++) if (ReferenceEquals(post.Attachments[i], row)) { index = i; break; }
        _sidebarPlayRequested = ReaderInlineVideoRules.CanOffer(_inlineVideo.Options, row);
        if (ReferenceEquals(ArchivePosts.SelectedItem, post)) { _sidebarIndex = index; RefreshMediaSidebar(); }
        else { _sidebarPendingIndex = index; ArchivePosts.SelectedItem = post; }
        return true;
    }

    /// <summary>"Open in gallery" (and a click on the picture): the gallery at this medium, the same path as Enter on a post.</summary>
    internal void OpenMediaSidebarMedia()
    {
        if (_ended || !_mediaSidebarShown || IsReaderDialogOpen) return;
        if (ArchiveMediaTab.IsSelected)
        {
            if (SavedMediaSidebarRow is { } saved)
                OpenActions.Start(() => OpenAssetGalleryAsync(saved, _mediaSidebar?.FrameArea), "archive.media.open");
        }
        else CurrentSidebarAttachment()?.OpenPreview?.Invoke();
    }

    private ArchiveAttachmentRow? CurrentSidebarAttachment() =>
        !ArchiveMediaTab.IsSelected && ArchivePosts.SelectedItem is ArchivePostRow { ShowsMedia: true } post
            ? post.Attachments[ReaderMediaSidebarRules.ClampIndex(_sidebarIndex, post.Attachments.Count)] : null;

    // ---- following the selection -----------------------------------------------------------------------------------------------

    private void OnMediaSidebarSelectionChanged(bool selectionChanged = true)
    {
        if (!_mediaSidebarShown || _ended) return;
        if (selectionChanged)
        {
            _mediaSidebar?.InvalidateTagEditor();
            // Throttle starting the next decode, not retiring the previous selection. Otherwise an old completion can still publish
            // a picture/error (or a strip thumbnail) in the interval before the follow timer runs.
            CancelMediaSidebarLoad();
            CancelSidebarStripLoad();
        }
        long now = _sidebarClock.ElapsedMilliseconds;
        // A selection that is only cleared (a page or thread being rebuilt) waits for the tick: the picture does not flash away.
        bool hasSelection = ArchiveMediaTab.IsSelected ? SelectedMediaRow() != null : ArchivePosts.SelectedItem != null;
        if (!hasSelection) _sidebarThrottle.MarkPending();
        else if (_sidebarThrottle.Request(now)) { RefreshMediaSidebar(); return; }
        DispatcherTimer timer = _sidebarFollowTimer ??= CreateSidebarTimer(() =>
        {
            if (_sidebarThrottle.Tick(_sidebarClock.ElapsedMilliseconds)) RefreshMediaSidebar();
        });
        if (!timer.IsEnabled)
        {
            timer.Interval = TimeSpan.FromMilliseconds(!hasSelection
                ? ReaderMediaSidebarRules.FollowIntervalMs : _sidebarThrottle.Remaining(now));
            timer.Start();
        }
    }

    private static DispatcherTimer CreateSidebarTimer(Action tick)
    {
        DispatcherTimer timer = new();
        timer.Tick += (_, _) => { timer.Stop(); tick(); };
        return timer;
    }

    // A wider or taller picture frame than the shown decode covers asks for one larger decode (through the same throttle).
    // Within NeedsLargerDecode's margin (the alignment's inset corrections, sub-dip layout jitter) nothing is decoded; the larger decode swaps in place.
    private void OnMediaSidebarFrameResized()
    {
        if (!_mediaSidebarShown || _mediaSidebar is not { Shown: { } shown } sidebar) return;
        (double Width, double Height) needed = ReaderMediaSidebarRules.PhysicalTarget(sidebar.Frame.Bounds.Size,
            TopLevel.GetTopLevel(sidebar)?.RenderScaling ?? 1);
        if (!ReaderMediaSidebarRules.NeedsLargerDecode(shown.PixelSize, needed, sidebar.Load.ShownSource)) return;
        OnMediaSidebarSelectionChanged(selectionChanged: false);
    }

    /// <summary>The gallery uncovered the reader: show a changed selection or retry an unanswered preview.</summary>
    internal void ResumeMediaSidebar()
    {
        if (_mediaSidebarShown && (_mediaSidebarDirty || _mediaSidebar is { Load.HasPicture: false, Load.Awaiting: false }))
            RefreshMediaSidebar();
    }

    private void CancelMediaSidebarLoad()
    {
        _mediaSidebar?.Load.Cancel();
        CancellationTokenSource? loading = _sidebarLoading;
        _sidebarLoading = null;
        try { loading?.Cancel(); } catch (ObjectDisposedException) { }
        // The running task owns disposal, including non-cooperative decodes after cancellation.
    }

    private void RefreshPostMediaSidebarDecisions()
    {
        if (_ended || !_mediaSidebarShown || _mediaSidebar is not { } sidebar || _displayedSidebarMedium is not { } shown
            || !ReferenceEquals(_thread, shown.Thread)) return;
        (ArchivedThreadView thread, ArchivePostRow post, ArchiveAttachmentRow row) = shown;
        ResourceId resource = thread.Summary.Id;
        ResolvedMediaAttachment? resolved = MediaAttachmentResolver.Resolve(_assets.Select(asset => asset.Manifest).Distinct(), resource, post.Id, row.Attachment);
        ArchiveAssetRow? saved = resolved == null ? null
            : _assets.FirstOrDefault(asset => ReferenceEquals(asset.Manifest, resolved.Snapshot) && asset.Asset == resolved.Asset);
        if (saved != null)
        {
            FavoriteAssetKey key = FavoriteAssetKey.From(resource, saved.Asset);
            bool Current() => !_ended && _available && _mediaSidebarShown && !_obscuredByGallery
                && ReferenceEquals(_thread, thread) && _displayedSidebarMedium == shown;
            sidebar.ConfigureRating(() => Current() && !saved.RatingUnavailable && _mediaRatingError == null && GetMediaRating != null
                && SetMediaRatingAsync != null && CanChangeMediaRating?.Invoke(key) == true, () => saved.Rating,
                value => SetMediaRatingAsync?.Invoke(key, value) ?? Task.CompletedTask);
        }
        else sidebar.ConfigureRating(null, null, null);

        string? blocked = _privateTags == null ? MediaMenuReasons.TagsUnknown : null;
        MediaTagSubject? subject = null;
        if (blocked == null && resolved != null && saved != null)
        {
            if (resolved.IsLegacy)
            {
                ArchiveMediaSubject context = ResolveArchiveMediaSubject(saved, row, post.Id);
                subject = MediaTagSubjects.Of(context.TagEntry);
                blocked = context.TagsBlocked;
            }
            else
            {
                GalleryEntry entry = new(new(resource, saved.Manifest.Directory, saved.Asset.AssetId),
                    saved.Manifest, saved.Asset, null, row.Attachment, !saved.Asset.IsCurrent, false) { AttachmentResolution = resolved };
                subject = MediaTagSubjects.Of(entry, out GalleryMediaTagUnavailable reason);
                if (subject == null) blocked = MediaMenuReasons.Tags(reason);
            }
        }
        string[]? tags = subject != null ? _privateTags?.TagsOf(subject).Select(tag => tag.Name.Display).ToArray() : null;
        sidebar.SetTags(tags, blocked ?? (subject == null ? MediaMenuReasons.NotSaved : null));
        sidebar.SetSourceFacts(resource.Source + " /" + resource.Board + "/ · " + resource.Thread, post.Author, post.Posted,
            saved?.Asset.Length, row.Attachment.ExpectedSize, row.Attachment.ProviderSizeHint,
            MediaProvenanceText.Summary(resolved?.Binding), saved?.Asset.IsCurrent == false);
    }

    private void RefreshMediaSidebar()
    {
        if (_ended || !_mediaSidebarShown || _mediaSidebar is not { } sidebar) return;
        // Under the gallery nothing new is decoded; the reader catches up when it is uncovered.
        if (_obscuredByGallery) { _mediaSidebarDirty = true; return; }
        _mediaSidebarDirty = false;
        CancelMediaSidebarLoad();
        if (ArchiveMediaTab.IsSelected)
        {
            RefreshSavedMediaSidebar(sidebar);
            return;
        }
        _savedMediaSidebarRow = null;
        ArchivePostRow? post = ArchivePosts.SelectedItem as ArchivePostRow;
        string? key = post == null ? null : _thread?.Summary.Id + "\n" + post.Id;
        bool samePost = string.Equals(key, _sidebarPostKey, StringComparison.Ordinal);
        if (!samePost) { _sidebarIndex = _sidebarPendingIndex ?? 0; _sidebarPendingIndex = null; _sidebarPostKey = key; }
        if (post == null)
        {
            SetDisplayedSidebarMedium(null);
            CancelSidebarStripLoad();
            OfferSidebarVideo(null, null);
            // Say what is true (RD-03, RD-04): a thread being read, a view being prepared or a view without posts is no "select a post".
            bool threadLoading = (_thread == null && _selectedThread != null && _busy) || (_thread != null && Projection == null);
            bool noPosts = _thread != null && Projection is { Count: 0 };
            string text = Texts.T(ReaderMediaSidebarRules.EmptyTextKey(threadLoading, noPosts));
            if (threadLoading) sidebar.ShowThreadLoading(text); // the previous picture stays until the new post is known
            else
            {
                sidebar.ShowEmpty(text);
                ScheduleSidebarAnnouncement(noPosts ? text : Texts.T("reader.sidebar.regionEmpty"));
            }
            return;
        }
        if (post.IsStub || !post.ShowsMedia)
        {
            SetDisplayedSidebarMedium(null);
            CancelSidebarStripLoad();
            sidebar.ClearStrip();
            OfferSidebarVideo(null, null);
            // A post the view shows as a stub or whose media the view hides names that; a thread in which no post has media says so once.
            string reason = post.IsStub || post.HasMediaHiddenLine ? Texts.T("reader.sidebar.hidden", post.Id)
                : ThreadHasPostMedia() ? Texts.T("reader.sidebar.noMedia", post.Id) : Texts.T("reader.sidebar.threadNoMedia");
            sidebar.Describe(post.NumberLabel, string.Empty, string.Empty, false);
            sidebar.ConfigureFavorite(null);
            // A final text: the frame is empty with the reason, never the previous post's picture dimmed under it.
            sidebar.ShowNoMedia(reason);
            ScheduleSidebarAnnouncement(reason);
            return;
        }
        int count = post.Attachments.Count;
        _sidebarIndex = ReaderMediaSidebarRules.ClampIndex(_sidebarIndex, count);
        ArchiveAttachmentRow row = post.Attachments[_sidebarIndex];
        CapturedAttachmentText file = CapturedAttachmentText.Of(row);
        string postLine = post.NumberLabel;
        // Tom (16.11.2026): the medium already shown (a rebuilt page, a refreshed thread, a larger frame, the sidebar back from the other host) keeps
        // its picture: no load at all while it covers the frame, otherwise a larger decode that swaps in place (ReaderMediaSidebarLoadState).
        string mediumKey = SidebarMediumKey(post, row);
        (double Width, double Height) target = MediaSidebarTarget(sidebar);
        ReaderMediaLoadTicket ticket = sidebar.Load.Begin(mediumKey, target);
        ThreadMediaPositions? media = _thread?.MediaPositions;
        int? number = media?.NumberOf(post.Id, row.Attachment.Position);
        GalleryTagPanelPosition? position = media != null && number is int at ? new(at, media.Count, false) : null;
        SetDisplayedSidebarMedium(_thread, post, row);
        sidebar.Describe(postLine, file.Name, string.Empty, row.OpenPreview != null, position);
        if (ticket.SameMedium && _sidebarShownDetail.Length > 0) sidebar.SetDetail(_sidebarShownDetail);
        sidebar.ConfigureFavorite(row);
        RefreshMediaSidebarDecisions();
        UpdateSidebarStrip(sidebar, post, samePost);
        OfferSidebarVideo(post, row);
        string announcement = ReaderMediaSidebarRules.RegionText(post.Id, _sidebarIndex, count, file);
        if (media != null && number is int current)
            announcement += " · " + Texts.T("tags.quick.gallery.position", current, media.Count);
        ScheduleSidebarAnnouncement(announcement);
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sidebarLoading = loading;
        if (ticket.UpToDate)
        {
            // Nothing to decode: only the gallery's fit limit may have changed (the picture's largest size); a video's autoplay is offered as after
            // a load (it starts nothing that already plays) and the neighbours are warmed again.
            if (sidebar.Load.ShownSource is { } source)
                sidebar.LimitPicture(ReaderMediaSidebarRules.MaximumDisplay(source.Width, _sidebarFit), ReaderMediaSidebarRules.MaximumDisplay(source.Height, _sidebarFit));
            ScheduleSidebarVideoAutoplay();
            MediaSidebarTask = RunMediaSidebarLoadAsync(loading, token => RewarmMediaSidebarNeighboursAsync(post, target, token));
            return;
        }
        MediaSidebarTask = RunMediaSidebarLoadAsync(loading,
            token => LoadMediaSidebarAsync(sidebar, post, row, mediumKey, ticket, target, token));
    }

    private Task RunMediaSidebarLoadAsync(CancellationTokenSource loading, Func<CancellationToken, Task> work) =>
        CancellationWork.RunAsync(loading, work, () =>
        {
            if (ReferenceEquals(_sidebarLoading, loading)) _sidebarLoading = null;
        });

    // Whether any post of the open thread has an attachment (the sidebar's "This thread has no media" is about the whole thread); one scan per thread read.
    private object? _sidebarMediaScanned;
    private bool _sidebarThreadHasMedia;

    private bool ThreadHasPostMedia()
    {
        if (_thread is not { } thread) return false;
        if (!ReferenceEquals(thread, _sidebarMediaScanned))
        {
            _sidebarThreadHasMedia = thread.Posts.Any(candidate => candidate.Attachments is { Count: > 0 });
            _sidebarMediaScanned = thread;
        }
        return _sidebarThreadHasMedia;
    }

    private void SetDisplayedSidebarMedium(ArchivedThreadView? thread, ArchivePostRow? post = null, ArchiveAttachmentRow? attachment = null)
    {
        (ArchivedThreadView, ArchivePostRow, ArchiveAttachmentRow)? next = thread != null && post != null && attachment != null
            ? (thread, post, attachment) : null;
        if (_displayedSidebarMedium == next) return;
        _displayedSidebarMedium = next;
        _mediaSidebar?.InvalidateTagEditor();
    }

    // The identity of a medium in the sidebar: the thread, the post and the attachment (as the inline video's slot keys it), stable across rebuilt rows.
    private string SidebarMediumKey(ArchivePostRow post, ArchiveAttachmentRow row) =>
        _thread?.Summary.Id + "\n" + post.Id + "\n" + ReaderInlineVideoSlot.KeyOf(row.Attachment);

    // ---- the thumbnail strip of a post with several files (R11) ----------------------------------------------------------------------

    private void CancelSidebarStripLoad()
    {
        CancellationTokenSource? loading = _sidebarStripLoading;
        _sidebarStripLoading = null;
        try { loading?.Cancel(); } catch (ObjectDisposedException) { }
        // The running task owns disposal, including non-cooperative decodes after cancellation.
    }

    // The strip shows the files of the focused post (at most the configured number at once); only a new layout (another post, another
    // window) loads thumbnails, and only the missing ones. They are decoded small, through the reader's thumbnail gate and cache.
    private void UpdateSidebarStrip(ReaderMediaSidebar sidebar, ArchivePostRow post, bool samePost)
    {
        int count = post.Attachments.Count;
        bool rebuilt = sidebar.ShowStrip(count, _sidebarIndex, ReaderMedia.Clamped().StripLimit,
            index => CapturedAttachmentText.Of(post.Attachments[index]).Name, samePost);
        if (!sidebar.StripVisible) { CancelSidebarStripLoad(); return; }
        // A selection rebuild can retire the previous load without changing this post's strip geometry. Resume its missing thumbnails.
        if (!rebuilt && _sidebarStripLoading != null) return;
        CancelSidebarStripLoad();
        List<int> missing = sidebar.StripIndexes.Where(index => !sidebar.HasStripPicture(index)).ToList();
        if (missing.Count == 0) return;
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sidebarStripLoading = loading;
        MediaSidebarStripTask = CancellationWork.RunAsync(loading,
            token => LoadSidebarStripAsync(sidebar, post, missing, token), () =>
            {
                if (ReferenceEquals(_sidebarStripLoading, loading)) _sidebarStripLoading = null;
            });
    }

    private async Task LoadSidebarStripAsync(ReaderMediaSidebar sidebar, ArchivePostRow post, IReadOnlyList<int> indexes, CancellationToken token)
    {
        async Task LoadOne(int index)
        {
            if (post.Attachments[index].LoadStripThumbnail is not { } load) return;
            try
            {
                Bitmap? bitmap = await load(ReaderMediaMultiRules.ThumbnailDecodeWidth, ReaderMediaMultiRules.ThumbnailDecodeHeight, token);
                if (bitmap == null) return;
                if (token.IsCancellationRequested || _ended || !_mediaSidebarShown) { MediaTileThumbnails.Release(bitmap); return; }
                sidebar.SetStripPicture(index, bitmap, MediaTileThumbnails.Release);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                // A thumbnail that cannot be made stays a quiet placeholder; the shown medium reports its own failure.
                Serilog.Log.Debug(error, "A media sidebar strip thumbnail could not be shown");
            }
        }
        await Task.WhenAll(indexes.Select(LoadOne));
    }

    private void ScheduleSidebarAnnouncement(string text)
    {
        _sidebarAnnouncement = text;
        DispatcherTimer timer = _sidebarAnnounceTimer ??= CreateSidebarTimer(() =>
        {
            if (!_ended && _mediaSidebarShown) _mediaSidebar?.Announce(_sidebarAnnouncement);
        });
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(ReaderMediaSidebarRules.AnnounceDelayMs);
        timer.Start();
    }

    private bool IsCurrentSidebarRequest(int request) => !_ended && _mediaSidebarShown && _mediaSidebar is { } sidebar && sidebar.Load.IsCurrent(request);

    // The frame's size in physical pixels; before the first layout an estimate from the configured width and the window height.
    private (double Width, double Height) MediaSidebarTarget(ReaderMediaSidebar sidebar)
    {
        Size frame = sidebar.Frame.Bounds.Size;
        if (frame.Width < 1 || frame.Height < 1)
        {
            double height = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 720;
            frame = new Size(Math.Max(1, ReaderMedia.Clamped().SidebarWidth - 32), Math.Max(1, height - 200));
        }
        return ReaderMediaSidebarRules.PhysicalTarget(frame, TopLevel.GetTopLevel(sidebar)?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
    }

    private Task LoadMediaSidebarAsync(ReaderMediaSidebar sidebar, ArchivePostRow post, ArchiveAttachmentRow row, string key,
        ReaderMediaLoadTicket ticket, (double Width, double Height) target, CancellationToken token)
    {
        ReaderMediaSidebarImageLoad load = new(sidebar, key, ticket, _sidebarFit, () => IsCurrentSidebarRequest(ticket.Request), token);
        return load.RunAsync(async ct =>
        {
            GalleryEntry? entry = row.ResolveEntry == null ? null : await row.ResolveEntry(ct);
            ct.ThrowIfCancellationRequested();
            if (entry == null) return new ReaderMediaSidebarContent.Unavailable("archive.notSaved");
            if (entry.Kind == SavedMediaKind.Image)
            {
                (Bitmap bitmap, GalleryImageInfo info) = await GalleryImageCache.DecodeAsync(entry,
                    new GalleryDecodeRequest.Fit(target.Width, target.Height), ct);
                return new ReaderMediaSidebarContent.Picture(bitmap, GalleryImageCache.Shared.Release, info.StillFrame, new PixelSize(info.Width, info.Height));
            }
            if (entry.Kind == SavedMediaKind.Video && row.LoadSized is { } poster)
            {
                Bitmap? bitmap = await poster((int)target.Width, (int)target.Height, ct);
                // A missing poster must not disable playback of the resolved video; only the current request may offer autoplay.
                if (IsCurrentSidebarRequest(ticket.Request) && !ct.IsCancellationRequested) ScheduleSidebarVideoAutoplay();
                if (bitmap != null) return new ReaderMediaSidebarContent.Picture(bitmap, MediaTileThumbnails.Release, Playable: true);
            }
            return new ReaderMediaSidebarContent.Unavailable("reader.sidebar.unavailable");
        }, picture =>
        {
            _sidebarShownDetail = picture.Source is { } source
                ? $"{source.Width}×{source.Height}" : string.Empty;
            sidebar.SetDetail(_sidebarShownDetail);
        }, () => PreloadMediaSidebarNeighboursAsync(post, target, token));
    }

    // A refresh that found the shown picture up to date warms the neighbours again (the load it replaced may have been cancelled before it did).
    private async Task RewarmMediaSidebarNeighboursAsync(ArchivePostRow post, (double Width, double Height) target, CancellationToken token)
    {
        try { await PreloadMediaSidebarNeighboursAsync(post, target, token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Serilog.Log.Debug(error, "The media sidebar's neighbours could not be warmed again"); }
    }

    // The next medium of this post and the first of the posts before and after it, decoded in the background into the gallery cache
    // (they wait while a shown image is decoded) and given back at once: one step later they show from memory.
    private async Task PreloadMediaSidebarNeighboursAsync(ArchivePostRow post, (double Width, double Height) target, CancellationToken token)
    {
        List<ArchiveAttachmentRow> neighbours = new();
        if (_sidebarIndex + 1 < post.Attachments.Count) neighbours.Add(post.Attachments[_sidebarIndex + 1]);
        if (ArchivePosts.ItemsSource is IList items)
        {
            int at = items.IndexOf(post);
            foreach (int index in new[] { at + 1, at - 1 })
                if (at >= 0 && index >= 0 && index < items.Count && items[index] is ArchivePostRow { ShowsMedia: true } near)
                    neighbours.Add(near.Attachments[0]);
        }
        GalleryDecodeRequest.Fit fit = new(target.Width, target.Height);
        await ReaderMediaSidebarRules.PreloadNeighboursAsync(neighbours.Select(neighbour => neighbour.ResolveEntry), async (entry, ct) =>
        {
            if (entry.Kind != SavedMediaKind.Image) return;
            (Bitmap bitmap, _) = await GalleryImageCache.DecodeThrough(GalleryImageCache.Shared, GalleryImageDecoder.DecodeBackgroundAsync,
                entry, fit, ct);
            GalleryImageCache.Shared.Release(bitmap);
        }, token);
    }
}
