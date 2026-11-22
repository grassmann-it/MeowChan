using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Booru;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// A Booru-only composition of the existing gallery image surface. It starts with the preview
/// already fetched by Search and loads a bounded sample for the active post.
/// The synthetic entries below are display identities, never published assets or library entries.
/// </summary>
internal sealed class BooruResultViewerWindow : Window, IAsyncDisposable
{
    private readonly BooruSearchModel _search;
    private readonly BooruPostKey[] _ids;
    private readonly HashSet<string> _selected;
    private readonly Action _selectionChanged;
    private readonly GalleryViewer _viewer = new();
    private readonly BooruGallerySampleLoader _sample = new();
    private readonly Func<BooruGallerySampleLimits>? _sampleSettings;
    private readonly BooruGalleryToolbar _toolbar = new("BooruViewer", popout: true);
    private readonly BooruGalleryTags _tags = new();
    private readonly TextBlock _caption = new() { Name = "BooruViewerCaption", Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap };
    private int _index;
    private byte[]? _shownBytes;
    private GalleryEntry? _shownEntry;
    private BooruPreviewState? _shownPreview;
    private string? _shownId;
    private bool _ended;

    internal (Size Size, PixelPoint Position)? NormalBounds { get; private set; }
    internal BooruPostKey CurrentPost => _ids[_index];
    internal string CurrentPostId => CurrentPost.PostId;
    internal Task Cleanup { get; private set; } = Task.CompletedTask;
    private BooruResultRowView? Row => _search.View.Rows.FirstOrDefault(row => row.Post.Identity == CurrentPost);

