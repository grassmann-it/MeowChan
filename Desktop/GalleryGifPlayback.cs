using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace MeowChan.Desktop;

internal sealed record GifPlaybackEvidence(int FrameCount, int FrameIndex, long FramesPresented,
    int CompletedPasses, bool IsRunning, bool UserPaused, bool Ended, bool Disposed, string? Failure);

/// <summary>Encoded loop semantics only; independent of slideshow navigation and saved preferences.</summary>
internal sealed class GifPlaybackSequence
{
    internal int FrameCount { get; }
    internal int Repetitions { get; }
    internal int Index { get; private set; }
    internal int CompletedPasses { get; private set; }
    internal bool Ended { get; private set; }
    internal GifPlaybackSequence(int frames, int repetitions)
    {
        if (frames < 1 || repetitions < -1) throw new ArgumentOutOfRangeException(nameof(frames));
        FrameCount = frames; Repetitions = repetitions;
    }
    internal int? Next => Ended || FrameCount == 1 ? null : Index + 1 < FrameCount ? Index + 1
        : Repetitions == -1 || CompletedPasses < Repetitions ? 0 : null;
    internal void Commit(int index)
    {
        if (Next != index) throw new InvalidOperationException("Stale GIF frame sequence.");
        if (index == 0) CompletedPasses = CompletedPasses == int.MaxValue ? int.MaxValue : CompletedPasses + 1;
        Index = index;
    }
    internal void Finish()
    {
        if (Next != null || Ended) return;
        CompletedPasses = CompletedPasses == int.MaxValue ? int.MaxValue : CompletedPasses + 1;
        Ended = true;
    }
    internal void Restart() { Index = CompletedPasses = 0; Ended = false; }
}

/// <summary>
/// One codec operation and one pooled presentation at a time. Only the UI thread writes the
/// Avalonia bitmap, matching the existing software-video surface ownership. Native codec
/// retirement awaits the actual operation, not just cancellation of its waiter.
/// </summary>
internal sealed class GalleryGifPlayback : IAsyncDisposable
{
    private readonly IGifFrameDecoder _decoder;
    private readonly Image _image;
    private readonly WriteableBitmap _bitmap;
    private readonly GifPlaybackSequence _sequence;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DispatcherTimer _timer;
    private readonly TimeProvider _time;
    private Task _advance = Task.CompletedTask;
    private Task? _dispose;
    private TimeSpan _remaining;
    private long _scheduled;
    private bool _active, _userPaused, _disposed, _advancing, _failed, _adopted;
    private string? _failure;
    private long _presented;
    internal event Action? StateChanged;
    internal event Action<Exception>? Failed;
    internal GalleryImageInfo Info => _decoder.Info;
    internal Bitmap Bitmap => _bitmap;
    internal bool UserPaused => _userPaused;
    internal GifPlaybackEvidence Evidence => new(_sequence.FrameCount, _sequence.Index, _presented,
        _sequence.CompletedPasses, Running, _userPaused, _sequence.Ended, _disposed, _failure);
    private bool Running => _adopted && !_disposed && !_failed && _active && !_userPaused && !_sequence.Ended && _sequence.FrameCount > 1;

    private GalleryGifPlayback(IGifFrameDecoder decoder, Image image, TimeProvider? time = null)
    {
        Dispatcher.UIThread.VerifyAccess();
        _decoder = decoder; _image = image; _time = time ?? TimeProvider.System;
        _sequence = new(decoder.FrameCount, decoder.RepetitionCount);
        _bitmap = new(new PixelSize(decoder.Info.Width, decoder.Info.Height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);
        _remaining = TimeSpan.FromMilliseconds(decoder.DurationMs(0));
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += OnTick;
    }

    internal static async Task<GalleryGifPlayback?> OpenAsync(MeowChan.Gallery.GalleryEntry entry,
        Image target, CancellationToken cancellationToken,
        Func<MeowChan.Gallery.GalleryEntry, CancellationToken, Task<IGifFrameDecoder?>>? decoderFactory = null)
    {
        IGifFrameDecoder? decoder = decoderFactory == null
            ? await GifFrameDecoder.OpenAsync(entry, cancellationToken) : await decoderFactory(entry, cancellationToken);
        if (decoder == null) return null; // Misnamed non-GIF retains the existing image decoder.
        GalleryGifPlayback? player = null;
        try
        {
            using GifDecodedFrame first = await decoder.DecodeAsync(0, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Dispatcher.UIThread.VerifyAccess();
            player = new(decoder, target);
            player.CopyPixels(first); // No image/control adoption until the viewer validates its generation.
            return player;
        }
        catch (Exception openError)
        {
            try
            {
                if (player != null) await player.DisposeAsync();
                else await decoder.DisposeAsync();
            }
            catch (Exception cleanup)
            { throw new GifRetirementException((object?)player ?? decoder, new AggregateException(openError, cleanup)); }
            throw; // A decode/open failure with successful retirement is recoverable.
        }
    }

    internal void Adopt()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _adopted) return;
        _adopted = true;
        ShowBitmap();
    }

