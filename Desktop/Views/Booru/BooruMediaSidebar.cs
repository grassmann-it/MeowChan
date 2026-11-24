using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using SkiaSharp;

namespace MeowChan.Desktop.Booru;

internal sealed class BooruMediaSidebar : Grid, IDisposable
{
    private const double Inset = 8;
    private const double TagRoom = 96;
    private const int PreviewEdge = 512;

    private readonly TextBlock _title = new()
    {
        Name = "BooruMediaTitle", Classes = { "card-title" }, VerticalAlignment = VerticalAlignment.Center,
        MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis
    };
    private readonly MediaPositionPill _position = new("BooruMediaPosition") { Margin = new Thickness(Inset, 0) };
    private readonly MediaCompactHeader _header;
    private readonly Image _image = new() { Name = "BooruMediaPreview", Stretch = Stretch.Uniform };
    private readonly TextBlock _placeholder = new()
    {
        Classes = { "workspace-meta" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(Inset),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Border _kind = WorkspaceChips.CreateCornerBadge("BooruMediaKind", HorizontalAlignment.Right, VerticalAlignment.Top);
    private readonly Flyout _facts = new();
    private readonly BooruGallerySampleLoader _sample = new();
    private readonly List<Visual> _visibilityAncestors = new();
    private readonly List<(Button Button, TextBlock Label)> _actionLabels = new();
    private BooruResultTarget? _target;
    private byte[]? _previewBytes;
    private IDisposable? _bitmap;
    private BooruGalleryPicture _picture;
    private double _aspect = 1;
    private bool _disposed;

    internal PanelCloseButton Close { get; } = new("BooruMediaClose") { Focusable = false, IsTabStop = false };
    internal Button OpenGallery { get; } = new()
    {
        Name = "BooruMediaOpenGallery", Focusable = false, IsTabStop = false, Classes = { "ghost" },
        Padding = new Thickness(0), MinWidth = 0, MinHeight = 0, ClipToBounds = true,
        HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch
    };
    internal PanelActionStack Actions { get; } = new("BooruMediaActions") { Margin = new Thickness(Inset, 0) };
    internal ScrollViewer Tags { get; } = new()
    {
        Name = "BooruMediaTags", Margin = new Thickness(Inset, 0), Focusable = true, IsTabStop = true,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
    };

    internal BooruMediaSidebar()
    {
        Name = "BooruMediaSidebar";
        IsVisible = false;
        Margin = new Thickness(Inset, 0, 0, 0);
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*");
        RowSpacing = Inset;
        ClipToBounds = true;
        _header = new MediaCompactHeader(_title, _position, Close) { Margin = new Thickness(Inset, 0) };
        _header.Meta.Name = "BooruMediaSummary";
        _header.Meta.AddHandler(PointerReleasedEvent, ShowFacts);
        _kind.Margin = new Thickness(0, 4, 4, 0);
        OpenGallery.Content = new Panel { Children = { _image, _placeholder, _kind } };
        AutomationProperties.SetAccessibilityView(_image, AccessibilityView.Raw);
        SetRow(OpenGallery, 1);
        SetRow(Actions, 2);
        SetRow(Tags, 3);
        Children.AddRange([_header, OpenGallery, Actions, Tags]);
        AutomationProperties.SetControlTypeOverride(this, Avalonia.Automation.Peers.AutomationControlType.Group);
        AttachedToVisualTree += (_, _) => ObserveVisibility();
        DetachedFromVisualTree += (_, _) => StopObservingVisibility();
    }

    internal void PresentPosition(int index, int count, bool marked) =>
        _position.Show(new GalleryTagPanelPosition(index + 1, count, false), marked);

    internal void Present(BooruResultTarget target, BooruResultRowView row)
    {
        if (_target != target)
        {
            Release();
            _target = target;
            _sample.Reset(row.Post.SampleUrl is not null);
            Tags.Offset = default;
        }
        if (_picture != BooruGalleryPicture.Sample && !ReferenceEquals(_previewBytes, row.PreviewBytes))
        {
            _previewBytes = row.PreviewBytes;
            Bitmap? preview = row.Preview == BooruPreviewState.Ready && _previewBytes is { Length: > 0 } bytes
                ? DecodePreview(bytes) : null;
            ReplacePicture(preview, preview is null ? BooruGalleryPicture.None : BooruGalleryPicture.Preview);
        }

        BooruPostView post = row.Post;
        _title.Text = Texts.T("booru.ui.post.title", post.PostId);
        AutomationProperties.SetName(this, Texts.T("media.layout.panel"));
        AutomationProperties.SetName(Tags, Texts.T("booru.tags.sourceLabel"));
        AutomationProperties.SetName(OpenGallery, CommandCatalog.Label("gallery.open") + " · " + _title.Text);
        ToolTip.SetTip(OpenGallery, AutomationProperties.GetName(OpenGallery));
        Close.Localize(Texts.T("action.close") + " · " + Texts.T("media.layout.panel"));
        _placeholder.Text = Texts.T(row.Preview switch
        {
            BooruPreviewState.Loading => "booru.ui.post.previewLoading",
            BooruPreviewState.None => "booru.ui.post.noPreview",
            _ => "booru.ui.post.previewUnavailable"
        });

        string? extension = post.Original?.Extension?.TrimStart('.');
        string? kind = extension?.ToLowerInvariant() switch
        {
            "gif" => Texts.T("media.tile.kind.gif"),
            "mp4" or "webm" or "zip" => Texts.T("media.tile.kind.video"),
            _ => null
        };
        _kind.IsVisible = kind is not null;
        ((TextBlock)_kind.Child!).Text = kind;
        int? width = post.Original?.Width;
        int? height = post.Original?.Height;
        _aspect = width is > 0 && height is > 0 ? (double)width.Value / height.Value : 1;
        string? dimensions = width is > 0 && height is > 0 ? $"{width} × {height}" : null;
        string? bytesText = post.Original?.ExpectedSize is { } size ? SizeText.Format(size) : null;
        string? rating = post.Rating == BooruRating.Unknown ? null : BooruText.Rating(post.Rating);
        string? score = post.Score?.ToString(Texts.Culture);
        string? favorites = post.FavoriteCount?.ToString(Texts.Culture);
        string typeAndDimensions = string.Join(" ", new[] { extension, dimensions }.Where(part => !string.IsNullOrEmpty(part)));
        string summary = string.Join(" · ", new[] { rating, score, favorites is null ? null : "♥ " + favorites, typeAndDimensions, bytesText }
            .Where(part => !string.IsNullOrEmpty(part)));
        _header.Describe(summary, "");

        MediaFactGrid facts = new("BooruMediaFacts") { Margin = new Thickness(Inset) };
        facts.Add(Texts.T("booru.ui.post.field.rating"), rating, "BooruMediaRating");
        facts.Add(Texts.T("booru.ui.post.field.score"), score, "BooruMediaScore");
        facts.Add(Texts.T("booru.ui.post.field.favorites"), favorites);
        facts.Add(Texts.T("booru.ui.post.field.file"), extension);
        facts.Add(Texts.T("media.fact.dimensions"), dimensions);
        facts.Add(Texts.T("media.fact.source"), bytesText);
        facts.Add(Texts.T("booru.ui.post.field.created"), post.CreatedAt?.ToLocalTime().ToString("g", Texts.Culture));
        facts.Add(Texts.T("media.fact.saved"), BooruText.Load(row) ?? Texts.T("booru.ui.result.localNo"));
        facts.Add(Texts.T("booru.media.variants.heading"), _picture == BooruGalleryPicture.None
            ? null : Texts.T("booru.mediaRole." + _picture));
        _facts.Content = facts;
        InvalidateMeasure();
    }

    private void ShowFacts(object? sender, PointerReleasedEventArgs e)
    {
        if (_target is null || e.InitialPressMouseButton != MouseButton.Left || !IsEffectivelyVisible) return;
        _facts.ShowAt(_header.Meta);
        e.Handled = true;
    }

    internal void RequestSample(IBooruSamplePort? port, BooruResultTarget target, Func<BooruGallerySampleLimits> limits,
        Func<bool> current, Action changed)
    {
        if (_disposed || _target != target || !this.IsAttachedToVisualTree() || !IsEffectivelyVisible) return;
        _sample.Start(port, target.Post, limits, BooruGallerySampleLoader.DefaultDelay,
            static (bytes, width, pixels) => BooruGalleryPanel.DecodeBitmap(bytes, width, pixels),
            image =>
            {
                if (_disposed || _target != target || !IsEffectivelyVisible || !current()) return false;
                ReplacePicture(image, BooruGalleryPicture.Sample);
                return true;
            }, changed);
    }

    private static Bitmap? DecodePreview(byte[] bytes)
    {
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            using SKManagedStream managed = new(stream, disposeManagedStream: false);
            using SKCodec? codec = SKCodec.Create(managed);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0
                || (long)codec.Info.Width * codec.Info.Height > Config.BooruGalleryMaximumPixelsDefault) return null;
            var display = ImageOrientation.DisplaySize(codec);
            double scale = Math.Min(1, (double)PreviewEdge / Math.Max(display.Width, display.Height));
            stream.Position = 0;
            return ImageOrientation.DecodeToWidth(stream, Math.Max(1, (int)Math.Floor(display.Width * scale)));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    private void ReplacePicture(IImage? image, BooruGalleryPicture picture)
    {
        _image.Source = image;
        _placeholder.IsVisible = image is null;
        _bitmap?.Dispose();
        _bitmap = image as IDisposable;
        _picture = picture;
    }

    internal void ClearActions()
    {
        Actions.Children.Clear();
        _actionLabels.Clear();
    }

    internal void AddAction(Button button, string icon, string label)
    {
        TextBlock text = new() { Text = label, Classes = { "panel-action-label" }, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        StackPanel line = new() { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        line.Children.Add(text);
        button.AttachedToVisualTree += (_, _) =>
        {
            if (line.Children.Count == 1) line.Children.Insert(0, CommandBindings.NamedIcon(icon));
        };
        PanelActionStack.SizeButton(button, compactSpacing: true);
        button.MinWidth = 0;
        button.Content = line;
        _actionLabels.Add((button, text));
        Actions.Children.Add(button);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = Math.Min(Width, availableSize.Width);
        if (!double.IsFinite(width)) width = Config.MediaPanelWidthDefault;
        double room = Math.Max(0, width - 2 * Inset);
        _header.Measure(new Size(width, double.PositiveInfinity));
        double actionHeight = _actionLabels.Count == 0 ? 0 : WorkspaceMetrics.InputHeight;
        double pictureRoom = Math.Max(0, availableSize.Height - _header.DesiredSize.Height - actionHeight - 3 * RowSpacing - TagRoom);
        OpenGallery.Height = Math.Min(width / _aspect, pictureRoom);
        double extra = room - _actionLabels.Count * WorkspaceMetrics.InputHeight - Math.Max(0, _actionLabels.Count - 1) * Actions.ItemSpacing;
        foreach (var (button, label) in _actionLabels)
        {
            FormattedText text = new(label.Text ?? string.Empty, Texts.Culture, label.FlowDirection,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch), label.FontSize, label.Foreground);
            double full = Math.Ceiling(text.Width + 36); // 16 icon, 4 gap, 8 on each edge
            double added = Math.Max(0, full - WorkspaceMetrics.InputHeight);
            bool showLabel = added <= extra;
            button.Width = showLabel ? Math.Max(WorkspaceMetrics.InputHeight, full) : WorkspaceMetrics.InputHeight;
            label.IsVisible = showLabel;
            button.Padding = showLabel ? new Thickness(Inset, 0) : default;
            if (showLabel) extra -= added;
        }
        return base.MeasureOverride(availableSize);
    }

    private void ObserveVisibility()
    {
        foreach (Visual ancestor in this.GetSelfAndVisualAncestors())
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += AncestorVisibilityChanged;
        }
    }

    private void StopObservingVisibility()
    {
        foreach (Visual ancestor in _visibilityAncestors) ancestor.PropertyChanged -= AncestorVisibilityChanged;
        _visibilityAncestors.Clear();
        _sample.Pause();
    }

    private void AncestorVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && !e.GetNewValue<bool>())
        {
            _facts.Hide();
            _sample.Pause();
        }
    }

    internal void Release()
    {
        _sample.Reset(offered: false);
        _facts.Hide();
        _facts.Content = null;
        _target = null;
        _previewBytes = null;
        ReplacePicture(null, BooruGalleryPicture.None);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopObservingVisibility();
        Release();
    }
}
