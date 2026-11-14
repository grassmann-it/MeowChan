using System;

namespace MeowChan.Desktop;

/// <summary>
/// Serial, caller-driven slideshow state. The viewer owns selection, playback, readiness and Tick;
/// this controller owns no dispatcher, timer, media, persistence or I/O. Generations must increase
/// for every selection/replay. Merely selecting Slideshow never starts it.
/// A video follows the <see cref="SlideshowVideoRule"/>: after each natural end of a pass the rule either advances or asks the
/// viewer to replay the same video (<c>requestReplay</c>). Played time is the sum of the time the slideshow ran, unheld, while
/// the ready video was shown; the decision is taken only at the end of a pass, so the pass that crosses the limit plays out.
/// </summary>
internal sealed class GallerySlideshowController
{
    private readonly Action _requestNext;
    private readonly Action? _requestReplay;
    private readonly TimeProvider _timeProvider;
    private SlideshowVideoRule _videoRule;
    private TimeSpan _videoAtLeast = TimeSpan.FromSeconds(Config.GallerySlideshowVideoSecondsDefault);
    private int _videoRepeats = Config.GallerySlideshowVideoRepeatsDefault;
    private int _videoPasses;
    private TimeSpan _videoPlayed;
    private long? _videoStartedAt;
    private GalleryPlaybackMode _mode;
    private TimeSpan _imageDuration = TimeSpan.FromSeconds(5);
    private TimeSpan _remaining = TimeSpan.FromSeconds(5);
    private long? _imageStartedAt;
    private long _generation;
    private bool _hasMedia, _video, _hasNext, _ready, _advanced, _videoEnded, _advanceHeld;

    public GallerySlideshowController(Action requestNext, TimeProvider? timeProvider = null, Action? requestReplay = null)
    {
        ArgumentNullException.ThrowIfNull(requestNext);
        _requestNext = requestNext;
        _requestReplay = requestReplay;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Completed natural passes of the current video (diagnostics and tests).</summary>
    internal int VideoPasses => _videoPasses;
    /// <summary>Counted play time of the current video (diagnostics and tests).</summary>
    internal TimeSpan VideoPlayed => _videoPlayed + RunningVideoTime();

    /// <summary>
    /// The rule for videos reached by a running slideshow. A change applies from the next pass end of the current video;
    /// its counters are kept. Without a replay callback every rule degrades to <see cref="SlideshowVideoRule.PlayOnce"/>.
    /// </summary>
    public void SetVideoRule(SlideshowVideoRule rule, TimeSpan atLeast, int repeats)
    {
        if (!Enum.IsDefined(rule)) throw new ArgumentOutOfRangeException(nameof(rule));
        if (atLeast <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(atLeast));
        if (repeats < 1) throw new ArgumentOutOfRangeException(nameof(repeats));
        _videoRule = rule;
        _videoAtLeast = atLeast;
        _videoRepeats = repeats;
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
        _videoPasses = 0;
        _videoPlayed = TimeSpan.Zero;
        _videoStartedAt = null;
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

    /// <summary>The current video is shown and can play: from now on running, unheld time counts as played time.</summary>
    public void VideoReady(long generation)
    {
        if (!IsCurrent(generation) || !_video || _ready || _advanced) return;
        _ready = true;
        ResumeVideoClock();
    }

    /// <summary>
    /// A natural end of one pass of the current video. While the slideshow runs, the video rule decides between advancing
    /// and a replay of the same video; a replay keeps the generation and the counters. Seeks never count as an end.
    /// </summary>
    public void VideoEnded(long generation)
    {
        if (!IsCurrent(generation) || !_video || _videoEnded || _advanced) return;
        PauseVideoClock();
        _videoPasses++;
        if (IsRunning && WantsAnotherPass())
        {
            ResumeVideoClock();
            _requestReplay!();
            return;
        }
        // Observe the end even while paused/stopped. Start must not consume a queued old Ended.
        // A replay needs a new BeginMedia generation from the viewer.
        _videoEnded = true;
        if (IsRunning && !_advanceHeld) Advance();
    }

    private bool WantsAnotherPass() => _requestReplay != null && _videoRule switch
    {
        SlideshowVideoRule.LoopAtLeastSeconds => _videoPlayed < _videoAtLeast,
        SlideshowVideoRule.RepeatTimes => _videoPasses < _videoRepeats,
        _ => false
    };

    private TimeSpan RunningVideoTime()
    {
        if (_videoStartedAt is not long started) return TimeSpan.Zero;
        TimeSpan elapsed = _timeProvider.GetElapsedTime(started);
        return elapsed > TimeSpan.Zero ? elapsed : TimeSpan.Zero;
    }

    private void PauseVideoClock()
    {
        _videoPlayed += RunningVideoTime();
        _videoStartedAt = null;
    }

    private void ResumeVideoClock()
    {
        if (_video && _ready && IsRunning && !_advanceHeld && !_advanced && !_videoEnded && _videoStartedAt == null)
            _videoStartedAt = _timeProvider.GetTimestamp();
    }

    public void Start()
    {
        if (IsRunning || _mode != GalleryPlaybackMode.Slideshow || !_hasMedia
            || _advanced || _videoEnded) return;
        if (!_advanceHeld && _ready && !_video) _imageStartedAt = _timeProvider.GetTimestamp();
        SetRunning(true);
        ResumeVideoClock();
    }

    /// <summary>Explicitly pauses progression. This controller never controls native playback.</summary>
    public void Pause()
    {
        if (!IsRunning) return;
        if (_ready && !_video) _remaining = RemainingImageTime();
        _imageStartedAt = null;
        PauseVideoClock();
        SetRunning(false);
    }

    /// <summary>Stops navigation and resets image time. It never rearms an already consumed generation.</summary>
    public void Stop()
    {
        _remaining = _imageDuration;
        _imageStartedAt = null;
        PauseVideoClock();
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
            PauseVideoClock();
        }
        _advanceHeld = held;
        if (!held && IsRunning && _ready && !_video && !_advanced)
            _imageStartedAt = _timeProvider.GetTimestamp();
        if (!held) ResumeVideoClock();
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
        PauseVideoClock();
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
