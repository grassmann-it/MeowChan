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
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

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
    private DispatcherTimer? _sidebarFollowTimer, _sidebarAnnounceTimer, _sidebarPlaceholderTimer;
    private CancellationTokenSource? _sidebarLoading;
    private CancellationTokenSource? _sidebarStripLoading;
    private int _sidebarRequest, _sidebarPlaceholderRequest;
    private string? _sidebarPostKey;
    private int _sidebarIndex;
    private PixelSize? _sidebarSource;
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
        ArchivePosts.SelectionChanged += (_, _) => OnMediaSidebarSelectionChanged();
        // The archive view: the reader pane gets the sidebar's columns (posts card, splitter, sidebar); its header row spans all three.
        // A hidden sidebar leaves the posts card alone in the first, star column.
        Grid pane = ArchiveReaderPane;
        pane.ColumnDefinitions = new ColumnDefinitions("*,0,0");
        foreach (Control child in pane.Children)
            if (!ReferenceEquals(child, ArchiveReaderContent)) Grid.SetColumnSpan(child, 3);
        pane.SizeChanged += (_, _) => { if (!_largeReader) LayoutMediaSidebar(); };
        pane.PropertyChanged += (_, e) => { if (e.Property == IsVisibleProperty && !_largeReader) LayoutMediaSidebar(); };
    }

    // ---- hosts ---------------------------------------------------------------------------------------------------------------

    /// <summary>The large reader's content: the pane in the frame with the (possibly hidden) sidebar.</summary>
    private Control WrapLargeReader(Grid pane)
    {
        Grid frame = _largeReaderFrame ??= CreateLargeReaderFrame();
        // From the archive view's pane (hidden first); the pane's columns give the posts card its whole width back.
        MoveMediaSidebarTo(frame);
        _paneSplitColumn = Grid.GetColumn(pane); // the embedded reader's column in ArchiveSplit, restored on the way back
        frame.Children.Add(pane);
        _mediaSidebarAppliedWidth = double.NaN;
        LayoutMediaSidebar(force: true);
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
            if (_mediaSidebarShown) SetMediaSidebarShown(false);
            Dispatcher.UIThread.Post(() => { if (!_ended && !_largeReader) LayoutMediaSidebar(force: true); }, DispatcherPriority.Loaded);
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
        ReaderMediaSidebar sidebar = _mediaSidebar = new ReaderMediaSidebar { IsVisible = false };
        GridSplitter splitter = _mediaSidebarSplitter = new GridSplitter
        {
            Name = "ReaderMediaSidebarSplitter", Width = ReaderMediaSidebarRules.SplitterWidth,
            ResizeDirection = GridResizeDirection.Columns, IsVisible = false
        };
        Avalonia.Automation.AutomationProperties.SetName(splitter, Texts.T("reader.sidebar.splitter"));
        splitter.DragCompleted += (_, _) => SaveMediaSidebarWidth();
        splitter.KeyUp += (_, _) => SaveMediaSidebarWidth();
        sidebar.Frame.SizeChanged += (_, _) => OnMediaSidebarFrameResized();
        sidebar.Open.Click += (_, e) => { e.Handled = true; OpenMediaSidebarMedia(); };
        sidebar.StripSelected += (_, index) => SelectMediaSidebarIndex(index);
        sidebar.StripActivated += (_, index) => { SelectMediaSidebarIndex(index); OpenMediaSidebarMedia(); };
        sidebar.PictureActivated += (_, _) => OnMediaSidebarPictureActivated();
        sidebar.GalleryRequested += (_, _) => OpenMediaSidebarMedia();
        // The "No." of its header line copies the post's reference like the post's own number does.
        sidebar.PostLinkClicked += (_, _) => { if (!_ended && ArchivePosts.SelectedItem is ArchivePostRow post) _ = CopyPostReferenceAsync(post); };
        CreateSidebarVideo(sidebar);
        sidebar.Close.Click += (_, e) => { e.Handled = true; CloseMediaSidebar(); };
        // The sidebar's keys reach the reader's view shortcuts too (the toggle, the reader's other switches), in either host.
        sidebar.AddHandler(KeyDownEvent, (_, e) => { if (!TryRouteSidebarVideoKey(e)) OnReaderViewKeyDown(sidebar, e); }, RoutingStrategies.Bubble);
        return sidebar;
    }

    // Puts the (hidden) sidebar and its splitter into the large reader's frame or the archive view's pane. The archive view shows it as a
    // card beside the posts card, below the header row; in the large reader it runs the full height and one edge faces the posts.
    private void MoveMediaSidebarTo(Grid host)
    {
        ReaderMediaSidebar sidebar = EnsureMediaSidebar();
        GridSplitter splitter = _mediaSidebarSplitter!;
        if (ReferenceEquals(_mediaSidebarHost, host)) return;
        _movingMediaSidebar = true;
        try
        {
            if (_mediaSidebarShown) SetMediaSidebarShown(false);
            Grid pane = ArchiveReaderPane;
            if (_mediaSidebarHost is { } previous)
            {
                previous.Children.Remove(splitter);
                previous.Children.Remove(sidebar);
                if (ReferenceEquals(previous, pane)) CollapseArchiveViewColumns();
            }
            bool archiveView = ReferenceEquals(host, pane);
            sidebar.IsVisible = splitter.IsVisible = false;
            Grid.SetRow(sidebar, archiveView ? 1 : 0);
            Grid.SetRow(splitter, archiveView ? 1 : 0);
            if (archiveView)
            {
                sidebar.Classes.Add("workspace-section-flush");
                sidebar.ClearValue(Border.BorderThicknessProperty);
                sidebar.ClearValue(Border.BorderBrushProperty);
            }
            else
            {
                sidebar.Classes.Remove("workspace-section-flush");
                sidebar[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.CardEdge");
            }
            host.Children.Add(sidebar);
            host.Children.Add(splitter);
            _mediaSidebarHost = host;
            _mediaSidebarAppliedWidth = double.NaN;
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
    }

    // Columns, side, width, visibility and the Tab scope from the preferences and the reader's width, in the host of the current view.
    private void LayoutMediaSidebar(bool force = false)
    {
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

    private void SetMediaSidebarShown(bool shown)
    {
        _mediaSidebarShown = shown;
        if (shown)
        {
            _sidebarThrottle.Reset();
            _sidebarPostKey = null;
            RefreshMediaSidebar();
        }
        else HideMediaSidebarContent();
        // Last: the setter re-applies the live media options, which lays the sidebar out again (now a no-op).
        FocusedMediaPresentedElsewhere = shown;
    }

    private void HideMediaSidebarContent()
    {
        CancelMediaSidebarLoad();
        _sidebarFollowTimer?.Stop();
        _sidebarAnnounceTimer?.Stop();
        _sidebarPlaceholderTimer?.Stop();
        _sidebarThrottle.Reset();
        _sidebarPostKey = null;
        _sidebarSource = null;
        _mediaSidebarDirty = false;
        CancelSidebarStripLoad();
        OfferSidebarVideo(null, null);
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

    private string? MediaSidebarHint()
    {
        ReaderMediaPreferences media = ReaderMedia;
        double width = (_largeReader ? _largeReaderFrame?.Bounds.Width : ArchiveReaderPane.Bounds.Width) ?? 0;
        bool on = ReaderMediaSidebarRules.IsOn(media, _largeReader);
        if (on && ReaderMediaSidebarRules.IsCollapsedByWidth(media, width))
            return Texts.T("reader.sidebar.collapsed", media.Clamped().SidebarMinimumReaderWidth);
        // The archive view while the sidebar shows only in the large reader: it says where it is (the switch shows it here too).
        if (!_largeReader && media.Sidebar && !on) return Texts.T("reader.sidebar.largeOnly");
        // Off and wide enough: the entry says what it does and which shortcut turns it on (nothing turns on by itself).
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

    /// <summary>× and Escape inside the sidebar: turn it off for this view, the focus goes back to the selected post.</summary>
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
        ListBox posts = ArchivePosts;
        if (posts.SelectedItem is { } item && posts.ContainerFromItem(item) is Control container && container.Focus()) return;
        posts.Focus();
    }

    internal bool CanStepMediaSidebar(int delta) => !_ended && _mediaSidebarShown && !IsReaderDialogOpen
        && ArchivePosts.SelectedItem is ArchivePostRow { ShowsMedia: true } post
        && ReaderMediaSidebarRules.CanStep(_sidebarIndex, post.Attachments.Count, delta);

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
        if (_ended || !_mediaSidebarShown || IsReaderDialogOpen || CurrentSidebarAttachment() is not { OpenPreview: { } open }) return;
        open();
    }

    private ArchiveAttachmentRow? CurrentSidebarAttachment() =>
        ArchivePosts.SelectedItem is ArchivePostRow { ShowsMedia: true } post
            ? post.Attachments[ReaderMediaSidebarRules.ClampIndex(_sidebarIndex, post.Attachments.Count)] : null;

    // ---- following the selection -----------------------------------------------------------------------------------------------

    private void OnMediaSidebarSelectionChanged()
    {
        if (!_mediaSidebarShown || _ended) return;
        long now = _sidebarClock.ElapsedMilliseconds;
        // A selection that is only cleared (a page or thread being rebuilt) waits for the tick: the picture does not flash away.
        if (ArchivePosts.SelectedItem == null) _sidebarThrottle.MarkPending();
        else if (_sidebarThrottle.Request(now)) { RefreshMediaSidebar(); return; }
        DispatcherTimer timer = _sidebarFollowTimer ??= CreateSidebarTimer(() =>
        {
            if (_sidebarThrottle.Tick(_sidebarClock.ElapsedMilliseconds)) RefreshMediaSidebar();
        });
        if (!timer.IsEnabled)
        {
            timer.Interval = TimeSpan.FromMilliseconds(ArchivePosts.SelectedItem == null
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
    private void OnMediaSidebarFrameResized()
    {
        if (!_mediaSidebarShown || _mediaSidebar is not { Shown: { } shown } sidebar) return;
        (double Width, double Height) needed = ReaderMediaSidebarRules.PhysicalTarget(sidebar.Frame.Bounds.Size,
            TopLevel.GetTopLevel(sidebar)?.RenderScaling ?? 1);
        if (!ReaderMediaSidebarRules.NeedsLargerDecode(shown.PixelSize, needed, _sidebarSource)) return;
        OnMediaSidebarSelectionChanged();
    }

    /// <summary>The gallery uncovered the reader: a selection that changed meanwhile is shown now.</summary>
    internal void ResumeMediaSidebar()
    {
        if (_mediaSidebarDirty && _mediaSidebarShown) RefreshMediaSidebar();
    }

    private void CancelMediaSidebarLoad()
    {
        ++_sidebarRequest;
        CancellationTokenSource? loading = _sidebarLoading;
        _sidebarLoading = null;
        try { loading?.Cancel(); } catch (ObjectDisposedException) { }
        loading?.Dispose();
    }

    private void RefreshMediaSidebar()
    {
        if (_ended || !_mediaSidebarShown || _mediaSidebar is not { } sidebar) return;
        // Under the gallery nothing new is decoded; the reader catches up when it is uncovered.
        if (_obscuredByGallery) { _mediaSidebarDirty = true; return; }
        _mediaSidebarDirty = false;
        CancelMediaSidebarLoad();
        int request = _sidebarRequest;
        ArchivePostRow? post = ArchivePosts.SelectedItem as ArchivePostRow;
        string? key = post == null ? null : _thread?.Summary.Id + "\n" + post.Id;
        bool samePost = string.Equals(key, _sidebarPostKey, StringComparison.Ordinal);
        if (!samePost) { _sidebarIndex = _sidebarPendingIndex ?? 0; _sidebarPendingIndex = null; _sidebarPostKey = key; }
        if (post == null || post.IsStub)
        {
            CancelSidebarStripLoad();
            OfferSidebarVideo(null, null);
            sidebar.ShowEmpty();
            ScheduleSidebarAnnouncement(Texts.T("reader.sidebar.regionEmpty"));
            return;
        }
        if (!post.ShowsMedia)
        {
            CancelSidebarStripLoad();
            sidebar.ClearStrip();
            OfferSidebarVideo(null, null);
            string reason = Texts.T(post.HasMediaHiddenLine ? "reader.sidebar.hidden" : "reader.sidebar.noMedia", post.Id);
            sidebar.Describe(post.NumberLabel, string.Empty, string.Empty, false, post.CopyNumberLabel);
            sidebar.ConfigureFavorite(null);
            sidebar.Dim(reason);
            ScheduleSidebarAnnouncement(reason);
            return;
        }
        int count = post.Attachments.Count;
        _sidebarIndex = ReaderMediaSidebarRules.ClampIndex(_sidebarIndex, count);
        ArchiveAttachmentRow row = post.Attachments[_sidebarIndex];
        CapturedAttachmentText file = CapturedAttachmentText.Of(row);
        // The position of the medium in its post is the strip's and the announcement's; the header line names the post, the file and its size.
        string postLine = post.NumberLabel;
        string size = file.Size is > 0 and long bytes ? SizeText.Format(bytes) : string.Empty;
        sidebar.Describe(postLine, file.Name, size, row.OpenPreview != null, post.CopyNumberLabel);
        sidebar.ConfigureFavorite(row);
        UpdateSidebarStrip(sidebar, post, samePost);
        OfferSidebarVideo(post, row);
        ScheduleSidebarAnnouncement(ReaderMediaSidebarRules.RegionText(post.Id, _sidebarIndex, count, file));
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sidebarLoading = loading;
        MediaSidebarTask = LoadMediaSidebarAsync(sidebar, post, row, size, request, loading.Token);
    }

    // ---- the thumbnail strip of a post with several files (R11) ----------------------------------------------------------------------

    private void CancelSidebarStripLoad()
    {
        CancellationTokenSource? loading = _sidebarStripLoading;
        _sidebarStripLoading = null;
        try { loading?.Cancel(); } catch (ObjectDisposedException) { }
        loading?.Dispose();
    }

    // The strip shows the files of the focused post (at most the configured number at once); only a new layout (another post, another
    // window) loads thumbnails, and only the missing ones. They are decoded small, through the reader's thumbnail gate and cache.
    private void UpdateSidebarStrip(ReaderMediaSidebar sidebar, ArchivePostRow post, bool samePost)
    {
        int count = post.Attachments.Count;
        if (!sidebar.ShowStrip(count, _sidebarIndex, ReaderMedia.Clamped().StripLimit, index => CapturedAttachmentText.Of(post.Attachments[index]).Name, samePost))
        {
            if (!sidebar.StripVisible) CancelSidebarStripLoad();
            return;
        }
        CancelSidebarStripLoad();
        List<int> missing = sidebar.StripIndexes.Where(index => !sidebar.HasStripPicture(index)).ToList();
        if (missing.Count == 0) return;
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sidebarStripLoading = loading;
        MediaSidebarStripTask = LoadSidebarStripAsync(sidebar, post, missing, loading.Token);
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

    private bool IsCurrentSidebarRequest(int request) => !_ended && _mediaSidebarShown && request == _sidebarRequest;

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

    private async Task LoadMediaSidebarAsync(ReaderMediaSidebar sidebar, ArchivePostRow post, ArchiveAttachmentRow row, string size,
        int request, CancellationToken token)
    {
        // A warm cache answers within a frame and the old picture simply gives way; a cold one shows the placeholder (with the file
        // line already above it) after PlaceholderDelayMs instead of a stale picture.
        DispatcherTimer placeholder = _sidebarPlaceholderTimer ??= CreateSidebarTimer(() =>
        {
            if (IsCurrentSidebarRequest(_sidebarPlaceholderRequest)) _mediaSidebar?.ShowPlaceholder(Texts.T("reader.sidebar.loading"));
        });
        _sidebarPlaceholderRequest = request;
        placeholder.Stop();
        placeholder.Interval = TimeSpan.FromMilliseconds(ReaderMediaSidebarRules.PlaceholderDelayMs);
        placeholder.Start();
        try
        {
            (double Width, double Height) target = MediaSidebarTarget(sidebar);
            GalleryEntry? entry = row.ResolveEntry == null ? null : await row.ResolveEntry(token);
            if (!IsCurrentSidebarRequest(request)) return;
            if (entry == null)
            {
                sidebar.ShowPlaceholder(Texts.T("archive.notSaved"));
                return;
            }
            if (entry.Kind == SavedMediaKind.Image)
            {
                (Bitmap bitmap, GalleryImageInfo info) = await GalleryImageCache.DecodeAsync(entry,
                    new GalleryDecodeRequest.Fit(target.Width, target.Height), token);
                if (!IsCurrentSidebarRequest(request)) { GalleryImageCache.Shared.Release(bitmap); return; }
                _sidebarSource = new PixelSize(info.Width, info.Height);
                sidebar.ShowPicture(bitmap, GalleryImageCache.Shared.Release, info.StillFrame,
                    ReaderMediaSidebarRules.MaximumDisplay(info.Width, _sidebarFit), ReaderMediaSidebarRules.MaximumDisplay(info.Height, _sidebarFit));
                sidebar.SetDetail((size.Length > 0 ? size + " · " : string.Empty) + $"{info.Width}×{info.Height}");
            }
            else if (entry.Kind == SavedMediaKind.Video && row.LoadSized is { } poster)
            {
                Bitmap? bitmap = await poster((int)target.Width, (int)target.Height, token);
                if (!IsCurrentSidebarRequest(request)) { if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }
                _sidebarSource = null;
                if (bitmap == null) sidebar.ShowPlaceholder(Texts.T("reader.sidebar.unavailable"));
                else sidebar.ShowPicture(bitmap, MediaTileThumbnails.Release, playable: true, double.PositiveInfinity, double.PositiveInfinity);
                ScheduleSidebarVideoAutoplay();
            }
            else
            {
                sidebar.ShowPlaceholder(Texts.T("reader.sidebar.unavailable"));
                return;
            }
            await PreloadMediaSidebarNeighboursAsync(post, target, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
        {
            // The shown medium's own failure: a localized sentence, the cause in the log (never the exception's English text).
            Serilog.Log.Warning(error, "Showing a media sidebar item failed");
            if (IsCurrentSidebarRequest(request)) sidebar.ShowPlaceholder(Texts.T("reader.sidebar.unavailable"));
        }
        }
        finally
        {
            if (IsCurrentSidebarRequest(request)) placeholder.Stop();
        }
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
