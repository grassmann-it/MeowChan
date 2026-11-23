using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan;

// The media panel of the Media page: the selected medium large beside the grid, the reader's media sidebar (ReaderMediaSidebar) in the Media page.
// The view is the reader's own passive control (header row with the post number, name and size, the star, "open in gallery" and the cross; the
// minimal video control below a video; no arrows and no counter: the panel shows the one selected medium); this file is the page's half of what ArchivePage does for it.
// The panel follows the grid's selection (the first change after a quiet moment at once, a held arrow key at most every 120 ms), decodes only the
// shown medium through the gallery's decode API and cache (GalleryImageCache: no second decoder, no memory outside its budget; a video's poster
// through the thumbnail cache) and preloads nothing. A single click or an arrow key in the grid only selects; double click and Enter still open the
// gallery. Space, M, L, ",", ".", X and C are the gallery's video commands from the bindings and act on the panel's video while the grid or the panel
// has the focus (never in a text field). Its place in the body row is MediaPage.Layout.cs; it hides by itself on a narrow page and keeps the choice.
public sealed partial class MediaPage
{
    private ReaderMediaSidebar? _panel;
    private ReaderInlineVideoSlot? _panelVideo;
    private readonly ReaderInlineVideoController _panelVideoController = new();
    private bool _panelShown, _panelCollapsed;
    private MediaLibraryTileRow? _panelRow, _panelWanted;
    private string _panelDimensions = string.Empty;
    private readonly ReaderMediaFollowThrottle _panelThrottle = new();
    private readonly Stopwatch _panelClock = Stopwatch.StartNew();
    private DispatcherTimer? _panelFollowTimer, _panelAnnounceTimer;
    private CancellationTokenSource? _panelLoading;
    // The load tokens, the shown medium and its decoded size live in the view's load state (ReaderMediaSidebarLoadState).
    private string _panelAnnouncement = string.Empty;
    private GalleryFitPreferences _panelFit = new();
    private readonly PressOpenedContextGate _panelPointerContext = new();

    /// <summary>The panel view (smoke and test seam); null until the panel was shown once.</summary>
    internal ReaderMediaSidebar? Panel => _panel;
    /// <summary>Whether the panel is on screen now (chosen in the layout, and the page wide enough).</summary>
    internal bool IsPanelShown => _panelShown;
    /// <summary>Whether the panel is chosen but hidden because the page is narrower than the setting (the choice stays).</summary>
    internal bool IsPanelCollapsedByWidth => _panelCollapsed;
    /// <summary>The tile the panel shows now; null while it shows nothing.</summary>
    internal MediaLibraryTileRow? PanelRow => _panelRow;
    /// <summary>The latest panel load (smoke and test seam).</summary>
    internal Task PanelTask { get; private set; } = Task.CompletedTask;
    /// <summary>The panel's video slot (smoke and test seam); null until the panel was shown once.</summary>
    internal ReaderInlineVideoSlot? PanelVideo => _panelVideo;
    /// <summary>The panel's inline players (smoke and test seam).</summary>
    internal ReaderInlineVideoController PanelVideoController => _panelVideoController;
    internal PlaybackArbitrationGate? PlaybackGate { get => _panelVideoController.PlaybackGate; set => _panelVideoController.PlaybackGate = value; }
    /// <summary>The drag handle on the panel's edge (smokes).</summary>
    internal GridSplitter PanelHandle => _panelHandle;
    /// <summary>The page width below which the panel hides (the setting); the shell binds it. Null: the default.</summary>
    internal Func<int>? PanelMinimumPageWidthSource { get; set; }

    internal int PanelMinimumPageWidth => Math.Clamp(PanelMinimumPageWidthSource?.Invoke() ?? MeowChan.Config.MediaPanelMinimumPageWidthDefault,
        MeowChan.Config.MediaPanelMinimumPageWidthMinimum, MeowChan.Config.MediaPanelMinimumPageWidthMaximum);

    /// <summary>The profile's timeline hover-preview cache (the gallery's own); the shell sets it. Videos started after it is set show the preview on their timeline.</summary>
    internal void ConfigureScrubPreviewCache(VideoScrubPreviewCache? cache) => _panelVideoController.ScrubCache = cache;

