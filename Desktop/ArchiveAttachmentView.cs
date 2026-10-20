using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

// A realized post can still be outside the viewport. Only visible attachments own decoded images.
public sealed class ArchiveAttachmentView : UserControl
{
    private readonly Image _image = new Image { MaxWidth = 240, MaxHeight = 180, Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Left, IsVisible = false };
    private readonly TextBlock _status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 240,
        FontSize = 12, MinHeight = 44 };
    private readonly TextBlock _label = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis,
        MaxWidth = 240, FontSize = 12 };
    private readonly Button _button;
    private CancellationTokenSource? _loading;
    private Bitmap? _bitmap;
    private bool _visible;
    private int _request;

    public ArchiveAttachmentView()
    {
        StackPanel content = new StackPanel { Spacing = 5 };
        content.Children.Add(_image);
        content.Children.Add(_status);
        content.Children.Add(_label);
        _button = new Button { Content = content, Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Left };
        _button.Click += (_, _) => (DataContext as ArchiveAttachmentRow)?.OpenPreview?.Invoke();
        Content = _button;
        DataContextChanged += (_, _) => { Reset(); UpdateLabel(); if (_visible) _ = LoadAsync(); };
        EffectiveViewportChanged += OnViewportChanged;
        DetachedFromVisualTree += (_, _) => { _visible = false; Reset(); };
    }

    internal Image Thumbnail => _image;
    internal Button PreviewButton => _button;

    private void UpdateLabel()
    {
        _label.Text = (DataContext as ArchiveAttachmentRow)?.Label ?? string.Empty;
        _status.Text = Texts.T("archive.loadingThumbnail");
        ToolTip.SetTip(_button, _label.Text);
    }

    private void OnViewportChanged(object? sender, EffectiveViewportChangedEventArgs e)
    {
        bool visible = new Rect(Bounds.Size).Intersects(e.EffectiveViewport) && IsEffectivelyVisible;
        if (_visible == visible) return;
        _visible = visible;
        if (visible) { UpdateLabel(); _ = LoadAsync(); }
        else Reset();
    }

    private async Task LoadAsync()
    {
        if (DataContext is not ArchiveAttachmentRow { LoadThumbnail: not null } row) return;
        int request = _request;
        using CancellationTokenSource loading = new CancellationTokenSource();
        _loading = loading;
        try
        {
            Bitmap? bitmap = await row.LoadThumbnail(loading.Token);
            if (request != _request || loading.IsCancellationRequested || !_visible)
            { bitmap?.Dispose(); return; }
            _bitmap = bitmap;
            _image.Source = bitmap;
            _image.IsVisible = bitmap != null;
            _status.IsVisible = bitmap == null;
            _status.Text = Texts.T("archive.inlineUnsupported");
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (request != _request || !_visible) return;
            _status.Text = exception.Message;
            _status.IsVisible = true;
        }
        finally { if (ReferenceEquals(_loading, loading)) _loading = null; }
    }

    private void Reset()
    {
        ++_request;
        _loading?.Cancel();
        _loading = null;
        _image.Source = null;
        _image.IsVisible = false;
        _bitmap?.Dispose();
        _bitmap = null;
        _status.IsVisible = true;
    }
}