    internal BooruResultViewerWindow(BooruSearchModel search, string postId, HashSet<string> selected, Action selectionChanged,
        BooruPreviewDecoder? decoder = null, Action<string>? openPost = null, Func<BooruGallerySampleLimits>? sampleSettings = null)
    {
        _search = search;
        _sampleSettings = sampleSettings;
        _ids = search.View.Rows.Select(row => row.Post.Identity).ToArray();
        _index = Array.FindIndex(_ids, identity => identity.PostId == postId);
        if (_index < 0) throw new ArgumentException("The post is not in this result page.", nameof(postId));
        _selected = selected;
        _selectionChanged = selectionChanged;
        Name = "BooruResultViewer";
        DialogWindow.Configure(this, DialogSize.XL, modal: false);
        _viewer.Configure(false, false, false, true, 0);
        decoder ??= new BooruPreviewDecoder();
        _viewer.DecodeImageAsync = (entry, request, token) =>
        {
            token.ThrowIfCancellationRequested();
            // Capture this entry's immutable preview on the dispatcher, never query mutable rows
            // from a worker. Gallery owns cancellation, UI adoption and stale-bitmap disposal.
            if (!ReferenceEquals(_shownEntry, entry) || _shownBytes is not { } bytes)
                throw new InvalidOperationException("The current Booru preview is unavailable.");
            return decoder.DecodeAsync(bytes, request, token);
        };
        // This host supplies its own Booru chrome, rather than changing shared gallery behavior.
        // A higher-priority presentation value keeps the gallery's own layout refresh from exposing
        // local-file actions on an unsaved provider preview. No controls are detached or reparented.
        _viewer.TemplateApplied += (_, _) => SuppressGalleryChrome();
        _viewer.AddHandler(ContextRequestedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        _viewer.CloseRequested += Close;
        _viewer.MoveRequested += Move;

        _toolbar.Move = Move;
        _toolbar.MoveToBoundary = last => MoveTo(last ? _ids.Length - 1 : 0);
        _toolbar.ToggleSelection = ToggleSelection;
        _toolbar.Load = () => OpenActions.Start(DownloadAsync, "booru.viewer.download");
        _toolbar.OpenPost = () =>
        {
            if (Row is { } row && BooruPostLinks.TryCanonical(row.Post, out string url)) openPost?.Invoke(url);
        };
        _toolbar.Close = Close;

        Grid stage = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Children = { _viewer, _tags } };
        Grid.SetColumn(_tags, 1);
        Grid root = new()
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(12),
            Children = { _toolbar, stage, _caption }
        };
        Grid.SetRow(stage, 1);
        Grid.SetRow(_caption, 2);
        DialogWindow.Frame(this, root, modal: false);
        AutomationProperties.SetLiveSetting(_caption, AutomationLiveSetting.Polite);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        PositionChanged += (_, _) => CaptureNormalBounds();
        Resized += (_, _) => CaptureNormalBounds();
        Closing += (_, _) => CaptureNormalBounds();
        Opened += (_, _) =>
        {
            _viewer.ApplyTemplate();
            SuppressGalleryChrome();
            CaptureNormalBounds();
            Refresh();
            _viewer.Focus();
        };
        _search.Changed += OnChanged;
        Texts.Changed += OnLanguageChanged;
        ShortcutService.Changed += OnChanged;
        Closed += (_, _) => OpenActions.Start(() => DisposeAsync().AsTask(), "booru.viewer.dispose");
    }

    public ValueTask DisposeAsync()
    {
        if (!_ended)
        {
            _ended = true;
            _sample.Pause();
            _search.Changed -= OnChanged;
            Texts.Changed -= OnLanguageChanged;
            ShortcutService.Changed -= OnChanged;
            Cleanup = _viewer.DisposeAsync().AsTask();
        }
        return new(Cleanup);
    }

    private void SuppressGalleryChrome()
    {
        // A UserControl has no visual descendants before its template is applied. Install the
        // presentation overrides on template admission, not while constructing an unattached host.
        foreach (Control control in _viewer.GetGalleryVisualDescendants().OfType<Control>())
        {
            if (control is Grid { Name: "GalleryViewport" } viewport)
                viewport[!BackgroundProperty] = new DynamicResourceExtension(BooruGalleryPanel.StageBackground);
            if (control.Name is "GalleryDock" or "GalleryOverlayControls" or "GalleryOverlayInfo" or "GalleryClose"
                or "GalleryFavoriteIndicator" or "GalleryNativeFavoriteIndicator" or "GalleryControlsEdgeIndicator" or "GalleryPostEdgeIndicator")
                control.SetValue(IsVisibleProperty, false, BindingPriority.Animation);
        }
    }

    private void OnChanged() => Dispatcher.UIThread.Post(() => { if (!_ended) Refresh(); });
    private void OnLanguageChanged(object? sender, EventArgs e) => OnChanged();

    internal void RestoreNormalBounds(Window owner, (Size Size, PixelPoint Position) bounds)
    {
        var screen = owner.Screens.ScreenFromPoint(bounds.Position) ?? owner.Screens.ScreenFromWindow(owner);
        if (screen is null) return;
        WindowStartupFit fit = DialogFit.Compute(bounds.Size, null, new Size(MinWidth, MinHeight),
            screen.WorkingArea, screen.Scaling, bounds.Position);
        WindowStartupLocation = WindowStartupLocation.Manual;
        MinWidth = fit.Minimum.Width;
        MinHeight = fit.Minimum.Height;
        Width = fit.Client.Width;
        Height = fit.Client.Height;
        Position = fit.Position;
    }

    private void CaptureNormalBounds()
    {
        if (IsVisible && WindowState == WindowState.Normal && ClientSize.Width > 0 && ClientSize.Height > 0)
            NormalBounds = (ClientSize, Position);
    }

    private void Move(int delta) => MoveTo(Math.Clamp(_index + delta, 0, _ids.Length - 1));

    private void MoveTo(int index)
    {
        if (_index == index) return;
        _index = index;
        Refresh();
    }

    private void ToggleSelection()
    {
        if (!_selected.Add(CurrentPostId)) _selected.Remove(CurrentPostId);
        _selectionChanged();
        Refresh();
    }

    private async Task DownloadAsync()
    {
        // Resolve the current row again; never let an old button closure target a previous image.
        if (Row is not { } row) return;
        string id = row.Post.PostId;
        if (row.CanRetry) await _search.RetryOriginalAsync(id, row.Post.Identity);
        else if (row.CanLoad) await _search.LoadOriginalAsync(id, row.Post.Identity);
        if (!_ended) Refresh();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing) return;
        _toolbar.RouteKey(e, _viewer);
        if (e.Handled || e.KeyModifiers == KeyModifiers.None && e.Key is Key.Space or Key.Enter) return;
        using var context = EffectiveShortcutBindings.EnterKeyContext("gallery");
        // Let the read-only tags keep the platform's copy and text-selection gestures.
        if (e.Source is Visual source && source.GetSelfAndVisualAncestors().Contains(_tags)) return;
        // Permit only image-view commands through to the composed viewer; local-media commands,
        // slideshow, rating and tagging do not apply to a result that has not been downloaded.
        if (e.Key == Key.Tab || new[] { "gallery.fit", "gallery.actual-size", "gallery.zoom-in", "gallery.zoom-out", "gallery.rotate" }
            .Any(id => CommandBindings.Matches(e, id))) return;
        e.Handled = true;
    }

    private void Refresh()
    {
        if (!_ids.SequenceEqual(_search.View.Rows.Select(value => value.Post.Identity)) || Row is not { } row)
        { Close(); return; }
        Title = Texts.T("booru.ui.post.title", CurrentPostId);
        _toolbar.Present(row, _index, _ids.Length, _selected.Contains(CurrentPostId));
        _tags.Present(row.Post);
        if (!_toolbar.Previous.IsEnabled && _toolbar.Previous.IsFocused || !_toolbar.Next.IsEnabled && _toolbar.Next.IsFocused)
            _viewer.Focus();

        if (_shownId != CurrentPostId)
            _sample.Reset(_search.Samples is not null && row.Post.SampleUrl is not null);
        if (_sample.State != BooruGallerySampleState.Shown && (_shownId != CurrentPostId
            || _shownPreview != row.Preview || !ReferenceEquals(_shownBytes, row.PreviewBytes)))
            ShowPreview(row);

        _sample.Start(_search.Samples, row.Post, _sampleSettings, BooruGallerySampleLoader.DefaultDelay,
            (bytes, width, pixels) => BooruGalleryPanel.DecodeBitmap(bytes, width, pixels), image => ApplySample(row, image));
        RenderCaption(row);
    }

    private void ShowPreview(BooruResultRowView row)
    {
        _shownPreview = row.Preview;
        _shownId = row.Post.PostId;
        _shownBytes = row.PreviewBytes;
        if (row.Preview != BooruPreviewState.Ready || row.PreviewBytes is not { Length: > 0 and <= 8388608 })
        {
            _shownEntry = null;
            OpenActions.Start(() => _viewer.SetEmpty(Texts.T(row.Preview == BooruPreviewState.Loading ? "booru.ui.post.previewLoading"
                : row.Preview == BooruPreviewState.None ? "booru.ui.post.noPreview" : "booru.ui.post.previewUnavailable"), false),
                "booru.viewer.empty");
            return;
        }
        GalleryEntry entry = _shownEntry = CreatePreviewEntry(row);
        OpenActions.Start(() => _viewer.ShowAsync(entry, CancellationToken.None), "booru.viewer.show");
    }

    private bool ApplySample(BooruResultRowView requested, IImage? image)
    {
        if (_ended || !IsVisible || Row is not { } row || !ReferenceEquals(requested.Post, row.Post)) return false;
        if (image is Bitmap bitmap)
        {
            GalleryEntry entry = _shownEntry ?? CreateDisplayEntry(row, length: 0);
            if (!_viewer.TryPresentDisplayVariant(_shownEntry, entry, bitmap)) return false;
            _shownEntry = entry;
            _shownBytes = null;
        }
        RenderCaption(row);
        return true;
    }

    private void RenderCaption(BooruResultRowView row)
    {
        BooruGalleryPicture picture = _sample.State == BooruGallerySampleState.Shown ? BooruGalleryPicture.Sample
            : row.Preview == BooruPreviewState.Ready ? BooruGalleryPicture.Preview : BooruGalleryPicture.None;
        BooruGallerySampleState state = _sample.State == BooruGallerySampleState.NotOffered
            ? BooruGallerySampleState.Deferred : _sample.State;
        BooruGallerySampleLimits limits = _sample.Limits;
        _caption.Text = BooruGalleryStatus.Line(row, picture, state,
            limits.Bytes / (1024 * 1024), limits.MaximumPixels, limits.TimeoutSeconds);
    }

    /// <summary>Builds the synthetic display identity without losing the provider that owns the post.</summary>
    internal static GalleryEntry CreatePreviewEntry(BooruResultRowView row)
    {
        if (row.PreviewBytes is not { Length: > 0 } bytes)
            throw new ArgumentException("A ready Booru preview must contain bytes.", nameof(row));
        return CreateDisplayEntry(row, bytes.Length);
    }

    private static GalleryEntry CreateDisplayEntry(BooruResultRowView row, int length)
    {
        string id = row.Post.PostId;
        MediaAssetReference asset = new(id, id, "preview", id + ".png", id + ".png", length, "", "", id + ".png", true);
        MediaAssetSnapshot snapshot = new("booru-preview", MediaManifestStatus.Missing, null, null, new[] { asset });
        // BooruPostId.SourceId is part of the identity contract; hard-coding Danbooru makes a
        // non-Danbooru provider's synthetic entry report the wrong source to Gallery diagnostics.
        string board = row.Post.Identity.InstanceId == BooruPostKey.DefaultInstance ? "preview" : "preview:" + row.Post.Identity.InstanceId;
        return new(new(new ResourceId(row.Post.SourceId, board, id), "booru-preview", id), snapshot, asset, null, null, false, true);
    }
}
