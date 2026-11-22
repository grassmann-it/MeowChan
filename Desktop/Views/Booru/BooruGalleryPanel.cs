using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using SkiaSharp;

namespace MeowChan.Desktop.Booru;

/// <summary>Which picture of the open post is on the stage.</summary>
internal enum BooruGalleryPicture { None, Preview, Sample }

/// <summary>Where the larger (sample) version of the open post stands.</summary>
internal enum BooruGallerySampleState { NotOffered, Deferred, Loading, Shown, Failed, PixelLimit, TimedOut }

/// <summary>The docks around the stage: later panels (tags, details, comments) and a filmstrip are put here, nothing else changes.</summary>
internal enum BooruGalleryDock { Left, Right, Bottom }

/// <summary>The words of the gallery's status line (pure), composed from facts the row already has.</summary>
internal static class BooruGalleryStatus
{
    /// <summary>"Post #12 · Danbooru rating: General · Shown: preview · larger version loading… · Original available".</summary>
    internal static string Line(BooruResultRowView row, BooruGalleryPicture picture, BooruGallerySampleState sample, int limitMiB,
        int maximumPixels = Config.BooruGalleryMaximumPixelsDefault, int timeoutSeconds = Config.BooruGalleryTimeoutSecondsDefault)
    {
        List<string> parts = new()
        {
            Texts.T("booru.ui.post.title", row.Post.PostId),
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
            case BooruGallerySampleState.PixelLimit: parts.Add(Texts.T("booru.gallery.sample.pixelLimit", maximumPixels)); break;
            case BooruGallerySampleState.TimedOut: parts.Add(Texts.T("booru.gallery.sample.timeout", timeoutSeconds)); break;
        }
        parts.Add(BooruText.Load(row) ?? BooruText.Original(row.Post.OriginalAccess, row.Post.IsDeleted || row.Post.IsBanned));
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// The third result view of the Danbooru page: one large image of the current page at a time, inside the tab (no window). The frame is a
/// toolbar, a stage with a source-tag panel and the status line. Loading is progressive and only for the open post: the preview that Search already fetched is
/// shown at once, then the provider's sample (the reduced-size version) replaces it through <see cref="IBooruSamplePort"/>; moving to another
/// post cancels the sample that is still loading, and a sample is never fetched for a page ahead. The original is never fetched for display.
/// </summary>
internal sealed partial class BooruGalleryPanel : Border, IDisposable
{
    internal const string PanelName = "BooruGallery";

    private readonly BooruGallerySampleLoader _sample = new();
    private readonly Image _image = new() { Name = "BooruGalleryImage", Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both };
    private readonly TextBlock _placeholder = new() { Name = "BooruGalleryPlaceholder", Classes = { "workspace-meta" }, TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _stage = new() { Name = "BooruGalleryStage", Focusable = true, IsTabStop = true, ClipToBounds = true, MinHeight = 320 };
    internal const string StageBackground = "Workspace.Raised";
    private readonly BooruGalleryToolbar _toolbar = new("BooruGallery") { IsVisible = false };
    private readonly PointerFocusReclaim _toolbarFocus;
    private readonly BooruGalleryTags _tags = new();
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
    private bool _requestSample;
    private bool _samplesSuspended;
    private bool _disposed;

    internal BooruGalleryPanel()
    {
        Name = PanelName;
        Classes.Add("booru-gallery");
        Focusable = false;
        AutomationProperties.SetControlTypeOverride(this, AutomationControlType.Group);

        _stage.Child = new Panel { Children = { _image, _placeholder } };
        _stage[!BackgroundProperty] = new DynamicResourceExtension(StageBackground);
        AddHandler(KeyDownEvent, (_, e) => RouteKey(e), RoutingStrategies.Tunnel);
        _stage.Classes.Add("workspace-card-focusable");
        AutomationProperties.SetControlTypeOverride(_stage, AutomationControlType.Image);
        AutomationProperties.SetAccessibilityView(_image, AccessibilityView.Raw);
        AutomationProperties.SetAccessibilityView(_placeholder, AccessibilityView.Raw);
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);

        _toolbar.Move = Move;
        _toolbar.MoveToBoundary = last => MoveTo(last ? _rows.Length - 1 : 0);
        _toolbar.ToggleSelection = ToggleSelection;
        _toolbar.Load = () => { if (_currentId is { } id) DownloadRequested?.Invoke(id); };
        _toolbar.OpenPost = () => { if (_currentId is { } id) OpenPostRequested?.Invoke(id); };
        _toolbar.OpenWindow = () => { if (_currentId is { } id) OpenWindowRequested?.Invoke(id); };
        _toolbar.Close = () => LeaveRequested?.Invoke();
        _toolbarFocus = PointerFocusReclaim.Attach(_toolbar, FocusStage);
        SetDock(BooruGalleryDock.Right, _tags);

        Grid middle = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        middle.Children.Add(_left);
        Grid.SetColumn(_stage, 1);
        middle.Children.Add(_stage);
        Grid.SetColumn(_right, 2);
        middle.Children.Add(_right);
        _right.VerticalContentAlignment = VerticalAlignment.Stretch;

        StackPanel frame = new() { Spacing = 8, Children = { _toolbar, middle, _bottom, _status } };
        Child = frame;
        AttachedToVisualTree += (_, _) => ObserveVisibility();
        DetachedFromVisualTree += (_, _) => StopObservingVisibility();
    }

    /// <summary>The post on the stage (null before the first <see cref="Show"/>).</summary>
    internal string? CurrentPostId => _currentId;
    internal BooruGalleryPicture Picture => _picture;
    internal BooruGallerySampleState SampleState => _sample.State;
    internal Control Stage => _stage;
    internal string StatusText => _status.Text ?? string.Empty;
    internal Button DownloadButton => _toolbar.Download;
    internal Button PreviousButton => _toolbar.Previous;
    internal Button NextButton => _toolbar.Next;
    internal CheckBox SelectBox => _toolbar.Select;

    /// <summary>How long a post must stay open before its sample is requested, so flipping through posts sends nothing (a pause, not a limit of the user's data; tests shorten it).</summary>
    internal TimeSpan SampleDelay { get; set; } = BooruGallerySampleLoader.DefaultDelay;

    /// <summary>Decodes still-image bytes to at most the given width (null: not decodable); the seam lets tests run without a rendering platform.</summary>
    internal Func<byte[], int, IImage?>? Decoder { get; set; }

    /// <summary>The height of the stage; the view sets it from the window so the picture fills the tab.</summary>
    internal double StageHeight
    {
        get => _stage.Height;
        set => _stage.Height = _tags.MaxHeight = Math.Max(_stage.MinHeight, value);
    }

    internal Func<BooruGallerySampleLimits>? SampleSettings { get; set; }
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

    internal void FocusStage()
    {
        if (!_disposed && _currentId is not null && _stage.IsEffectivelyVisible) _stage.Focus(NavigationMethod.Directional);
    }

    internal void RouteKey(KeyEventArgs e)
    {
        if (_currentId is not null) _toolbar.RouteKey(e, _stage);
    }

    /// <summary>
    /// Shows <paramref name="postId"/> of <paramref name="view"/>'s page. Another post than before: the sample load of the old one is cancelled,
    /// its pictures are released and the preview is shown at once; the same post again only refreshes the preview (when it arrived) and the texts.
    /// </summary>
    internal void Show(BooruSearchView view, string? postId, bool requestSample = false)
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
            _sample.Reset(Samples?.Invoke() != null && row.Post.SampleUrl is not null);
        }
        else if (!ReferenceEquals(_previewBytes, row.PreviewBytes) && _picture != BooruGalleryPicture.Sample)
            ShowPreview(row);
        _requestSample |= requestSample || !view.Restored;
        StartSampleIfVisible(row);
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
        _sample.Reset(offered: false);
        _requestSample = false;
        _currentId = postId;
        _toolbar.IsVisible = postId is not null;
        if (postId is null)
        {
            _tags.Present(null);
            _right.IsVisible = false;
        }
        _previewBytes = null;
        _picture = BooruGalleryPicture.None;
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
            ? Decode(bytes, 512, Config.BooruGalleryMaximumPixelsDefault).Bitmap : null;
        _image.Source = _sampleBitmap ?? bitmap;
        Release(_previewBitmap);
        _previewBitmap = bitmap;
        if (_sampleBitmap is null) _picture = bitmap is null ? BooruGalleryPicture.None : BooruGalleryPicture.Preview;
    }

