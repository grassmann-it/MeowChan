using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>
/// The thumbnails of the tagging page (TG1): the same verified, local preview path as the media page tiles
/// (<see cref="MediaTileThumbnails"/>: the decoded preview is kept by the bytes' hash, so a tile that the media page already showed
/// appears at once, EXIF orientation is applied by the one decoder, a video shows its local poster). Nothing is fetched from the network.
/// </summary>
internal sealed class TaggingThumbnails
{
    private readonly MediaManifestCache _manifests;

    internal TaggingThumbnails(CancellationToken lifetime = default) => _manifests = new MediaManifestCache(null, lifetime);

    /// <summary>Where the derived video posters are kept (the same folder every surface uses).</summary>
    internal string PosterCacheDirectory { get; set; } = System.IO.Path.Combine(Config.GetConfigDirectory(), "cache", "video-posters-v1");

    internal static MediaLibraryTile TileOf(TagQueueItem item)
    {
        MediaLibraryEntry entry = item.ToLibraryEntry();
        return new MediaLibraryTile(entry, new[] { entry });
    }

    /// <summary>A kept image preview leased to the caller (give it back with <see cref="MediaTileThumbnails.Release"/>), or null.</summary>
    internal Bitmap? Cached(TagQueueItem item) => MediaTileThumbnails.TryGetCached(TileOf(item));

    /// <summary>Loads and leases a verified preview or a video poster; null when none can be shown. The caller releases it once.</summary>
    internal Task<Bitmap?> LoadAsync(TagQueueItem item, CancellationToken token) =>
        MediaTileThumbnails.LoadAsync(TileOf(item), _manifests.OpenAsync, PosterCacheDirectory, token);

    /// <summary>Files may have changed: earlier manifest views must prove their identity again.</summary>
    internal void Invalidate() => _manifests.Invalidate();
}

/// <summary>
/// One preview picture of a queue entry: loads only while it is visible, at most four loads at a time over every preview of the page,
/// and gives the leased bitmap back when it is reused or leaves the screen. Used by the list rows (small) and the grid tiles (large).
/// </summary>
internal sealed class TaggingThumbnail : Border
{
    private static readonly SemaphoreSlim Gate = new(4, 4);
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _placeholder = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, IsVisible = false }.CaptionText();
    private Bitmap? _bitmap;
    private CancellationTokenSource? _loading;
    private TagQueueItem? _item;
    private int _request;
    private bool _visible, _showText;

    internal TaggingThumbnail(bool showText)
    {
        _showText = showText;
        Classes.Add("tagging-thumbnail");
        ClipToBounds = true;
        CornerRadius = new CornerRadius(5);
        Child = new Grid { Children = { _image, _placeholder } };
        EffectiveViewportChanged += (_, e) =>
        {
            bool visible = IsEffectivelyVisible && new Rect(Bounds.Size).Intersects(e.EffectiveViewport);
            if (visible == _visible) return;
            _visible = visible;
            if (visible) _ = LoadAsync(); else Reset();
        };
        DetachedFromVisualTree += (_, _) => { _visible = false; Reset(); };
    }

    /// <summary>The page's loader; without one (a harness) the preview stays a placeholder.</summary>
    internal TaggingThumbnails? Source { get; set; }

    /// <summary>True while a verified preview or poster is shown.</summary>
    internal bool HasPicture => _image.Source != null;

    /// <summary>The entry shown now (null clears the picture).</summary>
    internal TagQueueItem? Item
    {
        get => _item;
        set
        {
            if (ReferenceEquals(_item, value)) return;
            _item = value;
            Reset();
            if (_visible) _ = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_item is not { } item || Source is not { } source || _loading != null || _bitmap != null) return;
        int request = _request;
        if (source.Cached(item) is { } cached)
        {
            _bitmap = cached;
            _image.Source = cached;
            _placeholder.IsVisible = false;
            return;
        }
        using CancellationTokenSource loading = new();
        _loading = loading;
        if (_showText) { _placeholder.Text = Texts.T("archive.loadingThumbnail"); _placeholder.IsVisible = true; }
        try
        {
            await Gate.WaitAsync(loading.Token);
            Bitmap? bitmap;
            try { bitmap = await Task.Run(() => source.LoadAsync(item, loading.Token), loading.Token); }
            finally { Gate.Release(); }
            if (request != _request || !_visible || !ReferenceEquals(_item, item)) { if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _placeholder.Text = bitmap == null && _showText ? Texts.T("archive.previewUnavailable") : string.Empty;
            _placeholder.IsVisible = bitmap == null && _showText;
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (request == _request && _showText) { _placeholder.Text = Texts.T("archive.previewUnavailable"); _placeholder.IsVisible = true; } }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; }
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel();
        _loading = null;
        _image.Source = null;
        if (_bitmap != null) MediaTileThumbnails.Release(_bitmap);
        _bitmap = null;
        _placeholder.IsVisible = false;
    }
}
