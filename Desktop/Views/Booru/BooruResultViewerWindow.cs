using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Storage;
using SkiaSharp;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// A Booru-only composition of the existing gallery image surface. Only the bounded preview bytes
/// already fetched by Search are decoded; opening, marking and navigation never request originals.
/// The synthetic entries below are display identities, never published assets or library entries.
/// </summary>
internal sealed class BooruResultViewerWindow : Window
{
    private readonly BooruSearchModel _search;
    private readonly string[] _ids;
    private readonly HashSet<string> _selected;
    private readonly Action _selectionChanged;
    private readonly GalleryViewer _viewer = new();
    private readonly CheckBox _select = new() { Name = "BooruViewerSelect" };
    private readonly Button _download = new() { Name = "BooruViewerDownload" };
    private readonly Button _previous = new() { Name = "BooruViewerPrevious" };
    private readonly Button _next = new() { Name = "BooruViewerNext" };
    private readonly TextBlock _caption = new() { Name = "BooruViewerCaption", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _status = new() { Name = "BooruViewerStatus", TextWrapping = TextWrapping.Wrap };
    private int _index;
    private byte[]? _shownBytes;
    private BooruPreviewState? _shownPreview;
    private string? _shownId;
    private bool _ended;

    internal string CurrentPostId => _ids[_index];
    internal Task Cleanup { get; private set; } = Task.CompletedTask;
    private BooruResultRowView? Row => _search.View.Rows.FirstOrDefault(row => row.Post.PostId == CurrentPostId);

    internal BooruResultViewerWindow(BooruSearchModel search, string postId, HashSet<string> selected, Action selectionChanged)
    {
        _search = search;
        _ids = search.View.Rows.Select(row => row.Post.PostId).ToArray();
        _index = Array.IndexOf(_ids, postId);
        if (_index < 0) throw new ArgumentException("The post is not in this result page.", nameof(postId));
        _selected = selected;
        _selectionChanged = selectionChanged;
        Name = "BooruResultViewer";
        DialogWindow.Configure(this, DialogSize.XL, modal: false);
        _viewer.Configure(false, false, false, true, 0);
        _viewer.DecodeImageAsync = DecodePreviewAsync;
        // This host supplies its own Booru chrome, rather than changing shared gallery behavior.
        // A higher-priority presentation value keeps the gallery's own layout refresh from exposing
        // local-file actions on an unsaved provider preview. No controls are detached or reparented.
        foreach (Control control in _viewer.GetGalleryVisualDescendants().OfType<Control>().Where(control =>
            control.Name is "GalleryDock" or "GalleryOverlayControls" or "GalleryOverlayInfo" or "GalleryClose"
                or "GalleryFavoriteIndicator" or "GalleryNativeFavoriteIndicator" or "GalleryControlsEdgeIndicator" or "GalleryPostEdgeIndicator"))
            control.SetValue(IsVisibleProperty, false, BindingPriority.Animation);
        _viewer.AddHandler(ContextRequestedEvent, (_, e) => e.Handled = true, RoutingStrategies.Tunnel);
        _viewer.CloseRequested += Close;
        _viewer.MoveRequested += Move;

        Button close = new() { Name = "BooruViewerClose", Content = Texts.T("action.close") + " · Esc" };
        close.Click += (_, _) => Close();
        _previous.Click += (_, _) => Move(-1);
        _next.Click += (_, _) => Move(1);
        _select.Click += (_, _) => ToggleSelection();
        _download.Click += async (_, _) => await DownloadAsync();
        WrapPanel toolbar = new() { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(12) };
        foreach (Control control in new Control[] { close, _previous, _next, _select, _download }) toolbar.Children.Add(control);
        StackPanel footer = new() { Spacing = 4, Margin = new Thickness(12), Children = { _caption, _status } };
        Grid root = new() { RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        root.Children.Add(toolbar);
        root.Children.Add(_viewer); Grid.SetRow(_viewer, 1);
        root.Children.Add(footer); Grid.SetRow(footer, 2);
        DialogWindow.Frame(this, root, modal: false);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        AutomationProperties.SetLiveSetting(_caption, AutomationLiveSetting.Polite);
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Opened += (_, _) => { Refresh(); _viewer.Focus(); };
        _search.Changed += OnChanged;
        Texts.Changed += OnLanguageChanged;
        Closed += (_, _) =>
        {
            _ended = true;
            _search.Changed -= OnChanged;
            Texts.Changed -= OnLanguageChanged;
            Cleanup = _viewer.StopAsync();
        };
    }

    private void OnChanged() => Dispatcher.UIThread.Post(() => { if (!_ended) Refresh(); });
    private void OnLanguageChanged(object? sender, EventArgs e) => OnChanged();

    private void Move(int delta)
    {
        _index = Math.Clamp(_index + delta, 0, _ids.Length - 1);
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
        if (row.CanRetry) await _search.RetryOriginalAsync(id);
        else if (row.CanLoad) await _search.LoadOriginalAsync(id);
        if (!_ended) Refresh();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing) return;
        if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Escape) { e.Handled = true; Close(); return; }
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Left or Key.Right)
        { e.Handled = true; Move(e.Key == Key.Left ? -1 : 1); return; }
        // Native Tab/Space/Enter activation of toolbar controls stays accessible. On the picture,
        // Space and Enter mark this image, not playback or an implicit download.
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Space or Key.Enter
            && !ItemActivation.IsOwnedByInteractivePart(e.Source, _viewer))
        { e.Handled = true; ToggleSelection(); return; }
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Space or Key.Enter) return;
        using var context = EffectiveShortcutBindings.EnterKeyContext("gallery");
        if (CommandBindings.Matches(e, "media.export-original"))
        { e.Handled = true; _ = DownloadAsync(); return; }
        // Permit only image-view commands through to the composed viewer; local-media commands,
        // slideshow, rating and tagging do not apply to a result that has not been downloaded.
        if (e.Key == Key.Tab || new[] { "gallery.fit", "gallery.actual-size", "gallery.zoom-in", "gallery.zoom-out", "gallery.rotate" }
            .Any(id => CommandBindings.Matches(e, id))) return;
        e.Handled = true;
    }

    private void Refresh()
    {
        if (!_ids.SequenceEqual(_search.View.Rows.Select(value => value.Post.PostId)) || Row is not { } row)
        { Close(); return; }
        Title = Texts.T("booru.ui.post.title", CurrentPostId);
        _caption.Text = $"{Title} · {_index + 1}/{_ids.Length} · {Texts.T("booru.mediaRole.Preview")} · {Texts.T("booru.ui.post.rating", BooruText.Rating(row.Post.Rating))}";
        _previous.Content = Texts.T("gallery.viewer1") + " · ←";
        _next.Content = Texts.T("gallery.viewer2") + " · →";
        _previous.IsEnabled = _index > 0;
        _next.IsEnabled = _index < _ids.Length - 1;
        _select.Content = Texts.T("media.tile.selected") + " · Space / Enter";
        _select.IsChecked = _selected.Contains(CurrentPostId);
        _download.Content = Texts.T("booru.ui.load") + " · " + CommandCatalog.ShortcutDisplay("media.export-original");
        AutomationProperties.SetName(_download, Texts.T("booru.ui.loadFor", CurrentPostId));
        _download.IsEnabled = row.CanLoad || row.CanRetry;
        string? previewNote = row.Preview switch
        {
            BooruPreviewState.Loading => Texts.T("booru.ui.post.previewLoading"),
            BooruPreviewState.None => Texts.T("booru.ui.post.noPreview"),
            BooruPreviewState.Ready => null,
            _ => Texts.T("booru.ui.post.previewUnavailable")
        };
        _status.Text = (previewNote is null ? string.Empty : previewNote + " · ")
            + (BooruText.Load(row) ?? BooruText.Original(row.Post.OriginalAccess, row.Post.IsDeleted || row.Post.IsBanned));
        ToolTip.SetTip(_download, _status.Text);
        ToolTip.SetShowOnDisabled(_download, true);
        if (_shownId == CurrentPostId && _shownPreview == row.Preview && ReferenceEquals(_shownBytes, row.PreviewBytes)) return;
        _shownPreview = row.Preview;
        _shownId = CurrentPostId;
        _shownBytes = row.PreviewBytes;
        if (row.Preview != BooruPreviewState.Ready || row.PreviewBytes is not { Length: > 0 and <= 8388608 })
        {
            _ = _viewer.SetEmpty(Texts.T(row.Preview == BooruPreviewState.Loading ? "booru.ui.post.previewLoading"
                : row.Preview == BooruPreviewState.None ? "booru.ui.post.noPreview" : "booru.ui.post.previewUnavailable"), false);
            return;
        }
        string id = CurrentPostId;
        MediaAssetReference asset = new(id, id, "preview", id + ".png", id + ".png", row.PreviewBytes.Length, "", "", id + ".png", true);
        MediaAssetSnapshot snapshot = new("booru-preview", MediaManifestStatus.Missing, null, null, new[] { asset });
        GalleryEntry entry = new(new(new ResourceId("danbooru", "preview", id), "booru-preview", id), snapshot, asset, null, null, false, true);
        _ = _viewer.ShowAsync(entry, CancellationToken.None);
    }

    private Task<(Bitmap Bitmap, GalleryImageInfo Info)> DecodePreviewAsync(GalleryEntry entry, GalleryDecodeRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        byte[] bytes = _search.View.Rows.First(row => row.Post.PostId == entry.Asset.AssetId).PreviewBytes!;
        using MemoryStream stream = new(bytes, writable: false);
        using SKCodec? codec = SKCodec.Create(new SKManagedStream(stream, disposeManagedStream: false));
        if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 40_000_000)
            throw new InvalidDataException("Invalid Booru preview.");
        stream.Position = 0;
        Bitmap bitmap = ImageOrientation.DecodeToWidth(stream, Math.Min(ImageOrientation.DisplaySize(codec).Width, 2048));
        return Task.FromResult((bitmap, new GalleryImageInfo(bitmap.PixelSize.Width, bitmap.PixelSize.Height, "Preview", false)));
    }
}
