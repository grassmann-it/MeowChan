using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using MeowChan.Tags;

namespace MeowChan;

public sealed partial class TaggingPage
{
    private const string PanelCommand = MediaPanelKeys.ToggleCommandId;
    private const double MinimumListWidth = 640;
    private readonly Grid _panelBody = new() { ColumnDefinitions = new ColumnDefinitions("*,0,0") };
    private readonly GridSplitter _panelHandle = new()
    {
        Name = "TaggingPanelHandle", Width = MediaPage.HandleWidth, IsVisible = false,
        ResizeDirection = GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext
    };
    private readonly ToggleButton _panelToggle = new() { Name = "TaggingPanelToggle", Focusable = false };
    private readonly ReaderMediaFollowThrottle _panelThrottle = new();
    private readonly HeldKeyLatch _panelFavoriteKey = new();
    private ReaderMediaSidebar? _taggingPanel;
    private DispatcherTimer? _panelAnnounceTimer;
    private CancellationTokenSource? _panelFollowDelay;
    private CancellationTokenSource? _panelLoading;
    private TaggingQueueRow? _panelRow, _panelWanted;
    private bool _panelEnabled = true, _panelTooNarrow, _panelFollowsPosted, _panelCursorPending, _panelResizePending;
    private bool _panelWriting, _panelOpening;
    private bool? _panelFileAvailable;
    private string? _panelMediaKey;
    private string _panelAnnouncement = string.Empty;
    private MediaPanelSide? _panelIconSide;
    private ReaderMediaPreferences _panelInfo = ReaderMediaPreferences.Default;

    internal TimeProvider PanelClock { get; init; } = TimeProvider.System;
    internal Func<GalleryEntry, GalleryDecodeRequest, CancellationToken, Task<(Bitmap Image, GalleryImageInfo Info)>> DecodePanelImageAsync
        { get; init; } = GalleryImageCache.DecodeAsync;
    internal Func<string, CancellationToken, Task<MediaAssetSnapshot>> OpenPanelManifestAsync { get; init; } = MediaAssetReader.OpenAsync;
    internal Func<MediaPageLayout>? ReadPanelLayout { get; set; }
    internal Action<int>? SavePanelWidth { get; set; }
    internal Action<bool>? SavePanelEnabled { get; set; }
    internal Action<Action<Config>>? SavePanelSetting { get; set; }
    internal Func<string, MediaLibraryEntry, Func<bool>, Task>? ExecutePanelDecisionAsync { get; set; }
    internal ToggleButton PanelToggle => _panelToggle;
    internal bool IsPanelShown => !_queueWork.Ended && _taggingPanel is { IsEffectivelyVisible: true };

    internal void ConfigurePanelInfo(ReaderMediaPreferences preferences)
    {
        _panelInfo = preferences;
        _taggingPanel?.ConfigureInfo(preferences);
    }

    internal void RestorePanelEnabled(bool enabled)
    {
        _panelEnabled = enabled;
        ApplyPanelLayout();
    }

    internal void TogglePanel()
    {
        if (_queueWork.Ended || _tagBusy) return;
        RestorePanelEnabled(!_panelEnabled);
        SavePanelEnabled?.Invoke(_panelEnabled);
    }

    internal void RunPanelBarCommand(string id)
    {
        if (_queueWork.Ended || _tagBusy) return;
        bool wasShown = IsPanelShown;
        if (!wasShown && id == "media.sidebar.info" && !_panelEnabled) TogglePanel();
        string? hint = EnsureTaggingPanel().RunBarCommand(id, wasShown);
        if (_panelTooNarrow) hint = Texts.T("tagging.page.panel.narrow");
        if (hint != null) ShowStatus(hint);
    }

