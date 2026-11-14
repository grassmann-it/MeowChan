using System;

namespace MeowChan.Desktop;

/// <summary>Whether the gallery advances by itself. "Repeat video" is a separate choice (<see cref="GalleryPlaybackPreferences.VideoRepeat"/>);
/// it was the third mode until 1.1 and an old profile's "Repeat" is read as Manual with repeat on (see Config.GalleryPlaybackMode).</summary>
internal enum GalleryPlaybackMode { Manual, Slideshow }

/// <summary>What a running slideshow does when it reaches a video. Images always show for the slideshow seconds.</summary>
internal enum SlideshowVideoRule
{
    /// <summary>Play the video once, then advance.</summary>
    PlayOnce,
    /// <summary>Loop until at least the configured seconds have played; the pass that crosses them plays to its end.</summary>
    LoopAtLeastSeconds,
    /// <summary>Play the configured number of passes, then advance.</summary>
    RepeatTimes,
}

/// <summary>Profile choices only; never contains a running slideshow or a media position.</summary>
internal sealed record GalleryPlaybackPreferences(
    GalleryPlaybackMode Mode = GalleryPlaybackMode.Manual,
    int ImageSeconds = 5,
    bool MediaFocus = false,
    bool OverlayControls = false,
    bool AutoHideControls = false,
    bool OverlayPostContext = false,
    bool VideoRepeat = false,
    SlideshowVideoRule VideoRule = SlideshowVideoRule.PlayOnce,
    int VideoSeconds = Config.GallerySlideshowVideoSecondsDefault,
    int VideoRepeats = Config.GallerySlideshowVideoRepeatsDefault)
{
    internal const int ImageSecondsMinimum = 1, ImageSecondsMaximum = 300;

    // Overlay is the legacy authority: old docked profiles had dormant auto-hide ON.
    // Normalize snapshots, never infer an explicit toggle from a mismatched pair.
    internal GalleryPlaybackPreferences Normalize() => this with
    {
        AutoHideControls = OverlayControls,
        Mode = Enum.IsDefined(Mode) ? Mode : GalleryPlaybackMode.Manual,
        ImageSeconds = Math.Clamp(ImageSeconds, ImageSecondsMinimum, ImageSecondsMaximum),
        VideoRule = Enum.IsDefined(VideoRule) ? VideoRule : SlideshowVideoRule.PlayOnce,
        VideoSeconds = Math.Clamp(VideoSeconds, Config.GallerySlideshowVideoSecondsMinimum, Config.GallerySlideshowVideoSecondsMaximum),
        VideoRepeats = Math.Clamp(VideoRepeats, Config.GallerySlideshowVideoRepeatsMinimum, Config.GallerySlideshowVideoRepeatsMaximum),
    };

    // Both legacy UI/command aliases must use this explicit paired action.
    internal GalleryPlaybackPreferences WithControlsOverlay(bool enabled) =>
        this with { OverlayControls = enabled, AutoHideControls = enabled };

    internal static SlideshowVideoRule ParseVideoRule(string? value) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out SlideshowVideoRule rule) && Enum.IsDefined(rule) && !int.TryParse(value, out _)
            ? rule : SlideshowVideoRule.PlayOnce;

    internal static GalleryPlaybackPreferences FromConfig(Config config) => new GalleryPlaybackPreferences(
        Enum.TryParse<GalleryPlaybackMode>(config.GalleryPlaybackMode, out var mode) ? mode : GalleryPlaybackMode.Manual,
        config.GallerySlideshowSeconds, config.GalleryMediaFocus, config.GalleryOverlayControls,
        config.GalleryAutoHideControls, config.GalleryOverlayPostContext, config.GalleryVideoRepeat,
        ParseVideoRule(config.GallerySlideshowVideoRule), config.GallerySlideshowVideoSeconds, config.GallerySlideshowVideoRepeats).Normalize();

    internal void ApplyChangesTo(Config config, GalleryPlaybackPreferences baseline)
    {
        GalleryPlaybackPreferences edited = Normalize();
        baseline = baseline.Normalize();
        // Normalize live state even when this draft leaves the paired choice untouched.
        config.GalleryAutoHideControls = config.GalleryOverlayControls;
        if (edited.Mode != baseline.Mode) config.GalleryPlaybackMode = edited.Mode.ToString();
        if (edited.ImageSeconds != baseline.ImageSeconds) config.GallerySlideshowSeconds = edited.ImageSeconds;
        if (edited.MediaFocus != baseline.MediaFocus) config.GalleryMediaFocus = edited.MediaFocus;
        if (edited.OverlayControls != baseline.OverlayControls)
        {
            config.GalleryOverlayControls = edited.OverlayControls;
            config.GalleryAutoHideControls = edited.OverlayControls;
        }
        if (edited.OverlayPostContext != baseline.OverlayPostContext) config.GalleryOverlayPostContext = edited.OverlayPostContext;
        if (edited.VideoRepeat != baseline.VideoRepeat) config.GalleryVideoRepeat = edited.VideoRepeat;
        if (edited.VideoRule != baseline.VideoRule) config.GallerySlideshowVideoRule = edited.VideoRule.ToString();
        if (edited.VideoSeconds != baseline.VideoSeconds) config.GallerySlideshowVideoSeconds = edited.VideoSeconds;
        if (edited.VideoRepeats != baseline.VideoRepeats) config.GallerySlideshowVideoRepeats = edited.VideoRepeats;
    }

    internal void ApplyTo(Config config)
    {
        GalleryPlaybackPreferences normalized = Normalize();
        config.GalleryPlaybackMode = normalized.Mode.ToString();
        config.GallerySlideshowSeconds = normalized.ImageSeconds;
        config.GalleryMediaFocus = normalized.MediaFocus;
        config.GalleryOverlayControls = normalized.OverlayControls;
        config.GalleryAutoHideControls = normalized.AutoHideControls;
        config.GalleryOverlayPostContext = normalized.OverlayPostContext;
        config.GalleryVideoRepeat = normalized.VideoRepeat;
        config.GallerySlideshowVideoRule = normalized.VideoRule.ToString();
        config.GallerySlideshowVideoSeconds = normalized.VideoSeconds;
        config.GallerySlideshowVideoRepeats = normalized.VideoRepeats;
    }
}