    /// <summary>
    /// The video options of the panel (the shell: the gallery's volume, start-muted choice and seek step, the reader's player and file limits; the panel
    /// always plays inline) and the gallery's fit limit, which the panel's picture follows. A change applies to what the panel shows now; the shell
    /// calls it after the settings were saved and after a preference changed in the gallery (the volume) was written, so only a new fit limit decodes the
    /// picture again (a volume or seek step is read when the next player is made).
    /// </summary>
    internal void ConfigurePanel(ReaderInlineVideoOptions video, GalleryFitPreferences fit)
    {
        bool fitChanged = _panelFit != fit;
        _panelFit = fit;
        _ = _panelVideoController.ConfigureAsync((video with { Enabled = true }).Clamped());
        if (!_panelShown) return;
        OfferPanelVideo(_panelRow);
        if (fitChanged) RefreshPanel();
    }

    // ---- the view -------------------------------------------------------------------------------------------------------------

    private ReaderMediaSidebar EnsurePanel()
    {
        if (_panel is { } existing) return existing;
        ReaderMediaSidebar panel = _panel = new ReaderMediaSidebar
        {
            Name = "MediaPanel", IsVisible = false, UseGalleryCursor = true, TitleKey = "media.panel.title", EmptyKey = "media.panel.empty",
            CloseKey = "media.panel.close", ToggleCommandId = "media.panel.toggle", ShowEmptyTitle = true, DecisionKeyContext = "media"
        };
        // The sidebar's border faces the grid (set once; the side comes with the layout).
        panel[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.CardEdge");
        // The full-height panel owns the header and card, with a horizontal inset from both edges.
        panel.Padding = new Thickness(16, 0, 16, 0);
        panel.Localize();
        panel.ShowEmpty();
        // The drag handle's name and tip exist from the start, not only after the first language change.
        string resize = Texts.T("media.panel.resize");
        Avalonia.Automation.AutomationProperties.SetName(_panelHandle, resize);
        ToolTip.SetTip(_panelHandle, resize);
        panel.Frame.SizeChanged += (_, _) => OnPanelFrameResized();
        panel.OpenTagsAsync = anchor => _panelRow is { } row
            ? RunMediaCommandOnPanelAsync("media.tags.edit", row, anchor) : Task.CompletedTask;
        panel.Open.Click += (_, e) => { e.Handled = true; StartOpenPanelMedia(); };
        panel.GalleryRequested += (_, _) => StartOpenPanelMedia();
        panel.PictureActivated += (_, _) => OnPanelPictureActivated();
        panel.Close.Click += (_, e) => { e.Handled = true; ClosePanel(); };
        // In the panel the slot's activator covers the poster also while idle: the focusable "Play video here" (Enter plays).
        ReaderInlineVideoSlot slot = _panelVideo = new ReaderInlineVideoSlot(panel.FrameArea, activatorWhenIdle: true)
        {
            OwnerClaims = MediaPanelKeys.PageClaims, PointerPlayFocus = FocusGridAfterPointerPlay
        };
        _panelVideoController.NavigationKeys = TryNavigateGridFromPanel;
        panel.VideoRow.Children.Add(slot.Below);
        panel.AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        panel.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
        panel.AddHandler(PointerReleasedEvent, OnPanelPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        panel.AddHandler(ContextRequestedEvent, OnPanelContextRequested, RoutingStrategies.Bubble);
        _grid.AddHandler(KeyDownEvent, OnPanelGridKeyDown, RoutingStrategies.Tunnel);
        // A minimised window pauses the video (the shell pauses it when another page or the gallery covers this one).
        AttachedToVisualTree += (_, _) => WatchPanelWindow(TopLevel.GetTopLevel(this) as Window);
        DetachedFromVisualTree += (_, _) => { WatchPanelWindow(null); _ = ReleasePanelVideos(); };
        if (TopLevel.GetTopLevel(this) is Window attached) WatchPanelWindow(attached);
        return panel;
    }

    // Up, Down and the page keys pressed in the panel's video controls move the grid's selection (the panel follows it), as they do with the focus on the grid:
    // the key goes on to the grid, which has the focus from now on. The timeline keeps Left and Right (they seek).
    private bool TryNavigateGridFromPanel(KeyEventArgs e)
    {
        if (e.Handled || _lifetime.IsCancellationRequested || !_panelShown || !ReaderVideoKeys.IsPostNavigationKey(e.Key)) return false;
        _grid.Focus(NavigationMethod.Pointer);
        _grid.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = e.Key, KeyModifiers = e.KeyModifiers, PhysicalKey = e.PhysicalKey, KeyDeviceType = e.KeyDeviceType, Source = _grid });
        return true;
    }