    private void InitializePanel()
    {
        AutomationProperties.SetAutomationId(_panelToggle, "TaggingPanelToggle");
        AutomationProperties.SetAutomationId(_panelHandle, "TaggingPanelHandle");
        AutomationProperties.SetAutomationId(_actions, "TaggingActions");
        AutomationProperties.SetAutomationId(_listPresentation, "TaggingListPresentation");
        AutomationProperties.SetAutomationId(_status, "TaggingStatus");
        AutomationProperties.SetAutomationId(_grid, "TaggingGrid");
        foreach (ListBox list in new[] { _list, _grid })
            list.ContainerPrepared += (_, e) =>
                AutomationProperties.SetAutomationId(e.Container, list.Name + "Entry" + (e.Index + 1));
        _panelToggle.Click += (_, _) => TogglePanel();
        _panelHandle.DragCompleted += (_, _) => CommitPanelWidth();
        _panelHandle.KeyUp += (_, _) => CommitPanelWidth();
        DetachedFromVisualTree += (_, _) => StopPanel();
        foreach (ListBox list in new[] { _list, _grid })
        {
            list.GotFocus += (_, _) => QueuePanelFollow();
            list.SelectionChanged += (_, _) => QueuePanelFollow();
        }
        _grid.ContainerPrepared += (_, e) =>
        {
            if (e.Container.DataContext is not TaggingQueueRow row) return;
            (_, _, string? decisions) = DecisionOf(row.Item);
            AutomationProperties.SetHelpText(e.Container, decisions);
            ToolTip.SetTip(e.Container, decisions);
        };
    }

    private void LocalizePanel()
    {
        string resize = Texts.T("media.panel.resize");
        AutomationProperties.SetName(_panelHandle, resize);
        ToolTip.SetTip(_panelHandle, resize);
        _taggingPanel?.Localize();
        UpdatePanelToggle(GetPanelLayout());
    }

    private MediaPageLayout GetPanelLayout() =>
        ReadPanelLayout?.Invoke() ?? MediaPageLayout.Parse(_host.Config.MediaLayout, _host.Config.MediaBooruMode);

    private void UpdatePanelToggle(MediaPageLayout layout)
    {
        _panelToggle.IsChecked = _panelEnabled;
        if (_panelIconSide != layout.PanelSide)
        {
            _panelIconSide = layout.PanelSide;
            string icon = CommandCatalog.IconForState(PanelCommand, layout.PanelSide == MediaPanelSide.Left ? "left" : "right");
            _panelToggle.Content = CommandBindings.NamedIcon(icon);
            _panelToggle.Classes.Set(WorkspaceIcons.SquareClass, true);
        }

        string name = CommandCatalog.Label(PanelCommand);
        string? gesture = CommandCatalog.ShortcutTooltipDisplay(PanelCommand, "tagging");
        string tip = ReaderMediaSidebarRules.HeaderText(name, gesture ?? string.Empty,
            _panelTooNarrow ? Texts.T("tagging.page.panel.narrow") : string.Empty);
        AutomationProperties.SetName(_panelToggle, name);
        AutomationProperties.SetHelpText(_panelToggle, tip);
        ToolTip.SetTip(_panelToggle, tip);
    }

