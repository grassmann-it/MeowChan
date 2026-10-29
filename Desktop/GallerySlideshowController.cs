using System;

namespace MeowChan.Desktop;

/// <summary>
/// Serial, caller-driven slideshow state. The viewer owns selection, playback, readiness and Tick;
/// this controller owns no dispatcher, timer, media, persistence or I/O. Generations must increase
/// for every selection/replay. Merely selecting Slideshow never starts it.
/// </summary>
internal sealed class GallerySlideshowController
{
    private readonly Action _requestNext;
    private readonly TimeProvider _timeProvider;
    private GalleryPlaybackMode _mode;
    private TimeSpan _imageDuration = TimeSpan.FromSeconds(5);
    private TimeSpan _remaining = TimeSpan.FromSeconds(5);
    private long? _imageStartedAt;
    private long _generation;
    private bool _hasMedia, _video, _hasNext, _ready, _advanced, _videoEnded, _advanceHeld;

    public GallerySlideshowController(Action requestNext, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(requestNext);
        _requestNext = requestNext;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public bool IsRunning { get; private set; }
    internal bool IsAdvanceHeld => _advanceHeld;
    internal bool IsTerminal => _hasMedia && !_hasNext && (_advanced || _videoEnded);

    /// <summary>Raised synchronously, after IsRunning changes. Callers serialize all entry points.</summary>
    internal event Action? StateChanged;

    public void SetMode(GalleryPlaybackMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (_mode == mode) return;
        _mode = mode;
        if (mode != GalleryPlaybackMode.Slideshow) Stop();
    }

    /// <summary>A changed duration gives the current image a full new period, even while paused.</summary>
    public void SetImageDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (_imageDuration == duration) return;
        _imageDuration = duration;
        _remaining = duration;
        _imageStartedAt = IsRunning && !_advanceHeld && _ready && !_video && !_advanced
            ? _timeProvider.GetTimestamp() : null;
    }

    /// <summary>
    /// A new selection invalidates all readiness and countdown state before it can advance.
    /// Duplicate/older notifications cannot reset a current selection or resurrect an earlier one.
    /// A last/single entry finishes its own display/playback, then stops without navigation.
    /// </summary>
    public void BeginMedia(long generation, bool video, bool hasNext)
    {
        if (_hasMedia && generation <= _generation) return;
        _generation = generation;
        _hasMedia = true;
        _video = video;
        _hasNext = hasNext;
        _ready = _advanced = _videoEnded = _advanceHeld = false;
        _remaining = _imageDuration;
        _imageStartedAt = null;
    }

    /// <summary>Late list readiness may refine availability without rearming the current medium.</summary>
    public void UpdateHasNext(long generation, bool hasNext)
    {
        if (IsCurrent(generation) && !_advanced) _hasNext = hasNext;
    }

    public void ImageReady(long generation)
    {
        if (!IsCurrent(generation) || _video || _ready || _advanced) return;
        _ready = true;
        _remaining = _imageDuration;
        if (IsRunning && !_advanceHeld) _imageStartedAt = _timeProvider.GetTimestamp();
    }

    public void VideoEnded(long generation)
    {
        if (!IsCurrent(generation) || !_video || _videoEnded || _advanced) return;
        // Observe the end even while paused/stopped. Start must not consume a queued old Ended.
        // A replay needs a new BeginMedia generation from the viewer.
        _videoEnded = true;
        if (IsRunning && !_advanceHeld) Advance();
    }

    public void Start()
    {
        if (IsRunning || _mode != GalleryPlaybackMode.Slideshow || !_hasMedia
            || _advanced || _videoEnded) return;
        if (!_advanceHeld && _ready && !_video) _imageStartedAt = _timeProvider.GetTimestamp();
        SetRunning(true);
    }

    /// <summary>Explicitly pauses progression. This controller never controls native playback.</summary>
    public void Pause()
    {
        if (!IsRunning) return;
        if (_ready && !_video) _remaining = RemainingImageTime();
        _imageStartedAt = null;
        SetRunning(false);
    }

    /// <summary>Stops navigation and resets image time. It never rearms an already consumed generation.</summary>
    public void Stop()
    {
        _remaining = _imageDuration;
        _imageStartedAt = null;
        SetRunning(false);
    }

    /// <summary>Temporary UI hold, not Pause. Release is passive until the next safe Tick.</summary>
    public void SetAdvanceHeld(long generation, bool held)
    {
        if (!IsCurrent(generation) || _advanceHeld == held) return;
        if (held)
        {
            if (IsRunning && _ready && !_video) _remaining = RemainingImageTime();
            _imageStartedAt = null;
        }
        _advanceHeld = held;
        if (!held && IsRunning && _ready && !_video && !_advanced)
            _imageStartedAt = _timeProvider.GetTimestamp();
    }

    /// <summary>A seek replaces a deferred natural end, never an already consumed selection.</summary>
    public void SeekStarted(long generation)
    {
        if (!IsCurrent(generation) || !_video || _advanced) return;
        _videoEnded = false;
        SetAdvanceHeld(generation, true);
    }

    public void Tick()
    {
        if (!IsRunning || _advanceHeld || !_hasMedia || _advanced) return;
        if (_video) { if (_videoEnded) Advance(); }
        else if (_ready && RemainingImageTime() == TimeSpan.Zero) Advance();
    }

    private bool IsCurrent(long generation) => _hasMedia && generation == _generation;

    private TimeSpan RemainingImageTime()
    {
        if (_imageStartedAt is not long started) return _remaining;
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        if (elapsed <= TimeSpan.Zero) return _remaining;
        return elapsed >= _remaining ? TimeSpan.Zero : _remaining - elapsed;
    }

    private void Advance()
    {
        if (!IsRunning || _advanceHeld || _advanced) return;
        // Consume BEFORE invoking external navigation. The callback may synchronously call Tick,
        // Ended, Pause/Start, or BeginMedia for the next item; none may double-advance this one.
        _advanced = true;
        _ready = false;
        _imageStartedAt = null;
        if (!_hasNext) { SetRunning(false); return; }
        try { _requestNext(); }
        catch
        {
            Stop();
            throw;
        }
    }

    private void SetRunning(bool running)
    {
        if (IsRunning == running) return;
        IsRunning = running;
        StateChanged?.Invoke();
    }
}
