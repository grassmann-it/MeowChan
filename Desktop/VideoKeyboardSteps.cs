using System;

namespace MeowChan.Desktop;

/// <summary>What the repeat key (L) did, for the short status the window shows.</summary>
internal enum VideoLoopKeyResult
{
    /// <summary>Nothing changed (no seekable video yet, or the control is unavailable).</summary>
    Unchanged,
    /// <summary>Repeat was switched on.</summary>
    On,
    /// <summary>Repeat was switched off.</summary>
    Off,
    /// <summary>A running slideshow decides about repeating, so the key changed nothing.</summary>
    OwnedBySlideshow
}

/// <summary>Pure rules of the video keys "," and "." (seek step) and L (repeat); no UI types.</summary>
internal static class VideoKeyboardSteps
{
    /// <summary>The seek step setting clamped to its range (default 5, 1 to 60 seconds).</summary>
    internal static int ClampSeconds(int seconds) =>
        Math.Clamp(seconds, Config.GalleryVideoSeekStepSecondsMinimum, Config.GalleryVideoSeekStepSecondsMaximum);

    /// <summary>The offset of one step key press: negative for back, positive for forward, never zero.</summary>
    internal static TimeSpan Offset(bool forward, int seconds) =>
        TimeSpan.FromSeconds(forward ? ClampSeconds(seconds) : -ClampSeconds(seconds));

    /// <summary>How a press of the repeat key ended.</summary>
    internal static VideoLoopKeyResult LoopResult(bool before, bool after, bool slideshowOwnsRepeat)
    {
        if (before == after) return slideshowOwnsRepeat ? VideoLoopKeyResult.OwnedBySlideshow : VideoLoopKeyResult.Unchanged;
        return after ? VideoLoopKeyResult.On : VideoLoopKeyResult.Off;
    }
}