    internal void SuspendSamples(bool suspended)
    {
        _samplesSuspended = suspended;
        if (suspended) _sample.Pause();
        else ResumeWhenVisible();
    }

    private void StartSampleIfVisible(BooruResultRowView row)
    {
        if (_requestSample && CanShowSample)
            _sample.Start(Samples?.Invoke(), row.Post, SampleSettings, SampleDelay, Decode, bitmap => ApplySample(row.Post.PostId, bitmap));
    }

    private (IImage? Bitmap, BooruGallerySampleState State) Decode(byte[] bytes, int width, int maximumPixels)
    {
        if (Decoder is not { } decoder) return DecodeBitmap(bytes, width, maximumPixels);
        IImage? bitmap = decoder(bytes, width);
        return (bitmap, bitmap is null ? BooruGallerySampleState.Failed : BooruGallerySampleState.Shown);
    }

    private bool ApplySample(string postId, IImage? bitmap)
    {
        if (_disposed || !CanShowSample || _currentId != postId) return false;
        if (bitmap is not null)
        {
            _image.Source = bitmap;
            Release(_sampleBitmap);
            _sampleBitmap = bitmap;
            _picture = BooruGalleryPicture.Sample;
            Release(_previewBitmap);
            _previewBitmap = null;
        }
        if (_rows.FirstOrDefault(row => row.Post.PostId == postId) is { } row) Render(row);
        return true;
    }

