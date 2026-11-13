using System;

namespace MeowChan.Desktop;

/// <summary>
/// Whether the gallery's Fit view may enlarge a small image up to the whole viewport (the default, "no limit") or only up
/// to <see cref="MaxUpscalePercent"/> of its size in device-independent pixels. Fit only; manual zoom is not capped.
/// </summary>
internal readonly record struct GalleryFitPreferences(bool Limit = false, int MaxUpscalePercent = GalleryFitPreferences.MaxUpscalePercentDefault)
{
    // A struct's parameterless constructor would otherwise zero the percent instead of using its default.
    public GalleryFitPreferences() : this(false, MaxUpscalePercentDefault) { }

    internal const int MaxUpscalePercentDefault = 200;
    internal const int MaxUpscalePercentMinimum = 100;
    internal const int MaxUpscalePercentMaximum = 1600;

    internal static int ClampPercent(int percent) => Math.Clamp(percent, MaxUpscalePercentMinimum, MaxUpscalePercentMaximum);

    /// <summary>The enlargement cap in physical pixels per source pixel for this display scale, or null without a limit.</summary>
    internal double? MaxUpscale(double displayScale) => !Limit || !double.IsFinite(displayScale) || displayScale <= 0
        ? null : ClampPercent(MaxUpscalePercent) / 100.0 * displayScale;
}
