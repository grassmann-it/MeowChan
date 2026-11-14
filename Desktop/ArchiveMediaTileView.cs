using System;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
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
    // MB1b: what the tile says about the medium (favourite, type, length, file missing) is drawn by the shared media badges over exactly the
    // shown picture: one view, no child per badge (the former type chip is gone). The interactive favourite toggle stays what it was (a button,
    // bottom right, shown while the tile is hovered or focused); the badge carries the state at rest, so the toggle is quiet unless pointed at.
    private readonly ArchiveFavoriteButton _favorite = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
        Classes = { MeowChan.Desktop.Controls.WorkspaceChips.FavoriteToggleClass, QuietFavoriteClass }, Margin = new Thickness(0, 0, 2, 2) };
    private readonly MediaBadgeView _badges = new() { Name = "ArchiveMediaBadges", Options = MediaBadgeOptions.Archive };
    private ArchiveBadgeDescription _badgeText;
    private bool _missing;
    private readonly StackPanel _topRight = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 2, 0) };
    // D4 (T3): a selected tile also shows a check mark in the top-right corner, so the multi-selection is never colour alone. A badge that
    // already sits in that corner (a missing file) keeps its place: the check moves in beside it.
    private readonly Border _selectedMark = MeowChan.Desktop.Controls.WorkspaceChips.CreateSelectionCheck("ArchiveMediaSelected");
    private IDisposable? _selection;
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxWidth = 170, MaxHeight = 125 };
    private readonly LetterboxPanel _preview = new();
    private readonly TextBlock _rating = new TextBlock().CaptionText();
    private readonly TextBlock _title = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
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
    /// <summary>The style class of a favourite toggle that is only shown while its tile is hovered or focused (the badge shows the state at rest).</summary>
    internal const string QuietFavoriteClass = "tile-favorite-quiet";
    /// <summary>The space the check mark keeps from the tile's top-right edge (dip).</summary>
    internal const double CornerInset = 2, CornerGap = 2;
    internal bool HasThumbnail => _bitmap != null;
    /// <summary>The shared badge view over the shown picture (tests and smokes read its state and plan).</summary>
    internal MediaBadgeView Badges => _badges;
    /// <summary>The badge sentence in the tile's name (empty when the tile has no badge).</summary>
    internal string BadgeDescription => DataContext is ArchiveAssetRow ? _badgeText.Get(_badges.State, _badges.Options) : string.Empty;
    /// <summary>Where the picture is drawn, in the preview's coordinates (the badge, star and check mark sit on it).</summary>
    internal Rect PictureBounds => _preview.PictureBounds;
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
        MaxWidth = 190; Height = 210;
        // The cell stays uniform (the tile grid is regular); the picture is letterboxed in it and the badge, star and check mark are
        // arranged on the picture, not on the cell edge.
        LetterboxPanel preview = _preview; preview.Height = 125; preview.MaxWidth = 170;
        _topRight.Children.Add(_selectedMark);
        preview.Children.Add(_image); preview.Children.Add(_message); preview.Children.Add(_badges); preview.Children.Add(_topRight); preview.Children.Add(_favorite);
        Grid grid = new() { Margin = new Thickness(8), RowDefinitions = new RowDefinitions("125,Auto,Auto,*"), RowSpacing = 4 };
        grid.Children.Add(preview); grid.Children.Add(_title); Grid.SetRow(_title, 1);
        grid.Children.Add(_rating); Grid.SetRow(_rating, 2);
        grid.Children.Add(_status); Grid.SetRow(_status, 3); Content = grid;
        DataContextChanged += (_, _) =>
        {
            _missing = false;
            Reset(); RefreshFavorite(); RefreshRating();
            ArchiveAssetRow? row = DataContext as ArchiveAssetRow;
            _title.Text = row?.Name; _status.Text = row?.TileSummary;
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
            _selection?.Dispose();
            _selection = MeowChan.Desktop.Controls.WorkspaceChips.ObserveSelection(this, _selectedMark);
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
            _visibilityAncestors.Clear();
            _visible = false; Reset();
            _selection?.Dispose(); _selection = null; _selectedMark.IsVisible = false;
        };
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
        _favorite.Configure(row?.FavoriteAvailable, row?.IsFavorite, row?.ToggleFavoriteAsync, row?.FavoriteError);
        // The name is rebuilt only when the badges' sentence changed (the page refreshes every tile whenever a favourite changes).
        string before = BadgeDescription;
        UpdateBadges(row);
        if (!ReferenceEquals(before, BadgeDescription)) UpdateDescription();
    }

    // The badges follow the row (favourite, type) and a load that found the file gone. The view is told the picture's size; the letterbox
    // panel repeats that on every layout. Nothing is allocated when the state did not change (the view compares, the sentence is cached).
    private void UpdateBadges(ArchiveAssetRow? row)
    {
        if (row == null) { _badges.Clear(); _badgeText.Reset(); }
        else _badges.Apply(ArchiveMediaBadges.ForAsset(row, _missing), _preview.PictureBounds.Size);
        // The selection check keeps clear of a badge in the same corner (a missing file is a safety fact and is never covered). The check is
        // arranged on the marks band, which can be wider than a thin picture, so that difference is part of the inset.
        Rect picture = _preview.PictureBounds, band = _preview.MarksBounds;
        double inset = CornerInset + Math.Max(0, band.Right - picture.Right) + TopRightExtent(_badges.Plan, picture.Width);
        if (_topRight.Margin.Right != inset) _topRight.Margin = new Thickness(0, 2, inset, 0);
    }

    /// <summary>
    /// How far the badges of the top-right corner reach into the picture from its right edge, plus a gap; 0 when the corner holds none. The
    /// selection check is arranged beside them, never on top.
    /// </summary>
    internal static double TopRightExtent(MediaBadgePlan plan, double pictureWidth)
    {
        double reach = 0;
        foreach (PlacedBadge placed in plan.Badges)
            if (placed.Badge.Corner == MediaBadgeCorner.TopRight) reach = Math.Max(reach, pictureWidth - placed.Bounds.Left);
        return reach > 0 ? reach + CornerGap : 0;
    }

    internal void RefreshRating()
    {
        _rating.Text = DataContext is ArchiveAssetRow row ? MediaRatingText.AccessibleValue(row.RatingLabel) : string.Empty;
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
            _image.Source = kept; _preview.Aspect = MediaSlotGeometry.AspectOf(kept);
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
            _image.Source = bitmap; _preview.Aspect = MediaSlotGeometry.AspectOf(bitmap);
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
        string badges = row == null ? string.Empty : _badgeText.Get(_badges.State, _badges.Options);
        string description = string.Join(Environment.NewLine, new[] { _title.Text, kind, badges, _status.Text, _rating.Text, _message.Text }.Where(part => !string.IsNullOrEmpty(part)));
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, description);
        AutomationProperties.SetHelpText(this, badges);
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel(); _loading = null;
        _image.Source = null; _preview.Aspect = null; if (_bitmap != null) MediaTileThumbnails.Release(_bitmap); _bitmap = null;
        _message.Text = string.Empty;
        UpdateDescription();
    }
}