    internal void ApplyPanelLayout()
    {
        if (_queueWork.Ended) return;
        MediaPageLayout layout = GetPanelLayout();
        double room = Bounds.Width - MinimumListWidth - MediaPage.HandleWidth;
        _panelTooNarrow = Bounds.Width > 0 && (Bounds.Width < _host.Config.MediaPanelMinimumPageWidth || room < Config.MediaPanelWidthMinimum);
        bool visible = _panelEnabled && Bounds.Width > 0 && !_panelTooNarrow;
        bool wasVisible = _taggingPanel?.IsVisible == true;
        bool reclaimFocus = !visible && (_taggingPanel?.IsKeyboardFocusWithin == true || _panelHandle.IsKeyboardFocusWithin);
        bool right = layout.PanelSide == MediaPanelSide.Right;
        int pane = right ? 2 : 0, list = right ? 0 : 2;
        Grid.SetColumn(_panelBody.Children[0], list);
        Grid.SetColumn(_panelHandle, 1);
        _panelHandle.IsVisible = visible;
        ColumnDefinitions columns = _panelBody.ColumnDefinitions;
        columns[list].MinWidth = 0;
        columns[list].MaxWidth = double.PositiveInfinity;
        columns[list].Width = new GridLength(1, GridUnitType.Star);
        columns[pane].MinWidth = visible ? Config.MediaPanelWidthMinimum : 0;
        columns[pane].MaxWidth = visible ? Math.Min(Config.MediaPanelWidthMaximum, room) : double.PositiveInfinity;
        columns[pane].Width = new GridLength(visible ? Math.Min(layout.PanelWidth, room) : 0);
        columns[1].Width = new GridLength(visible ? MediaPage.HandleWidth : 0);

        if (visible) EnsureTaggingPanel();
        if (_taggingPanel is { } panel)
        {
            Grid.SetColumn(panel, pane);
            panel.BorderThickness = new Thickness(right ? 1 : 0, 0, right ? 0 : 1, 0);
            panel.IsVisible = visible;
        }

        UpdatePanelToggle(layout);
        if (reclaimFocus) FocusContent(_panelRow?.Key);
        if (visible && !wasVisible) RebindPanel();
        if (!visible && wasVisible) StopPanel();
    }

    private void CommitPanelWidth()
    {
        if (!IsPanelShown || _taggingPanel is not { } panel) return;
        double actual = _panelBody.ColumnDefinitions[Grid.GetColumn(panel)].ActualWidth;
        if (!double.IsFinite(actual) || actual <= 0) return;
        MediaPageLayout layout = GetPanelLayout();
        double displayed = Math.Min(layout.PanelWidth, Bounds.Width - MinimumListWidth - MediaPage.HandleWidth);
        if (Math.Abs(displayed - actual) < 1) return;
        SavePanelWidth?.Invoke(Math.Clamp((int)Math.Round(actual), Config.MediaPanelWidthMinimum, Config.MediaPanelWidthMaximum));
    }

    private ReaderMediaSidebar EnsureTaggingPanel()
    {
        if (_taggingPanel is { } existing) return existing;
        ReaderMediaSidebar panel = _taggingPanel = new()
        {
            Name = "TaggingPanel", IsVisible = false, TitleKey = "command.media.panel.toggle", EmptyKey = "tagging.page.panel.empty",
            CloseKey = "media.panel.close", ToggleCommandId = PanelCommand, DecisionKeyContext = "tagging",
            ShowEmptyTitle = true, SingleClickActs = true
        };
        panel[!Border.BorderBrushProperty] = new DynamicResourceExtension("Workspace.CardEdge");
        panel.Styles.Add(new Style(selector => selector.OfType<Button>()) { Setters = { new Setter(FocusableProperty, false) } });
        panel.ConfigureInfo(_panelInfo);
        panel.SaveSidebarSetting = update =>
        {
            _panelInfo = panel.CopyBarSettingsTo(_panelInfo);
            SavePanelSetting?.Invoke(update);
        };
        AutomationProperties.SetAutomationId(panel, "TaggingPanel");
        AutomationProperties.SetAutomationId(panel.Frame, "ReaderMediaSidebarFrame");
        AutomationProperties.SetAutomationId(panel.Picture, "ReaderMediaSidebarPicture");
        AutomationProperties.SetAutomationId(panel.Badges, "ReaderMediaSidebarBadges");
        AutomationProperties.SetName(panel, CommandCatalog.Label(PanelCommand));
        panel.Open.Click += (_, _) => OpenPanelGallery();
        panel.PictureActivated += (_, _) => OpenPanelGallery();
        panel.GalleryRequested += (_, _) => OpenPanelGallery();
        panel.Close.Click += (_, _) =>
        {
            TagQueueKey? cursor = _panelRow?.Key;
            TogglePanel();
            FocusContent(cursor);
        };
        panel.AddHandler(KeyDownEvent, OnPanelKeyDown, RoutingStrategies.Tunnel);
        panel.Frame.SizeChanged += (_, _) =>
        {
            if (panel.Shown is { } image && ReaderMediaSidebarRules.NeedsLargerDecode(image.PixelSize, PanelTarget(panel), panel.Load.ShownSource))
                QueuePanelFollow(resize: true);
        };
        panel.Localize();
        panel.ShowEmpty();
        _panelBody.Children.Add(panel);
        return panel;
    }

