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
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;

namespace MeowChan;

// The media sidebar of the large reader (R8). The detached reader pane sits in a frame grid with the sidebar and a splitter beside
// it (left or right). The sidebar follows the selected post (throttled while ↑/↓ is held), decodes through the gallery's decode API
// and cache (GalleryImageCache: no second decoder, no memory outside its budget; posters through the reader's ThumbnailDecodeGate),
// preloads the neighbouring posts in the background and announces the medium politely. While it is shown, it is the one surface that
// presents the focused media enlarged (FocusedMediaPresentedElsewhere), so the inline enlargement of the card steps back.
public partial class ArchivePage
{
    private Grid? _largeReaderFrame;
    private ReaderMediaSidebar? _mediaSidebar;
    private GridSplitter? _mediaSidebarSplitter;
    private bool _mediaSidebarShown, _mediaSidebarRight, _mediaSidebarDirty;
    private double _mediaSidebarAppliedWidth = double.NaN;
    private int _paneSplitColumn = 2;
    private IDisposable? _frameTabScope;
    private bool _tabScopeOnFrame;
    private readonly ReaderMediaFollowThrottle _sidebarThrottle = new();
    private readonly Stopwatch _sidebarClock = Stopwatch.StartNew();
    private DispatcherTimer? _sidebarFollowTimer, _sidebarAnnounceTimer, _sidebarPlaceholderTimer;
    private CancellationTokenSource? _sidebarLoading;
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

    /// <summary>The sidebar view (smoke seam); null until the large reader was opened once.</summary>
    internal ReaderMediaSidebar? MediaSidebar => _mediaSidebar;
    /// <summary>Whether the sidebar is on screen now (chosen, large reader, reader wide enough).</summary>
    internal bool IsMediaSidebarShown => _mediaSidebarShown;
    /// <summary>The latest sidebar load (smoke seam).</summary>
    internal Task MediaSidebarTask { get; private set; } = Task.CompletedTask;
    /// <summary>The medium of the focused post the sidebar shows (0-based).</summary>
    internal int MediaSidebarIndex => _sidebarIndex;

    private void InitializeMediaSidebar()
    {
        RegisterViewOption("reader.sidebar.toggle", () => ReaderMedia.Sidebar, ToggleMediaSidebar, note: MediaSidebarHint);
        ArchivePosts.SelectionChanged += (_, _) => OnMediaSidebarSelectionChanged();
    }

    // ---- frame ---------------------------------------------------------------------------------------------------------------

    /// <summary>The large reader's content: the pane in the frame with the (possibly hidden) sidebar.</summary>
    private Control WrapLargeReader(Grid pane)
    {
        Grid frame = _largeReaderFrame ??= CreateLargeReaderFrame();
        _paneSplitColumn = Grid.GetColumn(pane); // the embedded reader's column in ArchiveSplit, restored on the way back
        frame.Children.Add(pane);
        _mediaSidebarAppliedWidth = double.NaN;
        LayoutMediaSidebar(force: true);
        return frame;
    }

