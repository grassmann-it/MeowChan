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

namespace MeowChan.Desktop;

// Viewport eviction releases pixels, but never changes the post's reserved preview geometry.
public sealed class ArchiveAttachmentView : UserControl
{
    private sealed class PreviewSlot : Grid
    {
        internal bool Large { get; set; }
        protected override Size MeasureOverride(Size availableSize)
        {
            double maximumWidth = Large ? 1040 : 240;
            double width = double.IsFinite(availableSize.Width) ? Math.Clamp(availableSize.Width, 1, maximumWidth) : maximumWidth;
            // Keep room for the post text; full-size media remains available in the gallery.
            Size reserved = new(width, Large ? Math.Min(480, width * 720d / 1040) : width * 180d / 240);
            base.MeasureOverride(reserved);
            return reserved;
        }
    }

    private readonly PreviewSlot _slot = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform, IsVisible = false };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _play = new() { Text = "▶", FontSize = 28, Foreground = Brushes.White,
        Background = Brushes.Black, Padding = new Thickness(8, 0), Margin = new Thickness(8),
        HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, IsVisible = false };
    private readonly TextBlock _label = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12 };
    private readonly Button _button;
    private readonly ArchiveFavoriteButton _favorite = new() { HorizontalAlignment = HorizontalAlignment.Right };
    private CancellationTokenSource? _loading;
    private Bitmap? _bitmap;
    private bool _visible;
    private int _request;
    internal TimeSpan LoadTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public ArchiveAttachmentView()
    {
        _slot.Children.Add(_image); _slot.Children.Add(_status); _slot.Children.Add(_play);
        StackPanel content = new() { Spacing = 5 };
        content.Children.Add(_slot); content.Children.Add(_label);
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
        DetachedFromVisualTree += (_, _) => { _visible = false; Reset(); };
    }

    internal void RefreshFavorite()
    {
        ArchiveAttachmentRow? row = DataContext as ArchiveAttachmentRow;
        _favorite.Configure(row?.FavoriteAvailable, row?.IsFavorite, row?.ToggleFavoriteAsync);
    }

    internal Image Thumbnail => _image;
    internal Button PreviewButton => _button;
    internal string? PreviewStatus => _status.IsVisible ? _status.Text : null;

    private void UpdateLabel()
    {
        ArchiveAttachmentRow? row = DataContext as ArchiveAttachmentRow;
        _favorite.Configure(row?.FavoriteAvailable, row?.IsFavorite, row?.ToggleFavoriteAsync);
        bool large = row?.LargePreview == true;
        _slot.Large = large;
        _slot.InvalidateMeasure();
        _label.MaxWidth = _status.MaxWidth = large ? 1040 : 240;
        _button.MaxWidth = large ? 1056 : 256;
        _button.HorizontalAlignment = large ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
        _label.Text = row?.Label ?? string.Empty;
        _play.IsVisible = row != null && MeowChan.Gallery.SavedMediaTypes.Classify(row.Attachment.OriginalFileName ?? string.Empty)
            == MeowChan.Gallery.SavedMediaKind.Video;
        _status.Text = Texts.T("archive.loadingThumbnail");
        ToolTip.SetTip(_button, _label.Text);
    }

    private void RefreshViewport()
    {
        if (VisualRoot == null || !IsEffectivelyVisible || Bounds.Width <= 0 || Bounds.Height <= 0) return;
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
        if (_loading != null || _bitmap != null || DataContext is not ArchiveAttachmentRow { LoadThumbnail: not null } row) return;
        int request = _request;
        using CancellationTokenSource loading = new();
        _loading = loading;
        Task<Bitmap?>? pending = null;
        try
        {
            pending = row.LoadThumbnail(loading.Token);
            Bitmap? bitmap = await pending.WaitAsync(LoadTimeout, loading.Token);
            if (request != _request || loading.IsCancellationRequested || !_visible)
            { bitmap?.Dispose(); return; }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _image.IsVisible = bitmap != null;
            _status.IsVisible = bitmap == null;
            _status.Text = Texts.T(MeowChan.Gallery.SavedMediaTypes.Classify(row.Attachment.OriginalFileName ?? string.Empty)
                == MeowChan.Gallery.SavedMediaKind.Video ? "reader.video" : "archive.inlineUnsupported");
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested) { if (pending != null) _ = DisposeLateAsync(pending); }
        catch (Exception exception)
        {
            loading.Cancel();
            if (pending != null) _ = DisposeLateAsync(pending);
            if (request != _request || !_visible) return;
            // A cancelled shared media scan is not a pending thumbnail anymore.
            _status.Text = exception is OperationCanceledException or TimeoutException ? Texts.T("archive.previewLoadFailed") : exception.Message;
            _status.IsVisible = true;
        }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; }
    }

    private static async Task DisposeLateAsync(Task<Bitmap?> pending)
    {
        try { (await pending)?.Dispose(); }
        catch (Exception) { } // The visible request already reports the original failure/cancellation.
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel(); _loading = null;
        _image.Source = null; _image.IsVisible = false;
        _bitmap?.Dispose(); _bitmap = null;
        _status.IsVisible = true;
    }
}