    private int PanelIndex(TagQueueKey key)
    {
        for (int index = 0; index < _rows.Count; index++)
            if (_rows[index].Key == key) return index;
        return -1;
    }

    private TaggingQueueRow? RowForPanel(TagQueueKey? key)
    {
        int index = key is { } value ? PanelIndex(value) : -1;
        return index >= 0 ? _rows[index] : null;
    }

    private TaggingQueueRow? PanelCursor() =>
        RowForPanel(FocusedResultKey()) ?? Lead ?? _rows.FirstOrDefault(row => row.Next) ?? _rows.FirstOrDefault();

    private void RebindPanel()
    {
        if (!IsPanelShown || _tagBusy) return;
        TaggingQueueRow? row = RowForPanel(_panelWanted?.Key) ?? PanelCursor();
        CancelPanelFollow();
        _panelThrottle.Reset();
        SetPanelTarget(row);
    }

    private void QueuePanelFollow(bool resize = false)
    {
        if (_rendering || _tagBusy || !IsPanelShown) return;
        _panelResizePending |= resize;
        _panelCursorPending |= !resize;
        if (_panelFollowsPosted) return;
        _panelFollowsPosted = true;
        Dispatcher.UIThread.Post(FollowPanel, DispatcherPriority.Input);
    }

    private void FollowPanel()
    {
        _panelFollowsPosted = false;
        bool resizing = _panelResizePending;
        bool followCursor = _panelCursorPending;
        _panelResizePending = _panelCursorPending = false;
        if ((!resizing && !followCursor) || _rendering || _tagBusy || !IsPanelShown) return;

        TaggingQueueRow? pending = RowForPanel(_panelWanted?.Key);
        TaggingQueueRow? wanted = followCursor
            ? RowForPanel(FocusedResultKey()) ?? (_panelThrottle.Pending ? pending : null) ?? PanelCursor()
            : pending ?? RowForPanel(_panelRow?.Key) ?? PanelCursor();
        if (!resizing && wanted?.Key == _panelWanted?.Key && wanted?.Key == _panelRow?.Key) return;
        if (wanted?.Key != _panelWanted?.Key) CancelPanelLoad();
        _panelWanted = wanted;
        long now = PanelMilliseconds();
        if (_panelThrottle.Request(now))
        {
            CancelPanelFollow();
            SetPanelTarget(wanted);
        }
        else if (_panelFollowDelay == null)
        {
            long wait = _panelThrottle.Remaining(now);
            OpenActions.Start(() => FollowPanelAfterDelayAsync(wait), "tagging.panel.follow");
        }
    }

    private long PanelMilliseconds() => (long)PanelClock.GetElapsedTime(0, PanelClock.GetTimestamp()).TotalMilliseconds;