    /// <summary>Takes the pane out of the frame again; the sidebar gives its picture back.</summary>
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
        }
        return pane;
    }

    private Grid CreateLargeReaderFrame()
    {
        Grid frame = new() { Name = "ReaderLargeFrame", ColumnDefinitions = new ColumnDefinitions("0,0,*") };
        ReaderMediaSidebar sidebar = _mediaSidebar = new ReaderMediaSidebar { IsVisible = false };
        GridSplitter splitter = _mediaSidebarSplitter = new GridSplitter
        {
            Name = "ReaderMediaSidebarSplitter", Width = ReaderMediaSidebarRules.SplitterWidth,
            ResizeDirection = GridResizeDirection.Columns, IsVisible = false
        };
        Avalonia.Automation.AutomationProperties.SetName(splitter, Texts.T("reader.sidebar.splitter"));
        splitter.DragCompleted += (_, _) => SaveMediaSidebarWidth();
        splitter.KeyUp += (_, _) => SaveMediaSidebarWidth();
        frame.Children.Add(sidebar);
        frame.Children.Add(splitter);
        frame.SizeChanged += (_, _) => LayoutMediaSidebar();
        sidebar.Frame.SizeChanged += (_, _) => OnMediaSidebarFrameResized();
        sidebar.Previous.Click += (_, e) => { e.Handled = true; StepMediaSidebar(-1); };
        sidebar.Next.Click += (_, e) => { e.Handled = true; StepMediaSidebar(1); };
        sidebar.Open.Click += (_, e) => { e.Handled = true; OpenMediaSidebarMedia(); };
        sidebar.PictureActivated += (_, _) => OnMediaSidebarPictureActivated();
        CreateSidebarVideo(sidebar);
        sidebar.Close.Click += (_, e) => { e.Handled = true; CloseMediaSidebar(); };
        // The sidebar's keys reach the pane's View-menu shortcuts too (the toggle, the reader's other switches).
        frame.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Contains(sidebar)) OnReaderViewKeyDown(sidebar, e);
        }, RoutingStrategies.Bubble);
        return frame;
    }

    // Columns, side, width, visibility and the Tab scope from the preferences and the reader's width.
    private void LayoutMediaSidebar(bool force = false)
    {
        if (_largeReaderFrame is not { } frame || _mediaSidebar is not { } sidebar || _mediaSidebarSplitter is not { } splitter) return;
        ReaderMediaPreferences media = ReaderMedia;
        double readerWidth = frame.Bounds.Width > 0 ? frame.Bounds.Width : TopLevel.GetTopLevel(this)?.ClientSize.Width ?? 0;
        bool shown = _largeReader && frame.Children.Contains(ArchiveReaderPane) && ReaderMediaSidebarRules.IsShown(media, _largeReader, readerWidth);
        double width = ReaderMediaSidebarRules.EffectiveWidth(media.SidebarWidth, readerWidth);
        if (!force && shown == _mediaSidebarShown && media.SidebarRight == _mediaSidebarRight && (!shown || width == _mediaSidebarAppliedWidth)) return;
        _mediaSidebarRight = media.SidebarRight;
        _mediaSidebarAppliedWidth = width;
        Grid pane = ArchiveReaderPane;
        int sidebarColumn = _mediaSidebarRight ? 2 : 0, paneColumn = _mediaSidebarRight ? 0 : 2;
        Grid.SetColumn(sidebar, sidebarColumn);
        Grid.SetColumn(splitter, 1);
        Grid.SetColumn(pane, paneColumn);
        ColumnDefinitions columns = frame.ColumnDefinitions;
        ColumnDefinition side = columns[sidebarColumn], posts = columns[paneColumn];
        side.Width = shown ? new GridLength(width) : new GridLength(0);
        side.MinWidth = shown ? ReaderDisplayPreferences.MediaSidebarWidthMinimum : 0;
        side.MaxWidth = shown ? ReaderDisplayPreferences.MediaSidebarWidthMaximum : double.PositiveInfinity;
        posts.Width = new GridLength(1, GridUnitType.Star);
        posts.MinWidth = shown ? ReaderMediaSidebarRules.PostsMinimumWidth : 0;
        posts.MaxWidth = double.PositiveInfinity;
        columns[1].Width = shown ? new GridLength(ReaderMediaSidebarRules.SplitterWidth) : new GridLength(0);
        splitter.IsVisible = shown;
        sidebar.IsVisible = shown;
        // The sidebar's border faces the posts.
        sidebar.BorderThickness = new Thickness(_mediaSidebarRight ? 1 : 0, 0, _mediaSidebarRight ? 0 : 1, 0);
        sidebar[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.CardEdge");
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
        OfferSidebarVideo(null, null);
        _mediaSidebar?.ShowEmpty();
    }

    private void SaveMediaSidebarWidth()
    {
        if (_ended || !_mediaSidebarShown || _largeReaderFrame is not { } frame) return;
        double actual = frame.ColumnDefinitions[_mediaSidebarRight ? 2 : 0].ActualWidth;
        if (actual <= 0) return;
        int width = ReaderMediaSidebarRules.ClampWidth(actual);
        if (width == ReaderMedia.SidebarWidth) return;
        // Kept without a re-layout (the column already has this width) and written alone, like the View menu's switches.
        _readerDisplayPreferences.Media = ReaderMedia with { SidebarWidth = width };
        _mediaSidebarAppliedWidth = ReaderMediaSidebarRules.EffectiveWidth(width, frame.Bounds.Width);
        if (SaveReaderMediaSwitches is { } save) _ = SaveMediaSwitchAsync(save(config => config.ReaderMediaSidebarWidth = width));
    }

    // ---- switch and commands -------------------------------------------------------------------------------------------------

    private string? MediaSidebarHint()
    {
        if (!_largeReader) return Texts.T("reader.sidebar.largeOnly");
        double width = _largeReaderFrame?.Bounds.Width ?? 0;
        return ReaderMediaSidebarRules.IsCollapsedByWidth(ReaderMedia, width)
            ? Texts.T("reader.sidebar.collapsed", ReaderMedia.Clamped().SidebarMinimumReaderWidth) : null;
    }

    internal bool CanToggleMediaSidebar => !_ended && !IsFilenameSearchOpen && !IsReaderDialogOpen;

    /// <summary>The View menu switch and Primary+Shift+M: turns the sidebar on or off and stores the choice.</summary>
    internal void ToggleMediaSidebar()
    {
        if (!CanToggleMediaSidebar) return;
        bool focusInside = _mediaSidebar?.IsKeyboardFocusWithin == true;
        if (focusInside && ReaderMedia.Sidebar) FocusSelectedPostOrList();
        ToggleMediaSwitch(media => media.Sidebar, (media, on) => media with { Sidebar = on }, (config, on) => config.ReaderMediaSidebar = on);
    }

    /// <summary>× and Escape inside the sidebar: turn it off, the focus goes back to the selected post.</summary>
    internal void CloseMediaSidebar()
    {
        if (!ReaderMedia.Sidebar) return;
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

    /// <summary>◀ ▶ and Alt+Shift+←/→: the previous or next medium of the focused post.</summary>
    internal void StepMediaSidebar(int delta)
    {
        if (!CanStepMediaSidebar(delta) || ArchivePosts.SelectedItem is not ArchivePostRow post) return;
        _sidebarIndex = ReaderMediaSidebarRules.Step(_sidebarIndex, post.Attachments.Count, delta);
        RefreshMediaSidebar();
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
        if (!string.Equals(key, _sidebarPostKey, StringComparison.Ordinal)) { _sidebarIndex = 0; _sidebarPostKey = key; }
        if (post == null || post.IsStub)
        {
            OfferSidebarVideo(null, null);
            sidebar.ShowEmpty();
            ScheduleSidebarAnnouncement(Texts.T("reader.sidebar.regionEmpty"));
            return;
        }
        if (!post.ShowsMedia)
        {
            OfferSidebarVideo(null, null);
            string reason = Texts.T(post.HasMediaHiddenLine ? "reader.sidebar.hidden" : "reader.sidebar.noMedia", post.Id);
            sidebar.Describe(post.NumberLabel, string.Empty, string.Empty, false, false, false);
            sidebar.ConfigureFavorite(null);
            sidebar.Dim(reason);
            ScheduleSidebarAnnouncement(reason);
            return;
        }
        int count = post.Attachments.Count;
        _sidebarIndex = ReaderMediaSidebarRules.ClampIndex(_sidebarIndex, count);
        ArchiveAttachmentRow row = post.Attachments[_sidebarIndex];
        CapturedAttachmentText file = CapturedAttachmentText.Of(row);
        string postLine = count > 1 ? post.NumberLabel + " · " + Texts.T("reader.sidebar.position", _sidebarIndex + 1, count) : post.NumberLabel;
        string size = file.Size is > 0 and long bytes ? SizeText.Format(bytes) : string.Empty;
        sidebar.Describe(postLine, file.Name, size, ReaderMediaSidebarRules.CanStep(_sidebarIndex, count, -1),
            ReaderMediaSidebarRules.CanStep(_sidebarIndex, count, 1), row.OpenPreview != null);
        sidebar.ConfigureFavorite(row);
        OfferSidebarVideo(post, row);
        ScheduleSidebarAnnouncement(ReaderMediaSidebarRules.RegionText(post.Id, _sidebarIndex, count, file));
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sidebarLoading = loading;
        MediaSidebarTask = LoadMediaSidebarAsync(sidebar, post, row, size, request, loading.Token);
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
            if (IsCurrentSidebarRequest(request))
                sidebar.ShowPlaceholder(string.IsNullOrWhiteSpace(error.Message) ? Texts.T("reader.sidebar.unavailable") : error.Message);
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
        foreach (ArchiveAttachmentRow neighbour in neighbours)
        {
            token.ThrowIfCancellationRequested();
            if (neighbour.ResolveEntry == null || await neighbour.ResolveEntry(token) is not { Kind: SavedMediaKind.Image } entry) continue;
            try
            {
                (Bitmap bitmap, _) = await GalleryImageCache.DecodeThrough(GalleryImageCache.Shared, GalleryImageDecoder.DecodeBackgroundAsync,
                    entry, fit, token);
                GalleryImageCache.Shared.Release(bitmap);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { } // A neighbour that cannot be decoded is simply not ready early.
        }
    }
}
