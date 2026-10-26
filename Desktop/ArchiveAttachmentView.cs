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

// Viewport eviction releases pixels, but never changes the post's reserved preview geometry.
public sealed class ArchiveAttachmentView : UserControl
{
    private sealed class PreviewSlot : Grid
    {
        internal bool Large { get; set; }
        protected override Size MeasureOverride(Size availableSize)
        {
            double maximumWidth = Large ? 360 : 240;
            double width = double.IsFinite(availableSize.Width) ? Math.Clamp(availableSize.Width, 1, maximumWidth) : maximumWidth;
            // Keep room for the post text; full-size media remains available in the gallery.
            Size reserved = new(width, Large ? Math.Min(280, width * 280d / 360) : width * 180d / 240);
            base.MeasureOverride(reserved);
            return reserved;
        }
    }

    private readonly PreviewSlot _slot = new();
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
    private readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly TextBlock _provenance = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsVisible = false };
    private readonly Button _button;
    private readonly ArchiveFavoriteButton _favorite = new() { HorizontalAlignment = HorizontalAlignment.Right };
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
        _slot.Children.Add(_image); _slot.Children.Add(_status); _slot.Children.Add(_play);
        StackPanel content = new() { Spacing = 5 };
        content.Children.Add(_slot); content.Children.Add(_label); content.Children.Add(_provenance);
        _button = new Button { Content = content, Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        _button.Click += (_, _) => (DataContext as ArchiveAttachmentRow)?.OpenPreview?.Invoke();
        StackPanel actions = new() { Spacing = 4 };
        actions.Children.Add(_favorite); actions.Children.Add(_button);
        Content = actions;
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
        _slot.Large = large;
        _slot.InvalidateMeasure();
        _label.MaxWidth = _status.MaxWidth = large ? 360 : 240;
        _button.MaxWidth = large ? 376 : 256;
        _button.HorizontalAlignment = large ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        _label.Text = row?.Attachment.OriginalFileName ?? row?.Attachment.MediaId ?? string.Empty;
        _provenance.Text = MediaProvenanceText.Summary(row?.Binding);
        _provenance.IsVisible = !string.IsNullOrEmpty(_provenance.Text);
        _provenance.MaxWidth = large ? 360 : 240;
        _play.IsVisible = row != null && MeowChan.Gallery.SavedMediaTypes.Classify(row.Attachment.OriginalFileName ?? string.Empty)
            == MeowChan.Gallery.SavedMediaKind.Video;
        _status.Text = Texts.T("archive.loadingThumbnail");
        ToolTip.SetTip(_button, row?.Label ?? string.Empty);
        Avalonia.Automation.AutomationProperties.SetName(_button, row?.Label ?? string.Empty);
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
            { bitmap?.Dispose(); return; }
            UpdateLabel();
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
                _status.Text = exception is OperationCanceledException or TimeoutException ? Texts.T("archive.previewLoadFailed") : exception.Message;
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
        try { (await pending)?.Dispose(); }
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
        _bitmap?.Dispose(); _bitmap = null;
        _status.IsVisible = true;
    }
}
