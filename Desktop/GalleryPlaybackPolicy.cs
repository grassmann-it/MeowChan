namespace MeowChan.Desktop;

/// <summary>Selection playback policy deliberately has no saved slideshow-mode input.</summary>
internal static class GalleryPlaybackPolicy
{
    internal static bool ShouldPlaySelection(bool videoAutoplay, bool slideshowRunning, bool explicitlyPaused = false)
        => !explicitlyPaused && (videoAutoplay || slideshowRunning);
}

internal readonly record struct VideoVolumeRequest(long Generation, long Revision, int Volume);

/// <summary>
/// Latest accepted volume wins, including across media replacement/config application.
/// Protects native writes and dispatcher readbacks; persistence is notified in UI input order.
/// </summary>
internal sealed class VideoVolumeIntent
{
    private readonly object _gate = new();
    private VideoVolumeRequest _current;
    internal VideoVolumeRequest Request(long generation, int volume)
    {
        lock (_gate) return _current = new(generation, _current.Revision + 1, System.Math.Clamp(volume, 0, 100));
    }
    internal VideoVolumeRequest Current { get { lock (_gate) return _current; } }
    internal bool IsCurrent(VideoVolumeRequest request)
    { lock (_gate) return _current == request; }
}

/// <summary>
/// Native transport commands have an epoch separate from seek/end callback stamps. Seeking
/// can invalidate a terminal observation without cancelling a queued explicit Pause or Play.
/// </summary>
internal sealed class VideoTransportIntent
{
    private readonly object _gate = new();
    private VideoPlaybackIntentStamp _current;
    internal VideoPlaybackIntentStamp Request(long generation, VideoPlaybackMode mode)
    { lock (_gate) return _current = new(generation, _current.Revision + 1, mode); }
    internal bool IsCurrent(VideoPlaybackIntentStamp request)
    { lock (_gate) return _current == request; }
}