    private void Render(BooruResultRowView row)
    {
        string id = row.Post.PostId;
        Button? focusedNavigation = PreviousButton.IsFocused ? PreviousButton : NextButton.IsFocused ? NextButton : null;
        _toolbar.Present(row, IndexOf(id), _rows.Length, IsSelected?.Invoke(id) == true);
        _tags.Present(row.Post);
        _right.IsVisible = _tags.IsVisible;

        _placeholder.IsVisible = _picture == BooruGalleryPicture.None;
        _placeholder.Text = Texts.T(row.Preview == BooruPreviewState.Loading ? "booru.ui.post.previewLoading"
            : row.Preview == BooruPreviewState.None ? "booru.ui.post.noPreview" : "booru.ui.post.previewUnavailable");
        AutomationProperties.SetName(_stage, Texts.T("booru.ui.post.title", id));
        BooruGallerySampleLimits limits = _sample.Limits;
        _status.Text = BooruGalleryStatus.Line(row, _picture, _sample.State,
            limits.Bytes / (1024 * 1024), limits.MaximumPixels, limits.TimeoutSeconds);
        AutomationProperties.SetName(this, Texts.T("booru.gallery.listName"));
        // A button that became disabled (the first or last post) must not take the keyboard focus with it.
        if (focusedNavigation is { IsEnabled: false }) FocusStage();
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

    internal static (Bitmap? Bitmap, BooruGallerySampleState State) DecodeBitmap(byte[] bytes, int maximumWidth, int maximumPixels)
    {
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using SKManagedStream managed = new(stream, disposeManagedStream: false);
            using SKCodec? codec = SKCodec.Create(managed);
            if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0) return (null, BooruGallerySampleState.Failed);
            if ((long)codec.Info.Width * codec.Info.Height > maximumPixels) return (null, BooruGallerySampleState.PixelLimit);
            stream.Position = 0;
            Bitmap? bitmap = ImageOrientation.DecodeToWidth(stream, Math.Min(ImageOrientation.DisplaySize(codec).Width, maximumWidth));
            return (bitmap, bitmap is null ? BooruGallerySampleState.Failed : BooruGallerySampleState.Shown);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { return (null, BooruGallerySampleState.Failed); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _toolbarFocus.Dispose();
        _sample.Pause();
        // May run on any thread (the page is disposed by its host): only what is not a control is touched here.
        Release(_previewBitmap);
        Release(_sampleBitmap);
        _previewBitmap = _sampleBitmap = null;
    }
}
