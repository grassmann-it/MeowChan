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
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan;

// The media panel of the Media page: the selected medium large beside the grid, the reader's media sidebar (ReaderMediaSidebar) in the Media page.
// The view is the reader's own passive control (header row with the post number, name and size, the star, "open in gallery" and the cross; the
// overlay arrows with the "n / total" counter; the minimal video control below a video); this file is the page's half of what ArchivePage does for it.
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
    private string _panelSizeText = string.Empty, _panelDimensions = string.Empty;
    private readonly ReaderMediaFollowThrottle _panelThrottle = new();
    private readonly Stopwatch _panelClock = Stopwatch.StartNew();
    private DispatcherTimer? _panelFollowTimer, _panelAnnounceTimer, _panelPlaceholderTimer;
    private CancellationTokenSource? _panelLoading;
    private int _panelRequest, _panelPlaceholderRequest;
    private PixelSize? _panelSource;
    private string _panelAnnouncement = string.Empty;
    private GalleryFitPreferences _panelFit = new();
    private bool _panelPointerContextPending;

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
    /// <summary>The drag handle on the panel's edge (smokes).</summary>
    internal GridSplitter PanelHandle => _panelHandle;
    /// <summary>The page width below which the panel hides (the setting); the shell binds it. Null: the default.</summary>
    internal Func<int>? PanelMinimumPageWidthSource { get; set; }

    internal int PanelMinimumPageWidth => Math.Clamp(PanelMinimumPageWidthSource?.Invoke() ?? MeowChan.Config.MediaPanelMinimumPageWidthDefault,
        MeowChan.Config.MediaPanelMinimumPageWidthMinimum, MeowChan.Config.MediaPanelMinimumPageWidthMaximum);

    /// <summary>
    /// The video options of the panel (the shell: the gallery's volume, start-muted choice and seek step, the reader's player and file limits; the panel
    /// always plays inline) and the gallery's fit limit, which the panel's picture follows. A change applies to what the panel shows now.
    /// </summary>
    internal void ConfigurePanel(ReaderInlineVideoOptions video, GalleryFitPreferences fit)
    {
        _panelFit = fit;
        _ = _panelVideoController.ConfigureAsync((video with { Enabled = true }).Clamped());
        if (_panelShown) { OfferPanelVideo(_panelRow); RefreshPanel(); }
    }

    // ---- the view -------------------------------------------------------------------------------------------------------------

    private ReaderMediaSidebar EnsurePanel()
    {
        if (_panel is { } existing) return existing;
        ReaderMediaSidebar panel = _panel = new ReaderMediaSidebar
        {
            Name = "MediaPanel", IsVisible = false, TitleKey = "media.panel.title", EmptyKey = "media.panel.empty",
            CloseKey = "media.panel.close", ToggleCommandId = null
        };
        // The sidebar's border faces the grid (set once; the side comes with the layout).
        panel[!Border.BorderBrushProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.CardEdge");
        CommandBindings.Apply(panel.Previous, "media.panel.previous", iconOnly: true);
        CommandBindings.Apply(panel.Next, "media.panel.next", iconOnly: true);
        panel.Localize();
        panel.ShowEmpty();
        panel.Frame.SizeChanged += (_, _) => OnPanelFrameResized();
        panel.Previous.Click += (_, e) => { e.Handled = true; StepPanel(-1); };
        panel.Next.Click += (_, e) => { e.Handled = true; StepPanel(1); };
        panel.Open.Click += (_, e) => { e.Handled = true; _ = OpenPanelMediaAsync(); };
        panel.GalleryRequested += (_, _) => _ = OpenPanelMediaAsync();
        panel.PictureActivated += (_, _) => OnPanelPictureActivated();
        panel.Close.Click += (_, e) => { e.Handled = true; ClosePanel(); };
        // In the panel the slot's activator covers the poster also while idle: the focusable "Play video here" (Enter plays).
        ReaderInlineVideoSlot slot = _panelVideo = new ReaderInlineVideoSlot(panel.FrameArea, activatorWhenIdle: true);
        panel.VideoRow.Children.Add(slot.Below);
        panel.AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        panel.AddHandler(PointerPressedEvent, OnPanelPointerPressed, RoutingStrategies.Tunnel);
        panel.AddHandler(ContextRequestedEvent, OnPanelContextRequested, RoutingStrategies.Bubble);
        _grid.AddHandler(KeyDownEvent, OnPanelGridKeyDown, RoutingStrategies.Tunnel);
        PropertyChanged += (_, e) =>
        {
            // The page is left (another page of the shell is shown): a playing video pauses; the players stay to continue.
            if (e.Property == IsVisibleProperty && e.NewValue is false) _ = PausePanelVideo();
        };
        DetachedFromVisualTree += (_, _) => _ = ReleasePanelVideos();
        return panel;
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
        _panelPlaceholderTimer?.Stop();
        _panelThrottle.Reset();
        _panelSource = null;
        _panelRow = _panelWanted = null;
        _panelSizeText = _panelDimensions = string.Empty;
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
    // its medium when its tile is still among the results (found by its key), else follows the selection; its counter, arrows and star are made anew.
    private void SyncPanelToRows()
    {
        if (_lifetime.IsCancellationRequested) return;
        if (!_panelShown || _panel == null) { _panelRow = _panelWanted = null; return; }
        MediaLibraryTileRow? previous = _panelRow ?? _panelWanted;
        MediaLibraryTileRow? rebound = previous == null ? null : RowOfTile(MediaLibraryTileKey.Of(previous.Tile));
        MediaLibraryTileRow? pick = rebound != null && _grid.Selection.IsSelected(rebound.Index) ? rebound : SelectedRow ?? rebound;
        bool sameMedium = pick != null && _panelRow != null && MediaLibraryTileKey.Of(pick.Tile) == MediaLibraryTileKey.Of(_panelRow.Tile);
        if (sameMedium)
        {
            // Same tile in a new list: no new decode, only the header, the counter, the arrows and the star.
            _panelRow = _panelWanted = pick;
            DescribePanel(_panel, pick!);
            return;
        }
        _panelThrottle.Reset();
        _panelWanted = pick;
        RefreshPanel();
    }

    private MediaLibraryTileRow? RowOfTile(MediaLibraryTileKey key)
    {
        if (_grid.ItemsSource is not IReadOnlyList<MediaLibraryTileRow> rows) return null;
        for (int i = 0; i < _tiles.Count && i < rows.Count; i++)
            if (MediaLibraryTileKey.Of(_tiles[i]) == key) return rows[i];
        return null;
    }

    private void RequestPanel(MediaLibraryTileRow? row)
    {
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
        if (!ReaderMediaSidebarRules.NeedsLargerDecode(shown.PixelSize, needed, _panelSource)) return;
        RequestPanel(_panelRow);
    }

    private void CancelPanelLoad()
    {
        ++_panelRequest;
        CancellationTokenSource? loading = _panelLoading;
        _panelLoading = null;
        try { loading?.Cancel(); } catch (ObjectDisposedException) { }
        loading?.Dispose();
    }

    private bool IsCurrentPanelRequest(int request) => !_lifetime.IsCancellationRequested && _panelShown && request == _panelRequest;

    // ---- showing the medium -------------------------------------------------------------------------------------------------------

    private void RefreshPanel()
    {
        if (_lifetime.IsCancellationRequested || !_panelShown || _panel is not { } panel) return;
        CancelPanelLoad();
        int request = _panelRequest;
        MediaLibraryTileRow? row = _panelWanted;
        bool same = row != null && _panelRow != null && MediaLibraryTileKey.Of(row.Tile) == MediaLibraryTileKey.Of(_panelRow.Tile);
        _panelRow = row;
        if (!same) { _panelSizeText = _panelDimensions = string.Empty; }
        if (row == null)
        {
            OfferPanelVideo(null);
            panel.ShowEmpty();
            ScheduleAnnouncement(Texts.T("media.panel.regionEmpty"));
            return;
        }
        MediaLibraryEntry entry = row.Entry;
        _panelSizeText = entry.Length is > 0 and long bytes ? SizeText.Format(bytes) : string.Empty;
        DescribePanel(panel, row);
        OfferPanelVideo(row);
        ScheduleAnnouncement(Texts.T("media.panel.region", row.Index + 1, _tiles.Count,
            ReaderMediaSidebarRules.FileText(new CapturedAttachmentText(row.Name, entry.Length))));
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _panelLoading = loading;
        PanelTask = LoadPanelAsync(panel, row, request, loading.Token);
    }

    private string PanelDetail() => _panelDimensions.Length == 0 ? _panelSizeText
        : _panelSizeText.Length == 0 ? _panelDimensions : _panelSizeText + " · " + _panelDimensions;

    // The header (post number, name, size and pixels), the counter and arrows over the grid's results, "open in gallery" and the star.
    private void DescribePanel(ReaderMediaSidebar panel, MediaLibraryTileRow row)
    {
        MediaLibraryEntry entry = row.Entry;
        string postLine = entry.PostId != null ? Texts.T("reader.postNumber", entry.PostId)
            : row.IsProviderMedium ? Texts.T("booru.media.tile.post", entry.BooruPostId!) : string.Empty;
        panel.Describe(postLine, row.Name, PanelDetail(), CanStepPanel(-1), CanStepPanel(1),
            OpenGallery != null && entry.Presence == MediaLibraryPresence.Present, _tiles.Count, row.Index);
        // The star reads the panel's row when it is asked, so a new presentation (own state changed) needs no new wiring.
        panel.ConfigureFavorite(PanelFavoriteAvailable, () => _panelRow?.State?.Favorite == MediaLibraryFlag.Yes,
            () => _panelRow is { } current ? ToggleTileFavoriteAsync(current.Entry) : Task.CompletedTask);
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

    private async Task LoadPanelAsync(ReaderMediaSidebar panel, MediaLibraryTileRow row, int request, CancellationToken token)
    {
        // A warm cache answers within a frame and the old picture simply gives way; a cold one shows the placeholder (with the file
        // line already above it) after PlaceholderDelayMs instead of a stale picture.
        DispatcherTimer placeholder = _panelPlaceholderTimer ??= CreatePanelTimer(() =>
        {
            if (IsCurrentPanelRequest(_panelPlaceholderRequest)) _panel?.ShowPlaceholder(Texts.T("reader.sidebar.loading"));
        });
        _panelPlaceholderRequest = request;
        placeholder.Stop();
        placeholder.Interval = TimeSpan.FromMilliseconds(ReaderMediaSidebarRules.PlaceholderDelayMs);
        placeholder.Start();
        try
        {
            (double Width, double Height) target = PanelTarget(panel);
            GalleryEntry? entry = await ResolvePanelEntryAsync(row.Entry, token);
            if (!IsCurrentPanelRequest(request)) return;
            if (entry == null)
            {
                panel.ShowPlaceholder(Texts.T("archive.notSaved"));
                return;
            }
            if (entry.Kind == SavedMediaKind.Image)
            {
                (Bitmap bitmap, GalleryImageInfo info) = await GalleryImageCache.DecodeAsync(entry, new GalleryDecodeRequest.Fit(target.Width, target.Height), token);
                if (!IsCurrentPanelRequest(request)) { GalleryImageCache.Shared.Release(bitmap); return; }
                _panelSource = new PixelSize(info.Width, info.Height);
                panel.ShowPicture(bitmap, GalleryImageCache.Shared.Release, info.StillFrame,
                    ReaderMediaSidebarRules.MaximumDisplay(info.Width, _panelFit), ReaderMediaSidebarRules.MaximumDisplay(info.Height, _panelFit));
                _panelDimensions = $"{info.Width}×{info.Height}";
                panel.SetDetail(PanelDetail());
            }
            else if (entry.Kind == SavedMediaKind.Video)
            {
                int width = (int)target.Width, height = (int)target.Height;
                Bitmap? bitmap = await MediaTileThumbnails.LoadPosterAsync(entry.Manifest, entry.Asset, PosterCacheDirectory, width, height,
                    MediaTileThumbnails.PosterKey(entry.Asset, width, height), token);
                if (!IsCurrentPanelRequest(request)) { if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }
                _panelSource = null;
                if (bitmap == null) panel.ShowPlaceholder(Texts.T("reader.sidebar.unavailable"));
                else panel.ShowPicture(bitmap, MediaTileThumbnails.Release, playable: true, double.PositiveInfinity, double.PositiveInfinity);
            }
            else panel.ShowPlaceholder(Texts.T("reader.sidebar.unavailable"));
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The shown medium's own failure: a localized sentence, the cause in the log (never the exception's English text).
            Serilog.Log.Warning(error, "Showing a media panel item failed");
            if (IsCurrentPanelRequest(request)) panel.ShowPlaceholder(Texts.T("reader.sidebar.unavailable"));
        }
        finally
        {
            if (IsCurrentPanelRequest(request)) placeholder.Stop();
        }
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
            offered ? token => ResolvePanelEntryAsync(entry!, token) : null, row?.Name ?? string.Empty, offered ? () => _ = OpenPanelMediaAsync() : null);
    }

    /// <summary>Every panel player pauses (the gallery opens over the page, the page is left); the players stay to continue later.</summary>
    internal Task PausePanelVideo() => _panelVideoController.PauseAllAsync();

    /// <summary>Every panel player is given back (the panel hides, the page ends).</summary>
    internal Task ReleasePanelVideos() => _panelVideoController.ReleaseAllAsync();

    // A click on the panel's picture: plays or pauses a video here; an image opens the gallery, as in the reader's sidebar.
    private void OnPanelPictureActivated()
    {
        if (_panelVideo is { IsOffered: true } slot) { _ = slot.ToggleAsync(); return; }
        _ = OpenPanelMediaAsync();
    }

    // ---- stepping, opening, closing --------------------------------------------------------------------------------------------

    internal bool CanStepPanel(int delta) => !_lifetime.IsCancellationRequested && _panelShown && _panelRow != null
        && MediaPanelFollow.Step(_panelRow.Index, _tiles.Count, delta) >= 0;

    /// <summary>The overlay arrows: the previous or next tile of the grid's current results is selected (and shown), the grid scrolls to it.</summary>
    internal void StepPanel(int delta)
    {
        if (!CanStepPanel(delta) || _panelRow is not { } row) return;
        int target = MediaPanelFollow.Step(row.Index, _tiles.Count, delta);
        if (target < 0) return;
        _grid.SelectedIndex = target;
        _grid.ScrollIntoView(target);
    }

    /// <summary>"Open in gallery" (and a click on an image in the panel): the gallery at the panel's medium, the same path as Enter on the tile.</summary>
    internal Task OpenPanelMediaAsync() => _panelRow is { } row ? OpenRowAsync(row) : Task.CompletedTask;

    /// <summary>The cross: the panel is turned off in the layout (saved); the focus goes back to the grid.</summary>
    internal void ClosePanel()
    {
        if (!_layout.Panel) return;
        if (_panel?.IsKeyboardFocusWithin == true) FocusGrid();
        ApplyLayout(_layout with { Panel = false });
    }

    // ---- keys and the menu -----------------------------------------------------------------------------------------------------------

    private void OnPanelGridKeyDown(object? sender, KeyEventArgs e) => TryRoutePanelKey(e, fromPanel: false);

    private void OnPanelKeyDown(object? sender, KeyEventArgs e) => TryRoutePanelKey(e, fromPanel: true);

    private bool TryRoutePanelKey(KeyEventArgs e, bool fromPanel)
    {
        if (e.Handled || !_panelShown || _lifetime.IsCancellationRequested) return false;
        MediaPanelKeyRoute route = MediaPanelKeys.Route(e.Key, e.KeyModifiers, id => CommandBindings.Matches(e, id), _panelShown,
            _panelVideo is { IsOffered: true }, CommandRouter.IsNativeInput(e.Source, e.Key), fromPanel, CanStepPanel(-1), CanStepPanel(1));
        switch (route.Kind)
        {
            case MediaPanelKeyKind.Video:
                e.Handled = true;
                _ = _panelVideo!.ExecuteAsync(route.Action);
                return true;
            case MediaPanelKeyKind.Previous:
                e.Handled = true;
                StepPanel(-1);
                return true;
            case MediaPanelKeyKind.Next:
                e.Handled = true;
                StepPanel(1);
                return true;
            case MediaPanelKeyKind.ReturnToGrid:
                e.Handled = true;
                FocusGrid();
                return true;
            default:
                return false;
        }
    }

    // The panel's own right click and Menu key: the same media menu as the tile's, with the playback group on top for a video.
    private void OnPanelPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Handled || _lifetime.IsCancellationRequested || IsNativeContextSource(e.Source) || _panel is not { } panel
            || !e.GetCurrentPoint(panel).Properties.IsRightButtonPressed || _panelRow == null) return;
        OpenSharedMediaMenu(_panelRow, panel.Frame, _panelVideo);
        _panelPointerContextPending = true;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => _panelPointerContextPending = false, DispatcherPriority.Background);
    }

    private void OnPanelContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Handled || IsNativeContextSource(e.Source) || _panel is not { } panel) return;
        if (_panelPointerContextPending) { _panelPointerContextPending = false; e.Handled = true; return; }
        if (_panelRow == null) return;
        OpenSharedMediaMenu(_panelRow, panel.Frame, _panelVideo);
        e.Handled = true;
    }
}
