using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using MeowChan.Desktop.Motion;

namespace MeowChan.Desktop.Controls;

/// <summary>Plays the current sidebar picture on the gallery's GIF transport. The host still owns the poster.</summary>
internal sealed class SidebarGifPlayback
{
    private static readonly AttachedProperty<bool> SuspendedProperty =
        AvaloniaProperty.RegisterAttached<SidebarGifPlayback, Control, bool>("Suspended", inherits: true);

    private readonly Image _image;
    private readonly Func<bool>? _isCurrent;
    private readonly LatestUiWork<Func<CancellationToken, Task<IGifFrameDecoder?>>> _loads;
    private Func<CancellationToken, Task<IGifFrameDecoder?>>? _open;
    private Bitmap? _poster;
    private GalleryGifPlayback? _player;
    private Task _retirement = Task.CompletedTask;
    private GifRetirementException? _retirementFailure;
    private Visual[] _visibleTree = [];
    private Window? _window;
    private bool _attached;
    private bool _attempted;

    internal SidebarGifPlayback(Image image, Func<bool>? isCurrent = null)
    {
        _image = image;
        _isCurrent = isCurrent;
        _loads = new(OpenAsync);
        image.AttachedToVisualTree += (_, _) => Attach();
        image.DetachedFromVisualTree += (_, _) => Detach();
    }

    internal static void SetSuspended(Control root, bool value) => root.SetValue(SuspendedProperty, value);

    private bool CanPlay => _attached && _visibleTree.All(visual => visual.IsVisible) && !_image.GetValue(SuspendedProperty)
        && _window is not { WindowState: WindowState.Minimized } && !MotionPolicy.IsOff && _isCurrent?.Invoke() != false;

    internal void Show(Bitmap poster, Func<CancellationToken, Task<IGifFrameDecoder?>> open)
    {
        Clear();
        _poster = poster;
        _open = open;
        Refresh();
    }

    internal void Clear()
    {
        _open = null;
        _loads.Cancel();
        _attempted = false;
        Retire();
        _poster = null;
    }

    internal void Refresh()
    {
        if (!CanPlay)
        {
            if (_player == null && !_loads.IsRunning) return;
            _loads.Cancel();
            Retire();
            _attempted = false;
        }
        else if (!_attempted && _open is { } open && _retirementFailure is null)
        {
            _attempted = true;
            OpenActions.Start(() => _loads.Request(open), "sidebar.gif.open");
        }
    }

    private void Attach()
    {
        _attached = true;
        _window = TopLevel.GetTopLevel(_image) as Window;
        _visibleTree = _image.GetSelfAndVisualAncestors().ToArray();
        foreach (Visual visual in _visibleTree) visual.PropertyChanged += PresentationChanged;
        MotionPolicy.Changed += Refresh;
        Refresh();
    }

    private void Detach()
    {
        _attached = false;
        foreach (Visual visual in _visibleTree) visual.PropertyChanged -= PresentationChanged;
        _visibleTree = [];
        _window = null;
        MotionPolicy.Changed -= Refresh;
        Refresh();
    }

    private void Refresh(object? sender, EventArgs args) => Refresh();
    private void PresentationChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property == Visual.IsVisibleProperty || change.Property == Window.WindowStateProperty
            || ReferenceEquals(sender, _image) && change.Property == SuspendedProperty) Refresh();
    }

    private async Task OpenAsync(Func<CancellationToken, Task<IGifFrameDecoder?>> open, CancellationToken token)
    {
        try
        {
            await _retirement;
            token.ThrowIfCancellationRequested();
            if (_retirementFailure is not null) return;
            IGifFrameDecoder? decoder = await open(token);
            if (decoder is null) return;
            if (decoder.FrameCount < 2 || token.IsCancellationRequested)
            {
                await decoder.DisposeAsync();
                return;
            }
            GalleryGifPlayback player = await GalleryGifPlayback.OpenAsync(decoder, _image, token);
            if (token.IsCancellationRequested || !CanPlay)
            {
                await player.DisposeAsync();
                return;
            }
            _player = player;
            player.Failed += FrameFailed;
            player.Adopt();
            player.SetActive(true);
        }
        catch (GifRetirementException error) { _retirementFailure = error; throw; }
        catch (Exception error) when (error is not (OperationCanceledException or OutOfMemoryException))
        { Serilog.Log.Debug(error, "The GIF of a media sidebar could not animate"); }
    }

    private void FrameFailed(Exception error)
    {
        Serilog.Log.Debug(error, "The GIF of a media sidebar could not animate");
        Retire();
    }

    private void Retire()
    {
        if (_player is not { } player) return;
        _player = null;
        player.Failed -= FrameFailed;
        bool showing = ReferenceEquals(_image.Source, player.Bitmap);
        _retirement = OpenActions.StartObserved(() => RetireAsync(player), "sidebar.gif.retire");
        if (showing) _image.Source = _poster;
    }

    private async Task RetireAsync(GalleryGifPlayback player)
    {
        try { await player.DisposeAsync(); }
        catch (GifRetirementException error) { _retirementFailure = error; throw; }
    }
}
