using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

public sealed class ArchiveMediaTileView : UserControl
{
    private static readonly SemaphoreSlim ThumbnailGate = new(2, 2);
    private readonly ArchiveFavoriteButton _favorite = new() { Classes = { WorkspaceChips.FavoriteToggleClass } };
    private readonly MediaTileView _tile;
    private bool _missing;
    private ArchiveTileAppearance _appearance;
    private readonly TextBlock _rating = new TextBlock().CaptionText();
    private readonly TextBlock _status = new TextBlock { MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis }.CaptionText();
    private readonly TextBlock _message = new TextBlock { MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap }.CaptionText();
    private Bitmap? _bitmap;
    private CancellationTokenSource? _loading;
    private int _request;
    private bool _visible;
    private Rect _viewport;
    private readonly List<Visual> _visibilityAncestors = new();
    private static readonly Func<ArchiveAssetRow, CancellationToken, Task<Bitmap?>> DefaultThumbnailDecoder =
        static (row, token) => DecodeAsync(row, token, System.IO.Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1"));
    internal Func<ArchiveAssetRow, CancellationToken, Task<Bitmap?>> ThumbnailDecoder { get; set; } = DefaultThumbnailDecoder;

    // Saved-media tiles show the same local video poster as the reader's post
    // attachments and the Medien page (cache keyed by the verified bytes); images
    // keep the image decoder. Nothing is fetched or generated beyond the reader.
    internal const int PosterWidth = 340, PosterHeight = 250;
    /// <summary>
    /// Raised when the rating caption is clicked. The page opens "Rate and tag" for this tile's file.
    /// </summary>
    internal static readonly Avalonia.Interactivity.RoutedEvent<Avalonia.Interactivity.RoutedEventArgs> RateAndTagRequestedEvent =
        Avalonia.Interactivity.RoutedEvent.Register<ArchiveMediaTileView, Avalonia.Interactivity.RoutedEventArgs>("RateAndTagRequested",
            Avalonia.Interactivity.RoutingStrategies.Bubble);
    /// <summary>The rating caption under the title opens "Rate and tag" on click.</summary>
    internal TextBlock RatingCaption => _rating;
    /// <summary>
    /// The tile size of the surface a tile stands on: set it on the tile list (the reader's Media tab) and every tile in it, realized or made later,
    /// follows (the value is inherited down the tree). Medium is the tile as it always was.
    /// </summary>
    internal static readonly AttachedProperty<MediaTileSize> TileSizeProperty =
        AvaloniaProperty.RegisterAttached<ArchiveMediaTileView, Control, MediaTileSize>("TileSize", MediaTileSize.Medium, inherits: true);
    internal static MediaTileSize GetTileSize(Control control) => control.GetValue(TileSizeProperty);
    internal static void SetTileSize(Control control, MediaTileSize size) => control.SetValue(TileSizeProperty, size);
    /// <summary>The measures this tile has now (smokes and tests).</summary>
    internal ArchiveTileAppearance Appearance => _appearance;
    internal bool HasThumbnail => _bitmap != null;
    /// <summary>The shared badge view over the shown picture (tests and smokes read its state and plan).</summary>
    internal MediaBadgeView Badges => _tile.Badges;
    /// <summary>The badge sentence in the tile's name (empty when the tile has no badge).</summary>
    internal string BadgeDescription => DataContext is ArchiveAssetRow ? MediaBadgeSentences.Get(Badges.State, Badges.Options) : string.Empty;
    /// <summary>Where the picture is drawn, in the preview's coordinates (the picture's own mark sits on it; badge, star and check mark are on the slot corners).</summary>
    internal Rect PictureBounds => _tile.PictureBounds;
    internal static bool UsesVideoPoster(ArchiveAssetRow row) =>
        MeowChan.Gallery.SavedMediaTypes.Classify(row.Asset.OriginalFileName ?? row.Asset.Path) == MeowChan.Gallery.SavedMediaKind.Video;
    /// <summary>I7: GIF, video or other file by the saved name (no file is read); empty for still images. The badges name GIF and video themselves; only "other file" is added to the tile's name.</summary>
    internal static string KindLabel(ArchiveAssetRow row)
    {
        string name = row.Asset.OriginalFileName ?? row.Asset.Path;
        return MeowChan.Gallery.SavedMediaTypes.Classify(name) switch
        {
            MeowChan.Gallery.SavedMediaKind.Image => string.Equals(System.IO.Path.GetExtension(name), ".gif", StringComparison.OrdinalIgnoreCase)
                ? Texts.T("media.tile.kind.gif") : string.Empty,
            MeowChan.Gallery.SavedMediaKind.Video => Texts.T("media.tile.kind.video"),
            _ => Texts.T("media.tile.kind.other")
        };
    }
    internal static string UnavailableKey(ArchiveAssetRow row) =>
        UsesVideoPoster(row) ? "archive.tileVideoPosterUnavailable" : "archive.tilePreviewUnavailable";
    internal static Task<Bitmap?> DecodeAsync(ArchiveAssetRow row, CancellationToken token, string posterCacheDirectory) =>
        UsesVideoPoster(row)
            ? MediaTileThumbnails.LoadPosterAsync(row.Manifest, row.Asset, posterCacheDirectory, PosterWidth, PosterHeight,
                MediaTileThumbnails.PosterKey(row.Asset, PosterWidth, PosterHeight), token)
            : ArchivePage.DecodeThumbnailAsync(row, token);

    public ArchiveMediaTileView()
    {
        MediaBadgeView badges = new() { Name = "ArchiveMediaBadges", Options = MediaBadgeOptions.ArchiveTile, IsTabStop = false };
        MediaTileView grid = _tile = new(_message, _favorite, badges, "ArchiveMediaSelected")
        {
            Margin = new Thickness(8), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*"), RowSpacing = 4
        };
        grid.Children.Add(_rating); Grid.SetRow(_rating, 2);
        WireRatingCaption();
        grid.Children.Add(_status); Grid.SetRow(_status, 3); Content = grid;
        ApplyAppearance(ArchiveTileMetrics.Standard);
        DataContextChanged += (_, _) =>
        {
            _missing = false;
            Reset(); RefreshFavorite(); RefreshRating();
            ArchiveAssetRow? row = DataContext as ArchiveAssetRow;
            _tile.Title.Text = row?.Name; _status.Text = row?.TileSummary;
            ToolTip.SetTip(_status, row == null ? null : row.ExactSize + Environment.NewLine + row.VersionDetail);
            UpdateDescription();
            if (_visible && IsEffectivelyVisible) _ = LoadAsync();
        };
        EffectiveViewportChanged += (_, e) =>
        {
            _viewport = e.EffectiveViewport;
            RefreshVisibility();
        };
        AttachedToVisualTree += (_, _) =>
        {
            MeowChan.Desktop.Commands.ShortcutService.Changed += RefreshRating;
            RefreshRating();
            foreach (Visual ancestor in this.GetSelfAndVisualAncestors())
            {
                _visibilityAncestors.Add(ancestor);
                ancestor.PropertyChanged += OnAncestorVisibilityChanged;
            }
            Dispatcher.UIThread.Post(RefreshVisibility, DispatcherPriority.Loaded);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            foreach (Visual ancestor in _visibilityAncestors) ancestor.PropertyChanged -= OnAncestorVisibilityChanged;
            MeowChan.Desktop.Commands.ShortcutService.Changed -= RefreshRating;
            _visibilityAncestors.Clear();
            _visible = false; Reset();
        };
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TileSizeProperty) ApplyAppearance(ArchiveTileMetrics.For(change.GetNewValue<MediaTileSize>()));
    }