    internal void SetActive(bool active)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _active == active) return;
        PreserveRemaining(); _active = active;
        Schedule(); StateChanged?.Invoke();
    }
    internal Task ToggleAsync() => _sequence.Ended && !_advancing ? PlayAsync() : SetPausedAsync(!_userPaused);
    private Task SetPausedAsync(bool paused)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _failed || _userPaused == paused) return Task.CompletedTask;
        PreserveRemaining(); _userPaused = paused;
        Schedule(); StateChanged?.Invoke();
        return Task.CompletedTask;
    }
    internal Task PlayAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed || _failed) return Task.CompletedTask;
        if (_sequence.Ended)
        {
            if (_advancing) return _advance;
            PreserveRemaining(); _userPaused = false; _advancing = true;
            return _advance = RestartAsync();
        }
        return SetPausedAsync(false);
    }
    private async Task RestartAsync()
    {
        try
        {
            using GifDecodedFrame frame = await _decoder.DecodeAsync(0, _lifetime.Token);
            if (_disposed || _lifetime.IsCancellationRequested) return;
            _sequence.Restart(); Present(frame);
            _remaining = TimeSpan.FromMilliseconds(_decoder.DurationMs(0));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
        finally { _advancing = false; Schedule(); StateChanged?.Invoke(); }
    }
    private void PreserveRemaining()
    {
        if (_timer.IsEnabled)
        {
            TimeSpan elapsed = _time.GetElapsedTime(_scheduled);
            _remaining = elapsed >= _remaining ? TimeSpan.Zero : _remaining - elapsed;
            _timer.Stop();
        }
    }
    private void Schedule()
    {
        _timer.Stop();
        if (!Running || _advancing) return;
        _scheduled = _time.GetTimestamp();
        _timer.Interval = _remaining > TimeSpan.Zero ? _remaining : TimeSpan.FromMilliseconds(1);
        _timer.Start();
    }
    private void OnTick(object? sender, EventArgs args)
    {
        _timer.Stop(); _remaining = TimeSpan.Zero;
        if (!Running || _advancing) return;
        if (_sequence.Next is not int next)
        { _sequence.Finish(); StateChanged?.Invoke(); return; }
        _advancing = true;
        _advance = AdvanceAsync(next);
    }
    private async Task AdvanceAsync(int next)
    {
        try
        {
            using GifDecodedFrame frame = await _decoder.DecodeAsync(next, _lifetime.Token);
            if (_disposed || _lifetime.IsCancellationRequested || !Running) return;
            _sequence.Commit(next); Present(frame);
            _remaining = TimeSpan.FromMilliseconds(_decoder.DurationMs(next));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error) { Fail(error); }
        finally { _advancing = false; Schedule(); }
    }
    private void Present(GifDecodedFrame frame)
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        CopyPixels(frame);
        ShowBitmap();
    }
    private void CopyPixels(GifDecodedFrame frame)
    {
        using (ILockedFramebuffer destination = _bitmap.Lock())
            for (int row = 0; row < frame.Height; row++)
                Marshal.Copy(frame.Pixels, row * frame.RowBytes,
                    IntPtr.Add(destination.Address, row * destination.RowBytes), checked(frame.Width * 4));
    }
    private void ShowBitmap()
    {
        if (!_adopted || _disposed) return;
        _image.Source = _bitmap;
        _image.InvalidateVisual();
        if (_image.IsEffectivelyVisible && _image.Bounds.Width > 0 && _image.Bounds.Height > 0) ++_presented;
    }
    private void Fail(Exception error)
    {
        _timer.Stop(); _failed = true; _failure = error.Message;
        StateChanged?.Invoke(); Failed?.Invoke(error);
    }
    public ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_dispose != null) return new(_dispose);
        _disposed = true; _active = false; _timer.Stop(); _timer.Tick -= OnTick;
        _lifetime.Cancel();
        if (ReferenceEquals(_image.Source, _bitmap)) _image.Source = null;
        return new(_dispose = DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        Exception? failure = null;
        try { await _advance; } catch (Exception error) { failure = error; }
        try { await _decoder.DisposeAsync(); } catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
        try { _bitmap.Dispose(); } catch (Exception error) { failure = failure == null ? error : new AggregateException(failure, error); }
        _lifetime.Dispose(); StateChanged = null; Failed = null;
        if (failure != null) throw new GifRetirementException(this, failure);
    }
}
