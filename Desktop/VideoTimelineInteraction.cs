using System;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

internal readonly record struct VideoSeekRequest(long Generation, long Revision, long Position, long Length = 0);

/// <summary>
/// Separates native observations from a gesture's requested position. Neither hover nor focus
/// owns the clock. Revisions reject dispatcher observations/acknowledgements predating a seek.
/// All state is protected because native callbacks take stamps off the UI thread.
/// </summary>
internal sealed class VideoTimelineInteraction
{
    private readonly object _gate = new();
    private long _generation, _revision, _observed, _requested;
    private bool _pointer, _keyboard, _pending;
    internal event Action? SeekInteractionStarted;
    internal bool HasPendingSeek { get { lock (_gate) return _pending; } }
    internal long Revision { get { lock (_gate) return _revision; } }
    internal bool HasGesture { get { lock (_gate) return _pointer || _keyboard; } }
    internal long DisplayPosition { get { lock (_gate) return _pending || _pointer || _keyboard ? _requested : _observed; } }

    internal void Reset(long generation)
    {
        lock (_gate)
        {
            _generation = generation; ++_revision; _observed = _requested = 0;
            _pointer = _keyboard = _pending = false;
        }
    }
    internal void BeginGesture(bool pointer)
    {
        bool started;
        lock (_gate)
        {
            started = !_pointer && !_keyboard;
            if (!_pending && started) _requested = _observed;
            if (pointer) _pointer = true; else _keyboard = true;
        }
        if (started) SeekInteractionStarted?.Invoke();
    }
    internal void EndGesture(bool pointer)
    { lock (_gate) { if (pointer) _pointer = false; else _keyboard = false; } }
    // Cancel invalidates queued seeks, not a native setter that has already entered. Its later
    // observations may still report the real position, but cannot control a successor medium.
    internal void Cancel()
    { lock (_gate) { ++_revision; _pointer = _keyboard = _pending = false; } }
    internal VideoSeekRequest? Request(long generation, double normalizedPosition, long length, bool seekable)
    {
        lock (_gate)
        {
            if (generation != _generation || !seekable || length <= 0 || !double.IsFinite(normalizedPosition)) return null;
            _requested = (long)(Math.Clamp(normalizedPosition, 0, 1000) / 1000 * length);
            _pending = true;
            return new VideoSeekRequest(generation, ++_revision, _requested, length);
        }
    }
    internal bool IsLatest(VideoSeekRequest request)
    { lock (_gate) return request.Generation == _generation && request.Revision == _revision && _pending; }
    internal bool Complete(VideoSeekRequest request, long actualPosition)
    {
        lock (_gate)
        {
            if (!IsLatest(request)) return false;
            _pending = false; _observed = Math.Max(0, actualPosition);
            ++_revision; // Invalidate time notifications queued before this backend readback.
            return true;
        }
    }
    internal bool Observe(long generation, long revision, long position)
    {
        lock (_gate)
        {
            if (_generation != generation || revision != _revision || _pending) return false;
            _observed = Math.Max(0, position);
            return true;
        }
    }
}

/// <summary>One executing seek and one replaceable pending target, never one task per pixel.</summary>
internal sealed class VideoLatestSeekQueue
{
    private readonly object _gate = new();
    private readonly Func<VideoSeekRequest, Task> _execute;
    private VideoSeekRequest? _pending;
    private Task? _drain;
    internal VideoLatestSeekQueue(Func<VideoSeekRequest, Task> execute) => _execute = execute;
    internal Task Enqueue(VideoSeekRequest request)
    {
        lock (_gate)
        {
            _pending = request;
            return _drain ??= Task.Run(DrainAsync);
        }
    }
    private async Task DrainAsync()
    {
        while (true)
        {
            VideoSeekRequest next;
            lock (_gate)
            {
                if (_pending is not { } pending) { _drain = null; return; }
                next = pending; _pending = null;
            }
            // The adapter observes failures and performs the existing playback failure cleanup.
            // Reset admission on unexpected failure too, so subsequent work cannot get stranded.
            try { await _execute(next).ConfigureAwait(false); }
            catch { lock (_gate) { _pending = null; _drain = null; } throw; }
        }
    }
}

internal enum VideoPlaybackMode { Playing, Paused, Stopped }
internal readonly record struct VideoPlaybackIntentStamp(long Generation, long Revision, VideoPlaybackMode Mode);

