using System;
using System.Collections.Generic;
using System.Linq;

namespace MeowChan.Desktop;

/// <summary>
/// Playback speeds of the gallery transport. A click on the speed button and the speed keys (video.speed-down / video.speed-up: [ ] and X C) step through
/// <see cref="Cycle"/>; a right-click (or "Choose speed…") offers every entry of <see cref="Choices"/>, 0.25× to 4× in 0.25 steps.
/// The range is a documented fixed ceiling of the backend's useful rates, not a setting.
/// </summary>
internal static class VideoRates
{
    internal const float Normal = 1f;
    internal const float Minimum = 0.25f;
    internal const float Maximum = 4f;
    internal const float ChoiceStep = 0.25f;
    internal static readonly float[] Cycle = { 0.5f, 1f, 1.5f, 2f };
    internal static readonly float[] Choices = Enumerable.Range(1, (int)(Maximum / ChoiceStep)).Select(step => step * ChoiceStep).ToArray();

    private const float Epsilon = 0.001f;

    /// <summary>The next click speed after <paramref name="current"/>: the first cycle entry above it, wrapping to the slowest.</summary>
    internal static float Next(float current)
    {
        foreach (float rate in Cycle) if (rate > current + Epsilon) return rate;
        return Cycle[0];
    }

    /// <summary>One cycle entry down (negative) or up (positive) from <paramref name="current"/>, never wrapping; 0 returns to 1×.
    /// A chosen speed between or beyond the cycle entries steps to the nearest entry in that direction (or stays at the end).</summary>
    internal static float Step(float current, int direction)
    {
        if (direction == 0) return Normal;
        if (direction > 0)
        {
            foreach (float rate in Cycle) if (rate > current + Epsilon) return rate;
            return current;
        }
        for (int index = Cycle.Length - 1; index >= 0; index--) if (Cycle[index] < current - Epsilon) return Cycle[index];
        return current;
    }

    /// <summary>A speed clamped to 0.25×–4× and rounded to the 0.25 grid (the only values the menu offers).</summary>
    internal static float Normalize(float rate)
    {
        if (float.IsNaN(rate)) return Normal;
        float clamped = Math.Clamp(rate, Minimum, Maximum);
        return MathF.Round(clamped / ChoiceStep) * ChoiceStep;
    }

    internal static bool Same(float a, float b) => Math.Abs(a - b) < Epsilon;
}

internal enum VideoAbLoopState { Off, StartSet, Active }

internal enum VideoAbLoopPress { StartSet, Active, TooShort, Cleared }

/// <summary>
/// The A–B loop of one medium, in media milliseconds. The first press sets A, the second sets B (the two are ordered, so B may
/// be pressed before A in time) and starts the loop, the third clears it. A and B lie at least <see cref="MinimumSpanMs"/> apart
/// (a documented fixed minimum: a shorter loop cannot show a frame before it jumps). While active, reaching B returns to A.
/// </summary>
internal sealed class VideoAbLoop
{
    internal const long MinimumSpanMs = 200;
    /// <summary>A jump is scheduled ahead of B when the next observed time would lie beyond it (VLC reports time a few times per
    /// second, which alone would overshoot B by up to a quarter second).</summary>
    internal const long LookaheadMs = 350;

    internal VideoAbLoopState State { get; private set; }
    internal long Start { get; private set; }
    internal long End { get; private set; }
    internal bool IsActive => State == VideoAbLoopState.Active;
    internal bool IsSet => State != VideoAbLoopState.Off;

    internal VideoAbLoopPress Press(long position, long length)
    {
        long clamped = Math.Max(0, length > 0 ? Math.Min(position, length) : position);
        switch (State)
        {
            case VideoAbLoopState.Off:
                Start = clamped; End = 0; State = VideoAbLoopState.StartSet;
                return VideoAbLoopPress.StartSet;
            case VideoAbLoopState.StartSet:
                long start = Math.Min(Start, clamped), end = Math.Max(Start, clamped);
                if (end - start < MinimumSpanMs) return VideoAbLoopPress.TooShort;
                Start = start; End = end; State = VideoAbLoopState.Active;
                return VideoAbLoopPress.Active;
            default:
                Clear();
                return VideoAbLoopPress.Cleared;
        }
    }

    internal void Clear() { State = VideoAbLoopState.Off; Start = End = 0; }

    /// <summary>Whether the observed <paramref name="position"/> has reached B and playback must return to A.</summary>
    internal bool ShouldReturn(long position) => IsActive && position >= End;

    /// <summary>Wall-clock milliseconds until B at <paramref name="rate"/>, when B is close enough that the next time report
    /// would overshoot it (within <see cref="LookaheadMs"/>); otherwise null.</summary>
    internal long? JumpDelay(long position, float rate)
    {
        if (!IsActive || position >= End || rate <= 0) return null;
        long remaining = (long)Math.Ceiling((End - position) / rate);
        return remaining <= LookaheadMs ? remaining : null;
    }

}

/// <summary>Next frame (key "."): only a seekable, shown, not ended medium steps; a playing one is paused first, because the
/// backend steps a paused picture. LibVLC 3 has no step back.</summary>
internal static class VideoFrameStepPolicy
{
    internal static bool CanStep(bool canSeek, bool ended, VideoPlaybackMode mode) => canSeek && !ended && mode != VideoPlaybackMode.Stopped;
    internal static bool PausesFirst(VideoPlaybackMode mode) => mode == VideoPlaybackMode.Playing;
}

/// <summary>An audio track of the shown medium as the player menu lists it (the backend's "disable" entry is not a track).</summary>
internal readonly record struct VideoAudioTrackChoice(int Id, string Name);

internal static class VideoAudioTracks
{
    /// <summary>The real tracks of a backend description list: entries with a negative id ("Disable") are left out.</summary>
    internal static IReadOnlyList<VideoAudioTrackChoice> Real(IEnumerable<(int Id, string? Name)> descriptions) =>
        descriptions.Where(track => track.Id >= 0).Select(track => new VideoAudioTrackChoice(track.Id, track.Name?.Trim() ?? string.Empty)).ToArray();

    /// <summary>The track after <paramref name="current"/> in list order, wrapping; null when there is nothing to switch to.</summary>
    internal static int? Next(IReadOnlyList<VideoAudioTrackChoice> tracks, int current)
    {
        if (tracks.Count < 2) return null;
        int index = -1;
        for (int i = 0; i < tracks.Count; i++) if (tracks[i].Id == current) { index = i; break; }
        return tracks[(index + 1) % tracks.Count].Id;
    }
}
