using System;
using System.Threading;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.VisualTree;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

public sealed class ArchiveMediaTileView : UserControl
{
    private static readonly SemaphoreSlim ThumbnailGate = new(2, 2);
    private readonly ArchiveFavoriteButton _favorite = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
    private readonly Image _image = new() { Stretch = Stretch.Uniform, MaxWidth = 170, MaxHeight = 125 };
    private readonly TextBlock _title = new() { MaxLines = 1, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _status = new() { MaxLines = 2, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
    private readonly TextBlock _message = new() { MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private Bitmap? _bitmap;
    private CancellationTokenSource? _loading;
    private int _request;
    private bool _visible;
    private Rect _viewport;
    private readonly List<Visual> _visibilityAncestors = new();
    internal Func<ArchiveAssetRow, CancellationToken, Task<Bitmap?>> ThumbnailDecoder { get; set; }
        = static (row, token) => ArchivePage.DecodeThumbnailAsync(row, token);

    public ArchiveMediaTileView()
    {
        MaxWidth = 190; Height = 210;
        Grid preview = new() { Height = 125, MaxWidth = 170 };
        preview.Children.Add(_image); preview.Children.Add(_message); preview.Children.Add(_favorite);
        Grid grid = new() { Margin = new Thickness(8), RowDefinitions = new RowDefinitions("125,Auto,*"), RowSpacing = 5 };
        grid.Children.Add(preview); grid.Children.Add(_title); Grid.SetRow(_title, 1);
        grid.Children.Add(_status); Grid.SetRow(_status, 2); Content = grid;
        DataContextChanged += (_, _) =>
        {
            Reset(); RefreshFavorite();
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
    }

    private async Task LoadAsync()
    {
        if (DataContext is not ArchiveAssetRow row || _loading != null || _bitmap != null || !IsEffectivelyVisible) return;
        int request = _request;
        using CancellationTokenSource loading = new();
        _loading = loading;
        _message.Text = Texts.T("archive.loadingThumbnail");
        UpdateDescription();
        try
        {
            await ThumbnailGate.WaitAsync(loading.Token);
            Bitmap? bitmap;
            Func<ArchiveAssetRow, CancellationToken, Task<Bitmap?>> decode = ThumbnailDecoder;
            try { bitmap = await Task.Run(() => decode(row, loading.Token), loading.Token); }
            finally { ThumbnailGate.Release(); }
            if (request != _request || !_visible || !IsEffectivelyVisible || !ReferenceEquals(DataContext, row) || loading.IsCancellationRequested) { bitmap?.Dispose(); return; }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _message.Text = bitmap == null ? Texts.T("archive.previewUnavailable") : string.Empty;
            UpdateDescription();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (request == _request) { _message.Text = exception.Message; UpdateDescription(); } }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; }
    }

    private void UpdateDescription()
    {
        string description = string.Join(Environment.NewLine, new[] { _title.Text, _status.Text, _message.Text });
        ToolTip.SetTip(this, description);
        AutomationProperties.SetName(this, description);
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel(); _loading = null;
        _image.Source = null; _bitmap?.Dispose(); _bitmap = null;
        _message.Text = string.Empty;
        UpdateDescription();
    }
}
