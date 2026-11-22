using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using SkiaSharp;

namespace MeowChan.Desktop.Booru;

/// <summary>Which picture of the open post is on the stage.</summary>
internal enum BooruGalleryPicture { None, Preview, Sample }

/// <summary>Where the larger (sample) version of the open post stands.</summary>
internal enum BooruGallerySampleState { NotOffered, Loading, Shown, Failed }

/// <summary>The docks around the stage: later panels (tags, details, comments) and a filmstrip are put here, nothing else changes.</summary>
internal enum BooruGalleryDock { Left, Right, Bottom }

/// <summary>The words of the gallery's status line (pure), composed from facts the row already has.</summary>
internal static class BooruGalleryStatus
{
    /// <summary>"Post #12 · 3 of 20 · Danbooru rating: General · Shown: preview · larger version loading… · Original available".</summary>
    internal static string Line(BooruResultRowView row, int index, int count, BooruGalleryPicture picture, BooruGallerySampleState sample, int limitMiB)
    {
        List<string> parts = new()
        {
            Texts.T("booru.ui.post.title", row.Post.PostId),
            Texts.T("booru.gallery.position", index + 1, count),
            Texts.T("booru.ui.post.rating", BooruText.Rating(row.Post.Rating))
        };
        if (picture == BooruGalleryPicture.Preview) parts.Add(Texts.T("booru.gallery.shown.preview"));
        else if (picture == BooruGalleryPicture.Sample) parts.Add(Texts.T("booru.gallery.shown.sample"));
        else if (row.Preview == BooruPreviewState.Loading) parts.Add(Texts.T("booru.ui.post.previewLoading"));
        else parts.Add(Texts.T(row.Preview == BooruPreviewState.None ? "booru.ui.post.noPreview" : "booru.ui.post.previewUnavailable"));
        switch (sample)
        {
            case BooruGallerySampleState.Loading: parts.Add(Texts.T("booru.gallery.sample.loading")); break;
            case BooruGallerySampleState.NotOffered: parts.Add(Texts.T("booru.gallery.sample.none")); break;
            case BooruGallerySampleState.Failed: parts.Add(Texts.T("booru.gallery.sample.failed", limitMiB)); break;
        }
        parts.Add(BooruText.Load(row) ?? BooruText.Original(row.Post.OriginalAccess, row.Post.IsDeleted || row.Post.IsBanned));
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// The third result view of the Danbooru page: one large image of the current page at a time, inside the tab (no window). The frame is a
/// toolbar, a stage between a left and a right dock, a bottom dock and the status line; the docks are empty now and take the side panels and
/// the filmstrip later (<see cref="SetDock"/>). Loading is progressive and only for the open post: the preview that Search already fetched is
/// shown at once, then the provider's sample (the reduced-size version) replaces it through <see cref="IBooruSamplePort"/>; moving to another
/// post cancels the sample that is still loading, and a sample is never fetched for a page ahead. The original is never fetched for display.
/// </summary>
internal sealed class BooruGalleryPanel : Border, IDisposable
{
    internal const string PanelName = "BooruGallery";

    /// <summary>The widest a decoded picture is made (the same bound as the preview decoder); the memory of a shown picture is bounded by it.</summary>
    private const int MaximumDecodeWidth = 2048;
    private const int MaximumDecodedPixels = 40_000_000;

    private readonly CancellationTokenSource _lifetime = new();
    private readonly Image _image = new() { Name = "BooruGalleryImage", Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
    private readonly TextBlock _placeholder = new() { Name = "BooruGalleryPlaceholder", Classes = { "workspace-meta" }, TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _stage = new() { Name = "BooruGalleryStage", Focusable = true, IsTabStop = true, ClipToBounds = true, MinHeight = 320 };
    private readonly Button _previous = new() { Name = "BooruGalleryPrevious" };
    private readonly Button _next = new() { Name = "BooruGalleryNext" };
    private readonly CheckBox _select = new() { Name = "BooruGallerySelect" };
    private readonly Button _download = new() { Name = "BooruGalleryDownload" };
    private readonly Button _openPost = new() { Name = "BooruGalleryOpenPost" };
    private readonly Button _openWindow = new() { Name = "BooruGalleryOpenWindow" };
    private readonly TextBlock _status = new() { Name = "BooruGalleryStatus", TextWrapping = TextWrapping.Wrap, Classes = { "workspace-meta" } };
    private readonly ContentControl _left = new() { Name = "BooruGalleryDockLeft", IsVisible = false };
    private readonly ContentControl _right = new() { Name = "BooruGalleryDockRight", IsVisible = false };
    private readonly ContentControl _bottom = new() { Name = "BooruGalleryDockBottom", IsVisible = false };
    private ImmutableArray<BooruResultRowView> _rows = ImmutableArray<BooruResultRowView>.Empty;
    private string? _currentId;
    private byte[]? _previewBytes;
    private IImage? _previewBitmap;
    private IImage? _sampleBitmap;
    private BooruGalleryPicture _picture;
    private BooruGallerySampleState _sampleState = BooruGallerySampleState.NotOffered;
    private Action? _cancelSample;
    private bool _disposed;

    internal BooruGalleryPanel()
    {
        Name = PanelName;
        Classes.Add("booru-gallery");
        Focusable = false;
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Group);

        _stage.Child = new Panel { Children = { _image, _placeholder } };
        _stage[!BackgroundProperty] = new DynamicResourceExtension("Workspace.Raised");
        _stage.KeyDown += OnKeyDown;
        _stage.Classes.Add("workspace-card-focusable");
        AutomationProperties.SetControlTypeOverride(_stage, AutomationControlType.Image);
        AutomationProperties.SetAccessibilityView(_image, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_placeholder, AccessibilityView.Raw);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _previous.Click += (_, _) => Move(-1);
        _next.Click += (_, _) => Move(1);
        _select.Click += (_, _) => ToggleSelection();
        _download.Click += (_, _) => { if (_currentId is { } id) DownloadRequested?.Invoke(id); };
        _openPost.Click += (_, _) => { if (_currentId is { } id) OpenPostRequested?.Invoke(id); };
        _openWindow.Click += (_, _) => { if (_currentId is { } id) OpenWindowRequested?.Invoke(id); };
        _openWindow.Classes.Add("ghost");
        CommandBindings.EnsureIconBeforeText(_openWindow, "maximize");
        _download.Classes.Add("primary");
        CommandBindings.EnsureIconBeforeText(_download, "download-cloud");

        Border divider = new() { Width = 1, Margin = new Thickness(4, 2), [!BackgroundProperty] = new DynamicResourceExtension("Workspace.Border") };
        StackPanel actions = new() { Name = "BooruGalleryBar", Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center,
            Children = { _previous, _next, divider, _select, _download, _openPost } };
        Grid toolbar = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        toolbar.Children.Add(actions);
        Grid.SetColumn(_openWindow, 1);
        toolbar.Children.Add(_openWindow);

        Grid middle = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        middle.Children.Add(_left);
        Grid.SetColumn(_stage, 1);
        middle.Children.Add(_stage);
        Grid.SetColumn(_right, 2);
        middle.Children.Add(_right);

        StackPanel frame = new() { Spacing = 8, Children = { toolbar, middle, _bottom, _status } };
        Child = frame;
    }

    /// <summary>The post on the stage (null before the first <see cref="Show"/>).</summary>
    internal string? CurrentPostId => _currentId;
    internal BooruGalleryPicture Picture => _picture;
    internal BooruGallerySampleState SampleState => _sampleState;
    internal Control Stage => _stage;
    internal string StatusText => _status.Text ?? string.Empty;
    internal Button DownloadButton => _download;
    internal Button PreviousButton => _previous;
    internal Button NextButton => _next;
    internal CheckBox SelectBox => _select;

    /// <summary>How long a post must stay open before its sample is requested, so flipping through posts sends nothing (a pause, not a limit of the user's data; tests shorten it).</summary>
    internal TimeSpan SampleDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Decodes still-image bytes to at most the given width (null: not decodable); the seam lets tests run without a rendering platform.</summary>
    internal Func<byte[], int, IImage?> Decoder { get; set; } = DecodeBitmap;

    /// <summary>The height of the stage; the view sets it from the window so the picture fills the tab.</summary>
    internal double StageHeight { get => _stage.Height; set => _stage.Height = Math.Max(_stage.MinHeight, value); }

    /// <summary>Reads the setting "largest image" in bytes (the view's setting, read at each load).</summary>
    internal Func<int>? SampleLimitBytes { get; set; }
    /// <summary>Reads the setting "image memory" in bytes (read at each load).</summary>
    internal Func<long>? CacheBytes { get; set; }
    /// <summary>The sample source; null when there is none (the preview then stays).</summary>
    internal Func<IBooruSamplePort?>? Samples { get; set; }
    internal Func<string, bool>? IsSelected { get; set; }
    internal Action<string>? SelectionToggled { get; set; }
    internal Action<string>? DownloadRequested { get; set; }
    internal Action<string>? OpenPostRequested { get; set; }
    internal Action<string>? OpenWindowRequested { get; set; }
    /// <summary>The person moved to another post (the cursor follows).</summary>
    internal Action<string>? Moved { get; set; }
    /// <summary>The person asked to leave the gallery (Escape).</summary>
    internal Action? LeaveRequested { get; set; }

    /// <summary>Puts <paramref name="content"/> (null: nothing) into a dock around the stage. An empty dock takes no room.</summary>
    internal void SetDock(BooruGalleryDock dock, Control? content)
    {
        ContentControl host = dock switch { BooruGalleryDock.Left => _left, BooruGalleryDock.Right => _right, _ => _bottom };
        host.Content = content;
        host.IsVisible = content is not null;
    }

    internal void FocusStage() => _stage.Focus(NavigationMethod.Directional);

    /// <summary>
    /// Shows <paramref name="postId"/> of <paramref name="view"/>'s page. Another post than before: the sample load of the old one is cancelled,
    /// its pictures are released and the preview is shown at once; the same post again only refreshes the preview (when it arrived) and the texts.
    /// </summary>
    internal void Show(BooruSearchView view, string postId)
    {
        if (_disposed) return;
        _rows = view.Rows;
        BooruResultRowView? row = _rows.FirstOrDefault(candidate => candidate.Post.PostId == postId);
        if (row is null)
        {
            ResetPost(null);
            return;
        }
        if (_currentId != postId)
        {
            ResetPost(postId);
            ShowPreview(row);
            StartSample(row);
        }
        else if (!ReferenceEquals(_previewBytes, row.PreviewBytes) && _picture != BooruGalleryPicture.Sample)
            ShowPreview(row);
        Render(row);
    }

    /// <summary>Another view is shown: cancels a sample that is still loading and releases the pictures.</summary>
    internal void Release()
    {
        if (_disposed) return;
        _rows = ImmutableArray<BooruResultRowView>.Empty;
        ResetPost(null);
    }

    private void ResetPost(string? postId)
    {
        _cancelSample?.Invoke();
        _cancelSample = null;
        _currentId = postId;
        _previewBytes = null;
        _picture = BooruGalleryPicture.None;
        _sampleState = BooruGallerySampleState.NotOffered;
        _image.Source = null;
        Release(_previewBitmap);
        _previewBitmap = null;
        Release(_sampleBitmap);
        _sampleBitmap = null;
    }

    private static void Release(IImage? image) => (image as IDisposable)?.Dispose();

    private void ShowPreview(BooruResultRowView row)
    {
        _previewBytes = row.PreviewBytes;
        IImage? bitmap = row.Preview == BooruPreviewState.Ready && row.PreviewBytes is { Length: > 0 } bytes && bytes.Length <= 8 * 1024 * 1024
            ? Decoder(bytes, 512) : null;
        _image.Source = _sampleBitmap ?? bitmap;
        Release(_previewBitmap);
        _previewBitmap = bitmap;
        if (_sampleBitmap is null) _picture = bitmap is null ? BooruGalleryPicture.None : BooruGalleryPicture.Preview;
    }

    private void StartSample(BooruResultRowView row)
    {
        if (Samples?.Invoke() is not { } port || row.Post.SampleUrl is null)
        {
            _sampleState = BooruGallerySampleState.NotOffered;
            return;
        }
        _sampleState = BooruGallerySampleState.Loading;
        BooruPostView post = row.Post;
        int limit = Math.Clamp(SampleLimitBytes?.Invoke() ?? MeowChan.Config.BooruGallerySampleMiBDefault * 1024 * 1024,
            MeowChan.Config.BooruGallerySampleMiBMinimum * 1024 * 1024, MeowChan.Config.BooruGallerySampleMiBMaximum * 1024 * 1024);
        long cache = Math.Clamp(CacheBytes?.Invoke() ?? MeowChan.Config.BooruGalleryCacheMiBDefault * 1024L * 1024,
            MeowChan.Config.BooruGalleryCacheMiBMinimum * 1024L * 1024, MeowChan.Config.BooruGalleryCacheMiBMaximum * 1024L * 1024);
        OpenActions.Start(() => LoadSampleAsync(port, post, limit, cache), "booru.gallery.sample");
    }

    private async Task LoadSampleAsync(IBooruSamplePort port, BooruPostView post, int limit, long cache)
    {
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _cancelSample = () => { try { cancellation.Cancel(); } catch (ObjectDisposedException) { } };
        CancellationToken token = cancellation.Token;
        IImage? bitmap = null;
        Func<byte[], int, IImage?> decode = Decoder;
        try
        {
            await Task.Delay(SampleDelay, token);
            byte[]? bytes = await port.LoadSampleAsync(post, limit, cache, token);
            if (bytes is { Length: > 0 })
                bitmap = await Task.Run(() => decode(bytes, MaximumDecodeWidth), token);
            token.ThrowIfCancellationRequested();
            Adopt(post.PostId, token, bitmap);
            bitmap = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Serilog.Log.Warning(exception, "A Danbooru gallery sample could not be shown");
            if (!token.IsCancellationRequested) Adopt(post.PostId, token, null);
        }
        finally { Release(bitmap); }
    }

    private void Adopt(string postId, CancellationToken token, IImage? bitmap)
    {
        if (Dispatcher.UIThread.CheckAccess()) Apply(postId, token, bitmap);
        else Dispatcher.UIThread.Post(() => Apply(postId, token, bitmap));
    }

    private void Apply(string postId, CancellationToken token, IImage? bitmap)
    {
        // A sample that arrives after the person moved on (or closed the view) is released unseen.
        if (_disposed || token.IsCancellationRequested || _currentId != postId)
        {
            Release(bitmap);
            return;
        }
        if (bitmap is null) _sampleState = BooruGallerySampleState.Failed;
        else
        {
            Release(_sampleBitmap);
            _sampleBitmap = bitmap;
            _image.Source = bitmap;
            _picture = BooruGalleryPicture.Sample;
            _sampleState = BooruGallerySampleState.Shown;
            Release(_previewBitmap);
            _previewBitmap = null;
        }
        if (_rows.FirstOrDefault(row => row.Post.PostId == postId) is { } row) Render(row);
    }

    private void Render(BooruResultRowView row)
    {
        string id = row.Post.PostId;
        int index = IndexOf(id);
        IconButtons.Apply(_previous, "chevron-left", Texts.T("gallery.viewer1") + " · ←");
        IconButtons.Apply(_next, "chevron-right", Texts.T("gallery.viewer2") + " · →");
        _previous.IsEnabled = index > 0;
        _next.IsEnabled = index >= 0 && index < _rows.Length - 1;
        _select.Content = Texts.T("media.tile.selected") + " · Space";
        _select.IsChecked = IsSelected?.Invoke(id) == true;
        AutomationProperties.SetName(_select, Texts.T("media.tile.selected") + ": " + Texts.T("booru.ui.post.title", id));

        _download.Content = Texts.T("booru.ui.load");
        _download.IsEnabled = row.CanLoad || row.CanRetry;
        string? why = BooruText.Load(row) ?? BooruText.Original(row.Post.OriginalAccess, row.Post.IsDeleted || row.Post.IsBanned);
        ToolTip.SetTip(_download, Texts.T("booru.ui.loadFor", id) + Environment.NewLine + why);
        ToolTip.SetShowOnDisabled(_download, true);
        AutomationProperties.SetName(_download, Texts.T("booru.ui.loadFor", id));

        bool hasLink = BooruPostLinks.TryCanonical(row.Post, out string url);
        _openPost.IsVisible = hasLink;
        IconButtons.Apply(_openPost, "external-link", Texts.T("booru.ui.post.openFor", id));
        if (hasLink) ToolTip.SetTip(_openPost, Texts.T("booru.ui.post.openTip", url));

        _openWindow.Content = Texts.T("booru.gallery.openWindow");
        AutomationProperties.SetName(_openWindow, Texts.T("booru.gallery.openWindow"));

        _placeholder.IsVisible = _picture == BooruGalleryPicture.None;
        _placeholder.Text = Texts.T(row.Preview == BooruPreviewState.Loading ? "booru.ui.post.previewLoading"
            : row.Preview == BooruPreviewState.None ? "booru.ui.post.noPreview" : "booru.ui.post.previewUnavailable");
        AutomationProperties.SetName(_stage, Texts.T("booru.ui.post.title", id));
        _status.Text = BooruGalleryStatus.Line(row, Math.Max(0, index), _rows.Length, _picture, _sampleState,
            Math.Max(1, (SampleLimitBytes?.Invoke() ?? MeowChan.Config.BooruGallerySampleMiBDefault * 1024 * 1024) / (1024 * 1024)));
        AutomationProperties.SetName(this, Texts.T("booru.gallery.listName"));
        // A button that became disabled (the first or last post) must not take the keyboard focus with it.
        if (!_previous.IsEnabled && _previous.IsFocused || !_next.IsEnabled && _next.IsFocused) FocusStage();
    }

    private void Move(int delta)
    {
        if (_currentId is null) return;
        int index = IndexOf(_currentId);
        int target = Math.Clamp(index + delta, 0, _rows.Length - 1);
        if (index < 0 || target == index) return;
        Moved?.Invoke(_rows[target].Post.PostId);
    }

    private int IndexOf(string? postId)
    {
        for (int index = 0; index < _rows.Length; index++)
            if (_rows[index].Post.PostId == postId) return index;
        return -1;
    }

    private void MoveTo(int target)
    {
        if (_rows.IsEmpty) return;
        string id = _rows[Math.Clamp(target, 0, _rows.Length - 1)].Post.PostId;
        if (id != _currentId) Moved?.Invoke(id);
    }

    private void ToggleSelection()
    {
        if (_currentId is { } id) SelectionToggled?.Invoke(id);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ShortcutService.IsCapturing || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Left or Key.Up: e.Handled = true; Move(-1); break;
            case Key.Right or Key.Down: e.Handled = true; Move(1); break;
            case Key.Home: e.Handled = true; MoveTo(0); break;
            case Key.End: e.Handled = true; MoveTo(_rows.Length - 1); break;
            case Key.Space: e.Handled = true; ToggleSelection(); break;
            case Key.Escape: e.Handled = true; LeaveRequested?.Invoke(); break;
        }
    }

    /// <summary>Decodes still-image bytes to at most <paramref name="maximumWidth"/> pixels wide; null when they are not a decodable image.</summary>
    internal static Bitmap? DecodeBitmap(byte[] bytes, int maximumWidth)
    {
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using SKManagedStream managed = new(stream, disposeManagedStream: false);
            using SKCodec? codec = SKCodec.Create(managed);
            if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > MaximumDecodedPixels) return null;
            stream.Position = 0;
            return ImageOrientation.DecodeToWidth(stream, Math.Min(ImageOrientation.DisplaySize(codec).Width, maximumWidth));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        // May run on any thread (the page is disposed by its host): only what is not a control is touched here.
        Release(_previewBitmap);
        Release(_sampleBitmap);
        _previewBitmap = _sampleBitmap = null;
        _lifetime.Dispose();
    }
}