    // The tile keeps its parts and its aspect at every size: only the cell, the picture slot and the longest side of the picture change, so the
    // letterboxed picture, the badges on the slot's corners, the star and the selection check stay where they were (they hang on the slot).
    private void ApplyAppearance(ArchiveTileAppearance appearance)
    {
        if (appearance == _appearance) return;
        _appearance = appearance;
        MaxWidth = appearance.CellWidth;
        Height = appearance.CellHeight;
        _tile.SetPreviewSize(appearance.PictureWidth, appearance.PreviewHeight);
    }

    private void OnAncestorVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty) return;
        if (sender is Visual { IsVisible: false }) { _visible = false; Reset(); }
        else Dispatcher.UIThread.Post(RefreshVisibility, DispatcherPriority.Loaded);
    }

    private void RefreshVisibility()
    {
        bool visible = _visibilityAncestors.Count > 0 && IsEffectivelyVisible
            && new Rect(Bounds.Size).Intersects(_viewport);
        if (_visible == visible) return;
        _visible = visible;
        if (visible) _ = LoadAsync(); else Reset();
    }

    internal void RefreshFavorite()
    {
        ArchiveAssetRow? row = DataContext as ArchiveAssetRow;
        _favorite.Configure(row);
        // The name is rebuilt only when the badges' sentence changed (the page refreshes every tile whenever a favourite changes).
        string before = BadgeDescription;
        UpdateBadges(row);
        if (!ReferenceEquals(before, BadgeDescription)) UpdateDescription();
    }

    private void UpdateBadges(ArchiveAssetRow? row)
    {
        if (row == null) Badges.Clear();
        else _tile.ShowBadges(ArchiveMediaBadges.ForAsset(row, _missing));
    }

    private void WireRatingCaption()
    {
        TextBlock caption = _rating;
        caption.Focusable = false;
        caption.IsTabStop = false;
        // A text block without a background is hit only on its glyphs: a click in its padding would fall through to the tile and select it.
        caption.Background = Avalonia.Media.Brushes.Transparent;
        // The hand cursor needs the platform's cursor factory: without a platform (a headless host) the control keeps the arrow.
        try { caption.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand); }
        catch (InvalidOperationException) { }
        AutomationProperties.SetControlTypeOverride(caption, Avalonia.Automation.Peers.AutomationControlType.Button);
        caption.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(caption).Properties.IsLeftButtonPressed || DataContext is not ArchiveAssetRow) return;
            this.FindAncestorOfType<ListBoxItem>()?.Focus(NavigationMethod.Pointer);
            e.Handled = true;
            RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(RateAndTagRequestedEvent) { Source = this });
        };
    }

    internal void RefreshRating()
    {
        _rating.Text = DataContext is ArchiveAssetRow row ? MediaRatingText.AccessibleValue(row.RatingLabel) : string.Empty;
        AutomationProperties.SetName(_rating, _rating.Text);
        string label = MeowChan.Desktop.Commands.CommandCatalog.Label("media.rating.edit");
        string help = MeowChan.Desktop.Commands.CommandCatalog.ShortcutTooltipDisplay("media.rating.edit", "reader") is { } gesture ? label + " · " + gesture : label;
        ToolTip.SetTip(_rating, help);
        AutomationProperties.SetHelpText(_rating, help);
        UpdateDescription();
    }

    private async Task LoadAsync()
    {
        if (DataContext is not ArchiveAssetRow row || _loading != null || _bitmap != null || !IsEffectivelyVisible) return;
        int request = _request;
        Func<ArchiveAssetRow, CancellationToken, Task<Bitmap?>> decode = ThumbnailDecoder;
        // Images retain the immediate cache path. Videos obtain their lease inside DecodeAsync only after checking the original;
        // they must not bypass that check here or be added/leased a second time below. Replaced decoders never share the cache.
        string? key = ReferenceEquals(decode, DefaultThumbnailDecoder) && !UsesVideoPoster(row) ? MediaTileThumbnails.ByteKey(row.Asset) : null;
        if (key != null && MediaTileThumbnails.Cache.TryAcquire(key) is { } kept)
        {
            _bitmap = kept; _missing = false;
            _tile.Thumbnail = kept;
            _message.Text = string.Empty;
            UpdateBadges(row);
            UpdateDescription();
            return;
        }
        using CancellationTokenSource loading = new();
        _loading = loading;
        _message.Text = Texts.T("archive.loadingThumbnail");
        UpdateDescription();
        try
        {
            await ThumbnailGate.WaitAsync(loading.Token);
            Bitmap? bitmap;
            try { bitmap = await Task.Run(() => decode(row, loading.Token), loading.Token); }
            finally { ThumbnailGate.Release(); }
            if (key != null && bitmap != null) bitmap = MediaTileThumbnails.Cache.Add(key, bitmap);
            if (request != _request || !_visible || !IsEffectivelyVisible || !ReferenceEquals(DataContext, row) || loading.IsCancellationRequested)
            { if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }
            _bitmap = bitmap; _missing = false;
            _tile.Thumbnail = bitmap;
            // The tile has no path line below it, unlike the preview pane, so name only what is missing.
            _message.Text = bitmap == null ? Texts.T(UnavailableKey(row)) : string.Empty;
            UpdateBadges(row);
            UpdateDescription();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (request != _request) return;
            // A file that is gone is a safety fact the badges show (a failure to decode is not).
            _missing = ArchiveMediaBadges.IsMissingFile(exception);
            _message.Text = exception.Message;
            UpdateBadges(row);
            UpdateDescription();
        }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; }
    }

    private void UpdateDescription()
    {
        ArchiveAssetRow? row = DataContext as ArchiveAssetRow;
        // GIF and video are said by the badges' sentence ("Video", "Animated image"); only "file" (neither a picture nor a video) is added here.
        string kind = row != null && MeowChan.Gallery.SavedMediaTypes.Classify(row.Asset.OriginalFileName ?? row.Asset.Path)
            is not (MeowChan.Gallery.SavedMediaKind.Image or MeowChan.Gallery.SavedMediaKind.Video) ? KindLabel(row) : string.Empty;
        string badges = BadgeDescription;
        string description = string.Join(Environment.NewLine, new[] { _tile.Title.Text, kind, badges, _status.Text, _rating.Text, _message.Text }.Where(part => !string.IsNullOrEmpty(part)));
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, description);
        AutomationProperties.SetHelpText(this, badges);
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel(); _loading = null;
        _tile.Thumbnail = null; if (_bitmap != null) MediaTileThumbnails.Release(_bitmap); _bitmap = null;
        _message.Text = string.Empty;
        UpdateDescription();
    }
}
