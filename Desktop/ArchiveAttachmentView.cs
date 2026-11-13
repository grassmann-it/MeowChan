using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

// Viewport eviction releases pixels, but never changes the post's preview geometry: the picture's aspect stays on the row.
public sealed class ArchiveAttachmentView : UserControl
{
    /// <summary>
    /// File line, frame and provenance as one block that is as wide as the drawn picture (the file line keeps a modest minimum). The
    /// frame is measured first; the lines above and below take its width and wrap or trim to it instead of widening the block, so the
    /// text beside a portrait picture starts right after the picture and the star sits at the picture's right edge.
    /// </summary>
    private sealed class AttachmentColumn : Panel
    {
        private const double Spacing = 4;
        internal Control Lead = null!, Frame = null!, Trail = null!;

        protected override Size MeasureOverride(Size availableSize)
        {
            Frame.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            double width = MediaSlotGeometry.BlockWidth(Frame.DesiredSize.Width, availableSize.Width);
            Size line = new(width, double.PositiveInfinity);
            Lead.Measure(line); Trail.Measure(line);
            double height = Lead.DesiredSize.Height + Spacing + Frame.DesiredSize.Height;
            if (Trail.IsVisible) height += Spacing + Trail.DesiredSize.Height;
            return new Size(width, height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double width = Math.Min(finalSize.Width, DesiredSize.Width), y = 0;
            Lead.Arrange(new Rect(0, y, width, Lead.DesiredSize.Height)); y += Lead.DesiredSize.Height + Spacing;
            Frame.Arrange(new Rect(0, y, Math.Min(width, Frame.DesiredSize.Width), Frame.DesiredSize.Height)); y += Frame.DesiredSize.Height + Spacing;
            if (Trail.IsVisible) Trail.Arrange(new Rect(0, y, width, Trail.DesiredSize.Height));
            return finalSize;
        }
    }

