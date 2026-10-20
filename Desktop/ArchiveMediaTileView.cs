using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

public sealed class ArchiveMediaTileView : UserControl
{
    private static readonly SemaphoreSlim ThumbnailGate = new(2, 2);
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11 };
    private Bitmap? _bitmap;
    private CancellationTokenSource? _loading;
    private int _request;
    private bool _visible;

    public ArchiveMediaTileView()
    {
        Grid grid = new(); grid.Children.Add(_image); grid.Children.Add(_message); Content = grid;
        DataContextChanged += (_, _) => { Reset(); if (_visible) _ = LoadAsync(); };
        EffectiveViewportChanged += (_, e) =>
        {
            bool visible = new Rect(Bounds.Size).Intersects(e.EffectiveViewport) && IsEffectivelyVisible;
            if (_visible == visible) return;
            _visible = visible;
            if (visible) _ = LoadAsync(); else Reset();
        };
        DetachedFromVisualTree += (_, _) => { _visible = false; Reset(); };
    }

    private async Task LoadAsync()
    {
        if (DataContext is not ArchiveAssetRow row) return;
        int request = _request;
        using CancellationTokenSource loading = new();
        _loading = loading;
        _message.Text = Texts.T("archive.loadingThumbnail");
        try
        {
            await ThumbnailGate.WaitAsync(loading.Token);
            Bitmap? bitmap;
            try { bitmap = await Task.Run(() => ArchivePage.DecodeThumbnailAsync(row, loading.Token), loading.Token); }
            finally { ThumbnailGate.Release(); }
            if (request != _request || !_visible || loading.IsCancellationRequested) { bitmap?.Dispose(); return; }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _message.Text = bitmap == null ? Texts.T("archive.previewUnavailable") : string.Empty;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (request == _request) _message.Text = exception.Message; }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; }
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel(); _loading = null;
        _image.Source = null; _bitmap?.Dispose(); _bitmap = null;
        _message.Text = string.Empty;
    }
}