/// <summary>
/// Active loop preference is independent of media retirement. The viewer supplies persisted
/// preference values; this type never saves settings or requests navigation/playback itself.
/// Explicit transport intent has its own epoch, invalidating terminal/repeat callbacks.
/// </summary>
internal sealed class VideoPlaybackIntent
{
    private readonly object _gate = new();
    private VideoPlaybackIntentStamp _current = new(0, 0, VideoPlaybackMode.Stopped);
    private bool _loop, _endConsumed, _playStarted, _notifyingUserRequest;
    internal event Action<bool>? LoopChanged;
    internal event Action<bool>? UserPlaybackRequested;
    internal event Action? PlaybackEnded;
    internal bool LoopEnabled { get { lock (_gate) return _loop; } }
    internal bool SetLoopEnabled(bool enabled, bool notify = false)
    {
        lock (_gate)
        {
            if (_loop == enabled) return false;
            _loop = enabled;
        }
        if (notify) LoopChanged?.Invoke(enabled);
        return true;
    }
    internal void ToggleLoop() => SetLoopEnabled(!LoopEnabled, notify: true);
    internal VideoPlaybackIntentStamp Current { get { lock (_gate) return _current; } }
    private VideoPlaybackIntentStamp SetLocked(long generation, VideoPlaybackMode mode)
    {
        if (_current.Generation != generation || _current.Mode != VideoPlaybackMode.Playing
            || mode != VideoPlaybackMode.Playing || _endConsumed) _playStarted = false;
        _endConsumed = false;
        return _current = new(generation, _current.Revision + 1, mode);
    }
    // Lifecycle, autoplay and seeking are deliberately silent.
    internal VideoPlaybackIntentStamp Set(long generation, VideoPlaybackMode mode)
    { lock (_gate) return SetLocked(generation, mode); }
    internal VideoPlaybackIntentStamp? RequestUserPlayback(long generation, VideoPlaybackMode mode)
    {
        VideoPlaybackIntentStamp request;
        lock (_gate)
        {
            // Events are observations, not command-routing hooks. In particular, a viewer
            // reflecting a start/pause event must not recursively enqueue another command.
            if (_notifyingUserRequest) return null;
            request = SetLocked(generation, mode);
            // Repeated Play on an already running input need not emit another native Playing.
            // SetLocked still requires a fresh start after pause, stop, end or media replacement.
            _notifyingUserRequest = true;
        }
        try { UserPlaybackRequested?.Invoke(mode == VideoPlaybackMode.Playing); }
        finally { lock (_gate) _notifyingUserRequest = false; }
        return request;
    }
    internal bool IsCurrent(VideoPlaybackIntentStamp stamp)
    { lock (_gate) return _current == stamp; }
    internal bool CanRepeat(VideoPlaybackIntentStamp stamp)
    { lock (_gate) return _loop && stamp.Mode == VideoPlaybackMode.Playing && _current == stamp; }
    internal bool MarkPlaying(VideoPlaybackIntentStamp stamp)
    {
        lock (_gate)
        {
            if (_current != stamp || stamp.Mode != VideoPlaybackMode.Playing) return false;
            _playStarted = true;
            return true;
        }
    }
    internal bool TryAcceptEnd(VideoPlaybackIntentStamp stamp)
    {
        lock (_gate)
        {
            if (_current != stamp || stamp.Mode != VideoPlaybackMode.Playing || !_playStarted || _endConsumed) return false;
            _endConsumed = true;
            return true;
        }
    }
    internal bool FinishNaturalEnd(VideoPlaybackIntentStamp stamp)
    {
        lock (_gate)
        {
            if (_current != stamp || stamp.Mode != VideoPlaybackMode.Playing || _loop || !_endConsumed) return false;
            SetLocked(stamp.Generation, VideoPlaybackMode.Paused);
        }
        PlaybackEnded?.Invoke();
        return true;
    }
    // Re-arm only after repeat's native Stop has returned. Stop callbacks keep the consumed
    // old epoch; another natural end after the new Play has its own exactly-once claim.
    internal VideoPlaybackIntentStamp? BeginRepeat(VideoPlaybackIntentStamp stamp)
    {
        lock (_gate)
        {
            if (!CanRepeat(stamp) || !_endConsumed) return null;
            return SetLocked(stamp.Generation, VideoPlaybackMode.Playing);
        }
    }
}