    private readonly MediaFramePanel _slot = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsVisible = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Border _play = new()
    {
        Name = "VideoPlayOverlay", Width = 44, Height = 44, CornerRadius = new CornerRadius(22),
        Background = new SolidColorBrush(Color.FromArgb(205, 13, 16, 22)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)), BorderThickness = new Thickness(1),
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false, IsVisible = false,
        Child = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M 0,0 L 0,18 L 14,9 Z"), Fill = Brushes.White,
            Width = 14, Height = 18, Stretch = Stretch.Uniform, Margin = new Thickness(3, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        }
    };
    // Imageboard file line above the preview: "name.ext (size)" when the capture knows the size.
    // It takes the block's width and wraps to two lines, then trims: a long name never widens the block beyond the picture.
    private readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, MaxLines = 2, FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) };
    // A quiet slot surface only while no thumbnail is shown (loading, missing, unsupported).
    private readonly Border _placeholder = new() { CornerRadius = new CornerRadius(5), IsHitTestVisible = false };
    private readonly TextBlock _provenance = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly Button _button;
    private readonly ArchiveFavoriteButton _favorite = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>
    /// Inherited switch set by an owner whose content is covered (for example by the gallery overlay): attachments in
    /// its subtree start no new thumbnail loads while it is true. Waiting work is canceled cooperatively;
    /// an executing native decoder retains ownership until it really returns. Resuming loads visible empty slots once.
    /// </summary>
    public static readonly AttachedProperty<bool> LoadingSuspendedProperty =
        AvaloniaProperty.RegisterAttached<ArchiveAttachmentView, Control, bool>("LoadingSuspended", false, inherits: true);
    public static bool GetLoadingSuspended(Control control) => control.GetValue(LoadingSuspendedProperty);
    public static void SetLoadingSuspended(Control control, bool value) => control.SetValue(LoadingSuspendedProperty, value);
    internal static long LoadsSkippedWhileSuspended;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == LoadingSuspendedProperty)
        {
            if (change.NewValue is true) CancelPendingLoad();
            else if (_visible) _ = LoadAsync();
        }
        if (change.Property == IsVisibleProperty && change.NewValue is false) SetViewportVisible(false);
    }

    private CancellationTokenSource? _loading;
    private bool _reloadAfterDrain;
    private Bitmap? _bitmap;
    private bool _visible;
    private bool _wasEffectivelyVisible;
    private int _request;
    internal TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public ArchiveAttachmentView()
    {
        _placeholder[!Border.BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("Workspace.Raised");
        _placeholder.Bind(IsVisibleProperty, _status.GetObservable(IsVisibleProperty));
        // Flat: no raised box around the thumbnail. Semi's hover/pressed tint and the shared focus adorner remain the interaction
        // feedback of the native button; the button is the frame, so both run around the drawn picture and nothing else.
        Grid surface = new();
        surface.Children.Add(_placeholder); surface.Children.Add(_image); surface.Children.Add(_status);
        _button = new Button { Content = surface, Padding = new Thickness(0), Background = Brushes.Transparent,
            BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
        _button.Click += (_, _) => (DataContext as ArchiveAttachmentRow)?.OpenPreview?.Invoke();
        // The play mark is not part of the click target; it is arranged on the same rectangle as the picture, so it is centred on it.
        _slot.Children.Add(_button); _slot.Children.Add(_play);
        // The file line and the star share one row above the thumbnail (the star used to take a row of its own and
        // pushed the picture down); the thumbnail below it is the click target for the preview. The row is as wide as the picture.
        Grid fileLine = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), MinHeight = 0 };
        Grid.SetColumn(_favorite, 1);
        fileLine.Children.Add(_label); fileLine.Children.Add(_favorite);
        Content = new AttachmentColumn { Lead = fileLine, Frame = _slot, Trail = _provenance,
            Children = { fileLine, _slot, _provenance } };
        DataContextChanged += (_, _) => { Reset(); UpdateLabel(); if (_visible) _ = LoadAsync(); };
        EffectiveViewportChanged += (_, e) => SetViewportVisible(new Rect(Bounds.Size).Intersects(e.EffectiveViewport) && IsEffectivelyVisible);
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(RefreshViewport, DispatcherPriority.Loaded);
        SizeChanged += (_, _) => RefreshViewport();
        // Ancestor visibility can change without resizing this reserved slot.
        LayoutUpdated += (_, _) =>
        {
            bool visible = VisualRoot != null && IsEffectivelyVisible;
            if (_wasEffectivelyVisible == visible) return;
            _wasEffectivelyVisible = visible;
            RefreshViewport();
        };
        DetachedFromVisualTree += (_, _) => { _visible = false; Reset(); };
    }

    internal void RefreshFavorite()
    {
        ArchiveAttachmentRow? row = DataContext as ArchiveAttachmentRow;
        _favorite.Configure(row?.FavoriteAvailable, row?.IsFavorite, row?.ToggleFavoriteAsync, row?.FavoriteError);
    }

    internal Image Thumbnail => _image;
    internal Button PreviewButton => _button;
    internal string? PreviewStatus => _status.IsVisible ? _status.Text : null;

    private void UpdateLabel()
    {
        ArchiveAttachmentRow? row = DataContext as ArchiveAttachmentRow;
        _favorite.Configure(row?.FavoriteAvailable, row?.IsFavorite, row?.ToggleFavoriteAsync, row?.FavoriteError);
        bool large = row?.LargePreview == true;
        SetFrame(large, row?.KnownAspect);
        _label.Text = row?.FileLine ?? string.Empty;
        _provenance.Text = MediaProvenanceText.Summary(row?.Binding);
        _provenance.IsVisible = !string.IsNullOrEmpty(_provenance.Text);
        _play.IsVisible = row != null && MeowChan.Gallery.SavedMediaTypes.Classify(row.Attachment.OriginalFileName ?? string.Empty)
            == MeowChan.Gallery.SavedMediaKind.Video;
        _status.Text = Texts.T("archive.loadingThumbnail");
        ToolTip.SetTip(_button, row?.Label ?? string.Empty);
        Avalonia.Automation.AutomationProperties.SetName(_button, row?.Label ?? string.Empty);
    }

    // The frame follows the drawn picture: the wide-screen reservation until one is known, then its aspect. The row remembers it, so a
    // preview that was scrolled out (pixels released) or a recycled container keeps the geometry and the list does not jump again.
    private void SetFrame(bool large, double? aspect)
    {
        if (_slot.Large == large && _slot.Aspect == aspect) return;
        _slot.Large = large; _slot.Aspect = aspect;
        _slot.InvalidateMeasure();
    }

    private void RefreshViewport()
    {
        if (VisualRoot == null || !IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0)
        { SetViewportVisible(false); return; }
        ScrollViewer? scroll = this.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
        Point? position = scroll == null ? null : this.TranslatePoint(default, scroll);
        if (scroll != null && position != null)
            SetViewportVisible(new Rect(position.Value, Bounds.Size).Intersects(new Rect(scroll.Bounds.Size)));
    }

    private void SetViewportVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (visible) { UpdateLabel(); _ = LoadAsync(); }
        else Reset();
    }

    private async Task LoadAsync()
    {
        if (!_visible || !IsEffectivelyVisible || _bitmap != null || DataContext is not ArchiveAttachmentRow { LoadThumbnail: not null } row) return;
        if (GetLoadingSuspended(this))
        {
            Interlocked.Increment(ref LoadsSkippedWhileSuspended);
            PerformanceDiagnostics.Record("gallery.thumbnail", "suppressed");
            return;
        }
        if (_loading != null)
        {
            // The old data context/viewport can have canceled a native operation that has not
            // returned yet. Remember one demand; do not relinquish its ownership early.
            if (_loading.IsCancellationRequested) _reloadAfterDrain = true;
            return;
        }
        _reloadAfterDrain = false;
        int request = _request;
        using CancellationTokenSource loading = new();
        _loading = loading;
        Task<Bitmap?>? pending = null;
        long loadId = PerformanceDiagnostics.NextId();
        using var measurement = PerformanceDiagnostics.Measure("gallery.thumbnail.load", loadId);
        try
        {
            pending = row.LoadThumbnail(loading.Token);
            Bitmap? bitmap = await pending.WaitAsync(LoadTimeout, loading.Token);
            if (request != _request || loading.IsCancellationRequested || !_visible)
            { if (bitmap != null) MediaTileThumbnails.Release(bitmap); return; }
            UpdateLabel();
            if (MediaSlotGeometry.AspectOf(bitmap) is { } aspect) { row.KnownAspect = aspect; SetFrame(row.LargePreview, aspect); }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _image.IsVisible = bitmap != null;
            _status.IsVisible = bitmap == null;
            _status.Text = Texts.T(MeowChan.Gallery.SavedMediaTypes.Classify(row.Attachment.OriginalFileName ?? string.Empty)
                == MeowChan.Gallery.SavedMediaKind.Video ? "reader.video" : "archive.inlineUnsupported");
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
            PerformanceDiagnostics.Record("gallery.thumbnail", "canceled", loadId);
            if (pending != null) await DisposeLateAsync(pending);
        }
        catch (Exception exception)
        {
            loading.Cancel();
            if (request == _request && _visible)
            {
                UpdateLabel();
                // Report the timeout now; draining a noncancelable codec can take longer.
                _status.Text = exception is OperationCanceledException or TimeoutException
                    ? Texts.T("archive.previewLoadFailed")
                    : Texts.T("archive.previewLoadFailedDetail", exception.Message);
                _status.IsVisible = true;
            }
            if (pending != null) await DisposeLateAsync(pending);
        }
        finally
        {
            if (ReferenceEquals(_loading, loading)) _loading = null;
            if (_reloadAfterDrain && _visible && !GetLoadingSuspended(this)) _ = LoadAsync();
        }
    }

    private static async Task DisposeLateAsync(Task<Bitmap?> pending)
    {
        try { if (await pending is { } late) MediaTileThumbnails.Release(late); }
        catch (Exception) { } // The visible request already reports the original failure/cancellation.
    }

    private void CancelPendingLoad()
    {
        if (_loading == null || _loading.IsCancellationRequested) return;
        ++_request;
        _reloadAfterDrain = true;
        _loading.Cancel();
    }

    private void Reset()
    {
        ++_request;
        CancelPendingLoad();
        _image.Source = null; _image.IsVisible = false;
        if (_bitmap != null) MediaTileThumbnails.Release(_bitmap); _bitmap = null;
        _status.IsVisible = true;
    }
}
