using System;

namespace MeowChan.Desktop;

internal enum GalleryPlaybackMode { Manual, Repeat, Slideshow }

/// <summary>Profile choices only; never contains a running slideshow or a media position.</summary>
internal sealed record GalleryPlaybackPreferences(
    GalleryPlaybackMode Mode = GalleryPlaybackMode.Manual,
    int ImageSeconds = 5,
    bool MediaFocus = false,
    bool OverlayControls = false,
    bool AutoHideControls = false,
    bool OverlayPostContext = false)
{
    // Overlay is the legacy authority: old docked profiles had dormant auto-hide ON.
    // Normalize snapshots, never infer an explicit toggle from a mismatched pair.
    internal GalleryPlaybackPreferences Normalize() => this with { AutoHideControls = OverlayControls };

    // Both legacy UI/command aliases must use this explicit paired action.
    internal GalleryPlaybackPreferences WithControlsOverlay(bool enabled) =>
        this with { OverlayControls = enabled, AutoHideControls = enabled };

    internal static GalleryPlaybackPreferences FromConfig(Config config) => new GalleryPlaybackPreferences(
        Enum.TryParse<GalleryPlaybackMode>(config.GalleryPlaybackMode, out var mode) ? mode : GalleryPlaybackMode.Manual,
        config.GallerySlideshowSeconds, config.GalleryMediaFocus, config.GalleryOverlayControls,
        config.GalleryAutoHideControls, config.GalleryOverlayPostContext).Normalize();

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
    }
}