    // After a click on the panel's play button (which hides itself) the focus goes to the grid, unless a menu, dialog or field took it meanwhile.
    private void FocusGridAfterPointerPlay()
    {
        if (_lifetime.IsCancellationRequested || !_panelShown) return;
        object? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        if (focused is Visual visual && (_panel == null || !visual.GetSelfAndVisualAncestors().Any(node => ReferenceEquals(node, _panel)))) return;
        _grid.Focus(NavigationMethod.Pointer);
    }

    private Window? _panelWindow;

    private void WatchPanelWindow(Window? window)
    {
        if (ReferenceEquals(window, _panelWindow)) return;
        if (_panelWindow != null) _panelWindow.PropertyChanged -= OnPanelWindowPropertyChanged;
        _panelWindow = window;
        if (window != null) window.PropertyChanged += OnPanelWindowPropertyChanged;
    }

    private void OnPanelWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Window.WindowStateProperty && e.NewValue is WindowState.Minimized) _ = PausePanelVideo();
    }

    private bool _panelObscured, _panelDirty;

    /// <summary>The gallery covers the page (or uncovers it): the panel's video pauses and nothing new is decoded under it; a change made meanwhile shows when it is uncovered.</summary>
    internal void SetObscuredByGallery(bool covers)
    {
        if (_panelObscured == covers) return;
        _panelObscured = covers;
        if (covers) { _ = PausePanelVideo(); return; }
        if (_panelDirty && _panelShown) { _panelDirty = false; RefreshPanel(); }
    }

    // The body row (MediaPage.Layout.cs) calls this when the panel appears or disappears (the view was made and placed by then).
    private void OnPanelShownChanged(bool shown)
    {
        if (shown)
        {
            EnsurePanel();
            _panelThrottle.Reset();
            _panelWanted = _panelRow ?? SelectedRow;
            RefreshPanel();
        }
        else HidePanelContent();
    }

    private void HidePanelContent()
    {
        CancelPanelLoad();
        _panelFollowTimer?.Stop();
        _panelAnnounceTimer?.Stop();
        _panelThrottle.Reset();
        _panelRow = _panelWanted = null;
        _panelDimensions = string.Empty;
        OfferPanelVideo(null);
        _panel?.ShowEmpty();
        _ = ReleasePanelVideos();
    }

    private void LocalizePanel()
    {
        if (_panel is not { } panel) return;
        panel.Localize();
        string resize = Texts.T("media.panel.resize");
        Avalonia.Automation.AutomationProperties.SetName(_panelHandle, resize);
        ToolTip.SetTip(_panelHandle, resize);
        if (_panelShown) RefreshPanel();
    }

    // ---- following the selection ----------------------------------------------------------------------------------------------------

    // The grid's selection changed (a click, an arrow key, Ctrl or Shift): the panel shows the tile the change picked (MediaPanelFollow).
    private void OnPanelSelectionChanged(SelectionChangedEventArgs e)
    {
        if (!_panelShown || _lifetime.IsCancellationRequested) return;
        MediaLibraryTileRow? lastAdded = e.AddedItems.Count > 0 ? e.AddedItems[e.AddedItems.Count - 1] as MediaLibraryTileRow : null;
        MediaLibraryTileRow? pick = MediaPanelFollow.Pick(e.AddedItems.Count, lastAdded, _panelWanted ?? _panelRow,
            row => _grid.Selection.IsSelected(row.Index), () => SelectedRow);
        if (pick == null || ReferenceEquals(pick, _panelRow) && ReferenceEquals(pick, _panelWanted)) return;
        RequestPanel(pick);
    }

    // The rows were replaced (a new presentation: filter, order, new own state) or the selection restored (coming back from the gallery): the panel keeps
    // its medium when its tile is still among the results (found by its key), else follows the selection; its star and header are made anew.
    private void SyncPanelToRows()
    {
        if (_lifetime.IsCancellationRequested) return;
        if (!_panelShown || _panel == null) { _panelRow = _panelWanted = null; return; }
        MediaLibraryTileRow? previous = _panelRow ?? _panelWanted;
        MediaLibraryTileRow? rebound = previous == null ? null : RowOfTile(MediaLibraryTileKey.Of(previous.Tile), previous.Index);
        MediaLibraryTileRow? pick = rebound != null && _grid.Selection.IsSelected(rebound.Index) ? rebound : SelectedRow ?? rebound;
        bool sameMedium = pick != null && _panelRow != null && MediaLibraryTileKey.Of(pick.Tile) == MediaLibraryTileKey.Of(_panelRow.Tile);
        if (sameMedium)
        {
            // Same tile in a new list: no new decode, only the header and the star.
            _panelRow = _panelWanted = pick;
            DescribePanel(_panel, pick!);
            return;
        }
        _panelThrottle.Reset();
        _panelWanted = pick;
        RefreshPanel();
    }

    // The tile's row by its key. A presentation that kept the order (the own state changed) finds it at its old place at once (<paramref name="hint"/>);
    // only a reordered or filtered one walks the tiles.
    private MediaLibraryTileRow? RowOfTile(MediaLibraryTileKey key, int hint = -1)
    {
        if (_grid.ItemsSource is not IReadOnlyList<MediaLibraryTileRow> rows) return null;
        if (hint >= 0 && hint < _tiles.Count && hint < rows.Count && MediaLibraryTileKey.Of(_tiles[hint]) == key) return rows[hint];
        for (int i = 0; i < _tiles.Count && i < rows.Count; i++)
            if (MediaLibraryTileKey.Of(_tiles[i]) == key) return rows[i];
        return null;
    }

    private void RequestPanel(MediaLibraryTileRow? row)
    {
        if (!ReferenceEquals(_panelWanted, row)) CancelPanelLoad(); // retire now, not after the follow throttle
        _panelWanted = row;
        long now = _panelClock.ElapsedMilliseconds;
        if (_panelThrottle.Request(now)) { RefreshPanel(); return; }
        DispatcherTimer timer = _panelFollowTimer ??= CreatePanelTimer(() =>
        {
            if (_panelThrottle.Tick(_panelClock.ElapsedMilliseconds)) RefreshPanel();
        });
        if (!timer.IsEnabled)
        {
            timer.Interval = TimeSpan.FromMilliseconds(_panelThrottle.Remaining(now));
            timer.Start();
        }
    }

    private static DispatcherTimer CreatePanelTimer(Action tick)
    {
        DispatcherTimer timer = new();
        timer.Tick += (_, _) => { timer.Stop(); tick(); };
        return timer;
    }

    // A wider or taller picture frame than the shown decode covers asks for one larger decode (through the same throttle).
    private void OnPanelFrameResized()
    {
        if (!_panelShown || _panel is not { Shown: { } shown } panel) return;
        (double Width, double Height) needed = ReaderMediaSidebarRules.PhysicalTarget(panel.Frame.Bounds.Size, TopLevel.GetTopLevel(panel)?.RenderScaling ?? 1);
        if (!ReaderMediaSidebarRules.NeedsLargerDecode(shown.PixelSize, needed, panel.Load.ShownSource)) return;
        RequestPanel(_panelWanted ?? _panelRow); // a resize must not replace a pending selection with the old row
    }

    private void CancelPanelLoad()
    {
        _panel?.Load.Cancel();
        CancellationTokenSource? loading = _panelLoading;
        _panelLoading = null;
        try { loading?.Cancel(); } catch (ObjectDisposedException) { }
        loading?.Dispose();
    }

    private bool IsCurrentPanelRequest(int request) => !_lifetime.IsCancellationRequested && _panelShown && _panel is { } panel && panel.Load.IsCurrent(request);

    // ---- showing the medium -------------------------------------------------------------------------------------------------------

    private void RefreshPanel()
    {
        if (_lifetime.IsCancellationRequested || !_panelShown || _panel is not { } panel) return;
        // Under the gallery nothing new is decoded; the panel catches up when it is uncovered.
        if (_panelObscured) { _panelDirty = true; return; }
        _panelDirty = false;
        CancelPanelLoad();
        MediaLibraryTileRow? row = _panelWanted;
        bool same = row != null && _panelRow != null && MediaLibraryTileKey.Of(row.Tile) == MediaLibraryTileKey.Of(_panelRow.Tile);
        _panelRow = row;
        if (!same) { _panelDimensions = string.Empty; }
        if (row == null)
        {
            OfferPanelVideo(null);
            panel.ShowEmpty();
            ScheduleAnnouncement(Texts.T("media.panel.regionEmpty"));
            return;
        }
        MediaLibraryEntry entry = row.Entry;
        // Tom (16.11.2026): the medium already shown (a larger frame, a changed fit limit, a rebuilt grid) keeps its picture: no load while it covers the
        // frame, otherwise a larger decode that swaps in place (ReaderMediaSidebarLoadState).
        string mediumKey = PanelVideoKey(entry);
        (double Width, double Height) target = PanelTarget(panel);
        ReaderMediaLoadTicket ticket = panel.Load.Begin(mediumKey, target);
        DescribePanel(panel, row);
        OfferPanelVideo(row);
        // A refresh of the same medium (a larger frame, a changed setting) is no news for a screen reader: only another medium is announced.
        if (!same)
        {
            GalleryTagPanelPosition position = PanelPositionOf(row);
            ScheduleAnnouncement(Texts.T("media.panel.region", position.Number, position.Count,
                ReaderMediaSidebarRules.FileText(new CapturedAttachmentText(row.Name, entry.Length))));
        }
        if (ticket.UpToDate)
        {
            // Nothing to decode: only the gallery's fit limit may have changed (the picture's largest size); the pixel line comes from the shown picture.
            if (panel.Load.ShownSource is { } source)
            {
                panel.LimitPicture(ReaderMediaSidebarRules.MaximumDisplay(source.Width, _panelFit), ReaderMediaSidebarRules.MaximumDisplay(source.Height, _panelFit));
                _panelDimensions = $"{source.Width}×{source.Height}";
                panel.SetDetail(_panelDimensions);
            }
            return;
        }
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _panelLoading = loading;
        PanelTask = LoadPanelAsync(panel, row, mediumKey, ticket, target, loading.Token);
    }

    private void DescribePanel(ReaderMediaSidebar panel, MediaLibraryTileRow row)
    {
        MediaLibraryEntry entry = row.Entry;
        string postLine = entry.PostId != null ? Texts.T("reader.postNumber", entry.PostId)
            : row.IsProviderMedium ? Texts.T("booru.media.tile.post", entry.BooruPostId!) : string.Empty;
        panel.Describe(postLine, row.Name, _panelDimensions, OpenGallery != null && entry.Presence == MediaLibraryPresence.Present,
            threadPosition: PanelPositionOf(row), savedBytes: entry.Length);
        // Each write freezes the panel row even when the grid refreshes its selection.
        panel.ConfigureFavorite(PanelFavoriteAvailable, () => row.State?.Favorite == MediaLibraryFlag.Yes,
            () => ToggleTileFavoriteAsync(entry));
        bool Current() => !_lifetime.IsCancellationRequested && _building == null && _panelShown && ReferenceEquals(_panelRow, row);
        panel.ConfigureRating(() => Current() && !_selectionBusy && ExecuteMediaMenuActionAsync != null && row.State?.RatingKnown == true,
            () => _panelRow?.State?.UserRating, value => ExecuteMediaActionAsync(value is int rating ? "media.rating.set-" + rating : "media.rating.clear", entry, Current));
        panel.SetTags(row.State?.CanEditMediaTags == true ? row.State.MediaTags : null, TagsBlockedReason(entry, row.State));
        panel.SetSourceFacts(row.ThreadLabel, null, entry.PostedAt?.ToString("g", Texts.Culture), entry.Length, null, null);
    }

    private bool PanelFavoriteAvailable() => !_lifetime.IsCancellationRequested && ExecuteMediaMenuActionAsync != null
        && _panelRow is { Entry.Presence: MediaLibraryPresence.Present, Tile.IsGroup: false, State.Favorite: not MediaLibraryFlag.Unknown };

    // The frame's size in physical pixels; before the first layout an estimate from the configured width and the window height.
    private (double Width, double Height) PanelTarget(ReaderMediaSidebar panel)
    {
        Size frame = panel.Frame.Bounds.Size;
        if (frame.Width < 1 || frame.Height < 1)
        {
            double height = TopLevel.GetTopLevel(this)?.ClientSize.Height ?? 720;
            frame = new Size(Math.Max(1, _layout.PanelWidth - 32), Math.Max(1, height - 200));
        }
        return ReaderMediaSidebarRules.PhysicalTarget(frame, TopLevel.GetTopLevel(panel)?.RenderScaling ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
    }

    // The light entry of a tile for the decoder and the video player: the manifest of its directory and the asset the index describes (the same check as
    // the tile's preview: bytes that no longer match the index are not shown). The decode and the player verify the file themselves; the thread is not read.
    private async Task<GalleryEntry?> ResolvePanelEntryAsync(MediaLibraryEntry entry, CancellationToken token)
    {
        if (entry.Presence != MediaLibraryPresence.Present || entry.Directory == null || entry.AssetId == null) return null;
        MediaAssetSnapshot manifest = await OpenManifestAsync(entry.Directory, token);
        MediaAssetReference? asset = MediaTileThumbnails.Matching(manifest, entry);
        return asset == null ? null : new GalleryEntry(new GalleryAssetKey(entry.Thread, manifest.Directory, asset.AssetId), manifest, asset, null, null,
            !asset.IsCurrent, entry.Unassigned);
    }

    private Task LoadPanelAsync(ReaderMediaSidebar panel, MediaLibraryTileRow row, string key, ReaderMediaLoadTicket ticket,
        (double Width, double Height) target, CancellationToken token)
    {
        ReaderMediaSidebarImageLoad load = new(panel, key, ticket, _panelFit, () => IsCurrentPanelRequest(ticket.Request), token);
        return load.RunAsync(async ct =>
        {
            GalleryEntry? entry = await ResolvePanelEntryAsync(row.Entry, ct);
            ct.ThrowIfCancellationRequested();
            if (entry == null) return new ReaderMediaSidebarContent.Unavailable("archive.notSaved");
            if (entry.Kind == SavedMediaKind.Image)
            {
                (Bitmap bitmap, GalleryImageInfo info) = await GalleryImageCache.DecodeAsync(entry, new GalleryDecodeRequest.Fit(target.Width, target.Height), ct);
                return new ReaderMediaSidebarContent.Picture(bitmap, GalleryImageCache.Shared.Release, info.StillFrame, new PixelSize(info.Width, info.Height));
            }
            if (entry.Kind == SavedMediaKind.Video)
            {
                int width = (int)target.Width, height = (int)target.Height;
                Bitmap? bitmap = await MediaTileThumbnails.LoadPosterAsync(entry.Manifest, entry.Asset, PosterCacheDirectory, width, height,
                    MediaTileThumbnails.PosterKey(entry.Asset, width, height), ct);
                if (bitmap != null) return new ReaderMediaSidebarContent.Picture(bitmap, MediaTileThumbnails.Release, Playable: true);
            }
            return new ReaderMediaSidebarContent.Unavailable("reader.sidebar.unavailable");
        }, picture =>
        {
            _panelDimensions = picture.Source is { } source ? $"{source.Width}×{source.Height}" : string.Empty;
            panel.SetDetail(_panelDimensions);
        });
    }

    private void ScheduleAnnouncement(string text)
    {
        _panelAnnouncement = text;
        DispatcherTimer timer = _panelAnnounceTimer ??= CreatePanelTimer(() =>
        {
            if (!_lifetime.IsCancellationRequested && _panelShown) _panel?.Announce(_panelAnnouncement);
        });
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(ReaderMediaSidebarRules.AnnounceDelayMs);
        timer.Start();
    }

    // ---- the video -------------------------------------------------------------------------------------------------------------------

    private static string PanelVideoKey(MediaLibraryEntry entry) => string.Join("\n", entry.Directory, entry.AssetId, entry.Sha256 ?? string.Empty, entry.Length);

    /// <summary>The panel shows this tile (or none): a saved video offers inline playback in the panel; any other medium gives a player back.</summary>
    private void OfferPanelVideo(MediaLibraryTileRow? row)
    {
        if (_panelVideo is not { } slot) return;
        MediaLibraryEntry? entry = row?.Entry;
        bool offered = _panelShown && entry != null && MediaPanelKeys.CanOfferVideo(_panelVideoController.Options.Enabled,
            entry.Presence == MediaLibraryPresence.Present && entry.Directory != null && entry.AssetId != null, entry.Kind == SavedMediaKind.Video);
        slot.Offer(offered ? _panelVideoController : null, offered ? PanelVideoKey(entry!) : null,
            offered ? token => ResolvePanelEntryAsync(entry!, token) : null, row?.Name ?? string.Empty, offered ? StartOpenPanelMedia : null);
        if (_panel is { } panel) panel.SingleClickActs = slot.IsOffered;
        SchedulePanelVideoAutoplay(offered ? PanelVideoKey(entry!) : null);
    }

    // The panel is a viewer like the reader's sidebar: its video starts by itself (the shared "Autoplay videos in the media sidebar", default on)
    // once the selection rested, and only for a medium that was not started this way before (a refresh of the same tile starts nothing).
    private DispatcherTimer? _panelAutoplay;
    private string? _panelAutoplayKey, _panelAutoplayDone;

    private void SchedulePanelVideoAutoplay(string? key)
    {
        _panelAutoplay?.Stop();
        _panelAutoplayKey = key;
        if (key == null) { _panelAutoplayDone = null; return; }
        if (key == _panelAutoplayDone || _panelVideo is not { IsOffered: true, IsLive: false }
            || !ReaderInlineVideoRules.ShouldAutoplayViewer(_panelVideoController.Options, _panelShown && !_panelObscured)) return;
        DispatcherTimer timer = _panelAutoplay ??= new DispatcherTimer();
        timer.Interval = TimeSpan.FromMilliseconds(ReaderInlineVideoRules.AutoplayDelayMs);
        timer.Tick -= OnPanelAutoplayTick;
        timer.Tick += OnPanelAutoplayTick;
        timer.Start();
    }

    private void OnPanelAutoplayTick(object? sender, EventArgs e)
    {
        _panelAutoplay?.Stop();
        if (_lifetime.IsCancellationRequested || !_panelShown || _panelObscured || _panelAutoplayKey == null
            || _panelVideo is not { IsOffered: true, IsLive: false } video
            || !ReaderInlineVideoRules.ShouldAutoplayViewer(_panelVideoController.Options, shown: true)) return;
        _panelAutoplayDone = _panelAutoplayKey;
        _ = video.PlayAsync();
    }

    /// <summary>Every panel player pauses (the gallery opens over the page, the page is left); the players stay to continue later.</summary>
    internal Task PausePanelVideo() => _panelVideoController.PauseAllAsync();

    /// <summary>Every panel player is given back (the panel hides, the page ends).</summary>
    internal Task ReleasePanelVideos() => _panelVideoController.ReleaseAllAsync();

    // A sidebar click stays in the preview, regardless of the reader's activation
    // preference. Only double-click/the explicit Open action may leave this panel.
    private void OnPanelPictureActivated()
    {
        if (_panel is { } panel)
        {
            panel.Frame.Focusable = true;
            panel.Frame.Focus();
        }
        if (_panelVideo is { IsOffered: true } slot) _ = slot.ToggleAsync();
    }

    // ---- opening, closing ------------------------------------------------------------------------------------------------------

    /// <summary>"Open in gallery" (or a double-click in the panel): the gallery at the panel's medium, the same path as Enter on the tile.</summary>
    internal Task OpenPanelMediaAsync() => _panelRow is { } row ? OpenRowAsync(row) : Task.CompletedTask;

    // Every click, key and menu entry starts the open through the shared safe path: a failure is shown in the error banner, never lost in a discarded task.
    private void StartOpenPanelMedia() => OpenActions.Start(OpenPanelMediaAsync, "media.panel.open");

    /// <summary>The cross: the panel is turned off in the layout (saved); the focus goes back to the grid.</summary>
    internal void ClosePanel()
    {
        if (!_layout.Panel) return;
        if (_panel?.IsKeyboardFocusWithin == true) FocusGrid();
        ApplyLayout(_layout with { Panel = false });
    }

    // ---- keys and the menu -----------------------------------------------------------------------------------------------------------

    private void OnPanelGridKeyDown(object? sender, KeyEventArgs e) => TryRoutePanelKey(e, fromPanel: false);

    private void OnPanelKeyDown(object? sender, KeyEventArgs e)
    {
        _panelPointerContext.Reset(); // a key ends a pointer gesture whose release never came
        if (!TryRoutePanelKey(e, fromPanel: true)) TryRoutePanelPageKey(e); // the page's own keys (R, T, Q, I, Ctrl+E ...): MediaPage.PanelKeys.cs
    }

    private bool TryRoutePanelKey(KeyEventArgs e, bool fromPanel)
    {
        if (e.Handled || ShortcutService.IsCapturing || !_panelShown || _lifetime.IsCancellationRequested) return false;
        // With the focus in the panel, M / U / F act on the medium it shows (in the grid they act on the selection: OnGridKeyDown).
        if (fromPanel && TryRoutePanelFavoriteKey(e)) return true;
        MediaPanelKeyRoute route = MediaPanelKeys.Route(e.Key, e.KeyModifiers, id => CommandBindings.Matches(e, id), _panelShown,
            _panelVideo is { IsOffered: true }, CommandRouter.IsNativeInput(e.Source, e.Key), fromPanel);
        switch (route.Kind)
        {
            case MediaPanelKeyKind.Video:
                e.Handled = true;
                _ = _panelVideo!.ExecuteAsync(route.Action);
                return true;
            default:
                return false;
        }
    }

    // The panel's own right click, anywhere in it (picture, video row, header line with its buttons, the edge around the card), and the Menu key: the same
    // media menu as the tile's, with the playback group on top for a video. The press itself opens the menu; the ContextRequested that Avalonia 12 sends while
    // it processes the release of that click is consumed once. The flag lives from the press until that release is through (a click lasts far longer than a
    // Background tick: a flag dropped right after the press let the release open a second menu over the first).
    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _panelPointerContext.BeginPress(); // each press starts its own gesture
        if (e.Handled || _lifetime.IsCancellationRequested || IsNativeContextSource(e.Source) || _panel is not { } panel
            || !e.GetCurrentPoint(panel).Properties.IsRightButtonPressed || _panelRow == null) return;
        if (!_grid.Selection.SelectedItems.Contains(_panelRow)) _grid.SelectedItem = _panelRow;
        panel.Frame.Focusable = true;
        panel.Frame.Focus();
        OpenSharedMediaMenu(_panelRow, panel.Frame, _panelVideo);
        _panelPointerContext.MenuOpenedByPress();
        e.Handled = true;
    }

    // The release of that right click: the request Avalonia sends with it comes through the handlers after this one, so the flag ends on the next tick.
    private void OnPanelPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _panelPointerContext.Released(e.InitialPressMouseButton);
    }

    private void OnPanelContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || IsNativeContextSource(e.Source) || _panel is not { } panel) return;
        if (_panelPointerContext.TryConsume()) { e.Handled = true; return; }
        if (_panelRow == null) return;
        if (e.TryGetPosition(panel, out _))
        {
            if (!_grid.Selection.SelectedItems.Contains(_panelRow)) _grid.SelectedItem = _panelRow;
            panel.Frame.Focusable = true;
            panel.Frame.Focus();
        }
        OpenSharedMediaMenu(_panelRow, panel.Frame, _panelVideo);
        e.Handled = true;
    }
}