    private async Task FollowPanelAfterDelayAsync(long milliseconds)
    {
        // Owned by the field: whoever takes it out of the field (here, a cancel or Dispose) disposes it.
        CancellationTokenSource delay = new();
        CancellationToken stopped = delay.Token;
        _panelFollowDelay = delay;
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(milliseconds), PanelClock, stopped);
            if (!ReferenceEquals(_panelFollowDelay, delay) || _tagBusy || !IsPanelShown) return;
            if (_panelThrottle.Tick(PanelMilliseconds())) SetPanelTarget(_panelWanted);
        }
        catch (OperationCanceledException) when (stopped.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_panelFollowDelay, delay))
            {
                _panelFollowDelay = null;
                delay.Dispose();
            }
        }
    }

    private void CancelPanelFollow()
    {
        CancellationTokenSource? delay = _panelFollowDelay;
        _panelFollowDelay = null;
        delay?.Cancel();
        delay?.Dispose();
    }

    private static DispatcherTimer PanelTimer(Action action)
    {
        DispatcherTimer timer = new();
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        return timer;
    }

    internal void StopPanel()
    {
        CancelPanelLoad();
        CancelPanelFollow();
        _panelAnnounceTimer?.Stop();
        _panelThrottle.Reset();
        _panelRow = _panelWanted = null;
        _panelMediaKey = null;
        _panelFileAvailable = null;
        _panelResizePending = _panelCursorPending = false;
        _taggingPanel?.InvalidateTagEditor();
        _taggingPanel?.ResetInfoOverride();
        _taggingPanel?.ShowEmpty();
    }

    private void CancelPanelLoad()
    {
        _taggingPanel?.Load.Cancel();
        CancelPanelLoadSource();
    }

    private void CancelPanelLoadSource()
    {
        CancellationTokenSource? loading = _panelLoading;
        _panelLoading = null;
        loading?.Cancel();
        loading?.Dispose();
    }

    private void SetPanelTarget(TaggingQueueRow? row)
    {
        if (_taggingPanel is not { } panel) return;
        _panelWanted = _panelRow = row;
        if (row == null)
        {
            bool hadMedium = _panelMediaKey != null;
            StopPanel();
            if (hadMedium) AnnouncePanel(Texts.T("tagging.page.panel.empty"));
            return;
        }

        MediaLibraryEntry entry = EntryOf(row.Item);
        string key = string.Join("\n", row.Key, entry.Sha256, entry.Length, entry.Variant, entry.AttachmentRevisionKey);
        bool changed = key != _panelMediaKey;
        if (changed)
        {
            CancelPanelLoad();
            _panelMediaKey = key;
            _panelFileAvailable = null;
            panel.ShowPlaceholder(Texts.T("reader.sidebar.loading"));
        }

        GalleryTagPanelPosition position = DescribePanel(panel, row, entry, key);
        if (changed) AnnouncePanel(Texts.T("media.panel.region", position.Number, position.Count, row.Name));
        if (panel.Load.Awaiting || _panelFileAvailable == false) return;
        (double Width, double Height) target = PanelTarget(panel);
        ReaderMediaLoadTicket ticket = panel.Load.Begin(key, target);
        if (ticket.UpToDate) return;
        OpenActions.Start(() => LoadTaggingPanelAsync(panel, entry, key, ticket, target), "tagging.panel.load");
    }

    private GalleryTagPanelPosition DescribePanel(ReaderMediaSidebar panel, TaggingQueueRow row, MediaLibraryEntry entry, string key)
    {
        GalleryTagPanelPosition position = new(PanelIndex(row.Key) + 1, _rows.Count, More: false);
        string? post = entry.PostId ?? entry.BooruPostId;
        PixelSize? source = panel.Load.ShownKey == key ? panel.Load.ShownSource : null;
        string? blocked = PanelTagBlocked(entry);
        panel.OpenTagsAsync = blocked == null ? (anchor, token) => TagRowAsync(row, anchor, token) : null;
        panel.Describe(post != null ? Texts.T("reader.postNumber", post) : string.Empty, row.Name,
            source is { } size ? $"{size.Width}×{size.Height}" : string.Empty, OpenGallery != null,
            threadPosition: position, savedBytes: entry.Length, mediumKey: key);
        panel.SetSourceFacts(row.SourceText, null, null, entry.Length, null, null);
        RefreshPanelActions();
        return position;
    }

    private (double Width, double Height) PanelTarget(ReaderMediaSidebar panel)
    {
        Size frame = panel.Frame.Bounds.Size;
        if (frame.Width < 1 || frame.Height < 1) frame = new Size(Config.MediaPanelWidthDefault, Math.Max(1, Bounds.Height));
        return ReaderMediaSidebarRules.PhysicalTarget(frame, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
    }

    private async Task LoadTaggingPanelAsync(ReaderMediaSidebar panel, MediaLibraryEntry entry, string key, ReaderMediaLoadTicket ticket,
        (double Width, double Height) target)
    {
        // The field holds the newest load for cancelling; each load disposes its own source when it ends.
        CancellationTokenSource loading = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        CancellationToken stopped = loading.Token;
        _panelLoading = loading;
        bool Current() => IsPanelShown && panel.Load.IsCurrent(ticket.Request);
        ReaderMediaSidebarImageLoad request = new(panel, key, ticket, ReaderSettingsPreferences.Gallery(_host.Config), Current, stopped);
        try
        {
            await request.RunAsync(ReadContentAsync, picture =>
            {
                if (picture.Source is { } size) panel.SetDetail($"{size.Width}×{size.Height}");
                // The first decode may have started before a drag enlarged the frame. Retry only for a larger target.
                var needed = PanelTarget(panel);
                if ((needed.Width > target.Width || needed.Height > target.Height)
                    && ReaderMediaSidebarRules.NeedsLargerDecode(picture.Bitmap.PixelSize, needed, picture.Source))
                    QueuePanelFollow(resize: true);
            });
        }
        finally
        {
            if (ReferenceEquals(_panelLoading, loading)) _panelLoading = null;
            loading.Dispose();
        }

        async Task<ReaderMediaSidebarContent> ReadContentAsync(CancellationToken token)
        {
            bool verified = false;
            try
            {
                MediaAssetSnapshot manifest = await OpenPanelManifestAsync(entry.Directory!, token);
                if (MediaTileThumbnails.Matching(manifest, entry) is not { } asset || !File.Exists(asset.Path))
                    return new ReaderMediaSidebarContent.Unavailable("archive.notSaved");
                if (entry.Kind == SavedMediaKind.Image)
                {
                    GalleryEntry saved = new(new GalleryAssetKey(entry.Thread, manifest.Directory, asset.AssetId),
                        manifest, asset, null, null, !asset.IsCurrent, entry.Unassigned);
                    (Bitmap image, GalleryImageInfo info) = await DecodePanelImageAsync(saved, new GalleryDecodeRequest.Fit(target.Width, target.Height), token);
                    verified = true;
                    return new ReaderMediaSidebarContent.Picture(image, GalleryImageCache.Shared.Release, info.StillFrame, new PixelSize(info.Width, info.Height));
                }
                // A saved video can be rated even if it has no previewable poster.
                await using (Stream original = await manifest.OpenVerifiedReadAsync(asset.AssetId, token)) { }
                Bitmap? poster = null;
                if (entry.Kind == SavedMediaKind.Video)
                {
                    int width = (int)target.Width, height = (int)target.Height;
                    poster = await MediaTileThumbnails.LoadPosterAsync(manifest, asset, _thumbnails.PosterCacheDirectory, width, height,
                        MediaTileThumbnails.PosterKey(asset, width, height), token);
                }
                verified = true;
                return poster == null ? new ReaderMediaSidebarContent.Unavailable("reader.sidebar.unavailable")
                    : new ReaderMediaSidebarContent.Picture(poster, MediaTileThumbnails.Release, Playable: true);
            }
            finally
            {
                if (Current() && !token.IsCancellationRequested && _panelFileAvailable != verified && _panelRow is { } row)
                {
                    _panelFileAvailable = verified;
                    if (!verified) panel.InvalidateTagEditor();
                    DescribePanel(panel, row, entry, key);
                }
            }
        }
    }

    private void AnnouncePanel(string text)
    {
        _panelAnnouncement = text;
        DispatcherTimer timer = _panelAnnounceTimer ??= PanelTimer(() =>
        {
            if (!IsPanelShown || _taggingPanel is not { } panel) return;
            panel.Announce(_panelAnnouncement);
            AutomationProperties.SetName(panel, CommandCatalog.Label(PanelCommand));
        });
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(ReaderMediaSidebarRules.AnnounceDelayMs);
        timer.Start();
    }
}
