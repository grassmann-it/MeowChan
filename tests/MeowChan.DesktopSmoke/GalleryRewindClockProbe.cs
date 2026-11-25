using MeowChan.Desktop;

namespace MeowChan.DesktopSmoke;

internal enum GalleryRewindClockResult
{
    WaitingForRewind,
    WaitingForProgress,
    Advanced,
    PlaybackChanged,
    MediaChanged,
}

internal sealed class GalleryRewindClockProbe(long generation, long beforeMs, long targetMs)
{
    internal const int ClockAdvanceMs = 200;
    internal const int SeekToleranceMs = 900;
    private long? _rewoundMs;

    internal GalleryRewindClockResult Observe(long? sampleGeneration, string state, long timeMs)
    {
        if (sampleGeneration != generation || state == "Retired")
            return GalleryRewindClockResult.MediaChanged;
        if (state != "Playing")
            return GalleryRewindClockResult.PlaybackChanged;

        // A clock on the old timeline can advance while the seek is still queued.
        // Anchor only once the native clock has moved backward to the seek target.
        if (_rewoundMs is not { } rewoundMs)
        {
            if (timeMs < 0 || timeMs >= beforeMs || Math.Abs(timeMs - targetMs) >= SeekToleranceMs)
                return GalleryRewindClockResult.WaitingForRewind;
            _rewoundMs = timeMs;
            return GalleryRewindClockResult.WaitingForProgress;
        }

        return timeMs >= rewoundMs + ClockAdvanceMs
            ? GalleryRewindClockResult.Advanced : GalleryRewindClockResult.WaitingForProgress;
    }

    internal static bool HasRealFrames(GalleryVideoPlayer.PlaybackEvidence evidence, bool windowsNativeOutput) =>
        evidence.HasPlayback && evidence.State == "Playing" && evidence.Time > 100 && evidence.Seekable
        && evidence.Length >= 8000 && (windowsNativeOutput
            ? evidence.DisplayedFrames >= 3 : evidence.SurfaceFrames >= 3 && evidence.SurfaceNonUniform);
}
