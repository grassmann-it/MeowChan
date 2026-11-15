using System;

namespace MeowChan.Desktop;

/// <summary>
/// The keyboard of a shown video beyond the transport bar: "," and "." jump back and forward by the seek step setting
/// (<see cref="Config.GalleryVideoSeekStepSeconds"/>) and L repeats the video. The keys are matched by the gallery's single key
/// handler (they do nothing while a picture is shown and never reach a text field); this partial only holds their actions.
/// </summary>
internal sealed partial class GalleryViewer
{
    private int _videoSeekStepSeconds = Config.GalleryVideoSeekStepSecondsDefault;

    /// <summary>Raised after the repeat key: the window says "Repeat on / off" (or why the key changed nothing).</summary>
    internal event Action<VideoLoopKeyResult, string?>? VideoLoopKeyed;

    /// <summary>Seconds of one "," / "." press (setting <see cref="Config.GalleryVideoSeekStepSeconds"/>, clamped to its range).</summary>
    internal int VideoSeekStepSeconds
    {
        get => _videoSeekStepSeconds;
        set
        {
            _videoSeekStepSeconds = VideoKeyboardSteps.ClampSeconds(value);
            _video?.ConfigureSeekStep(_videoSeekStepSeconds);
        }
    }

    private void SeekVideoByStep(bool forward) => SeekVideo((int)VideoKeyboardSteps.Offset(forward, _videoSeekStepSeconds).TotalSeconds);

    private void ToggleVideoLoopByKey()
    {
        if (!CanUseVideoTransport || _video is not { } video) return;
        bool before = video.LoopEnabled;
        bool owned = video.RepeatOwnedBySlideshow;
        video.ToggleLoop();
        VideoLoopKeyResult result = VideoKeyboardSteps.LoopResult(before, video.LoopEnabled, owned);
        if (result != VideoLoopKeyResult.Unchanged) VideoLoopKeyed?.Invoke(result, video.RepeatOwnedReason);
    }
}
