using System;
using System.Globalization;
using System.Linq;
using MeowChan.Gallery;
using MeowChan.Storage;

namespace MeowChan.Desktop;

/// <summary>
/// Caller-configured limits of the scrub preview cache (NH-3). The disk budget is a user setting: <see cref="CacheLimitMiBDefault"/>,
/// <see cref="CacheLimitMiBMinimum"/> and <see cref="CacheLimitMiBMaximum"/> are the range a later <c>Config</c> key mirrors (pattern of
/// <c>PosterCacheLimitMiB</c>); nothing here reads or writes settings. Thumbnail bounds, per-thumbnail byte bound and the decode timeout
/// are technical safety limits, not user budgets.
/// </summary>
internal sealed record VideoScrubPreviewOptions
{
    internal const int CacheLimitMiBDefault = 128;
    internal const int CacheLimitMiBMinimum = 8;
    internal const int CacheLimitMiBMaximum = 4096;
    /// <summary>Thumbnail bounds handed to the decoder; a decoded frame is scaled to fit, never enlarged.</summary>
    internal const int ThumbnailMaximumWidth = 320, ThumbnailMaximumHeight = 180;
    /// <summary>A stored or decoded thumbnail above this size is rejected (a 320×180 PNG is far below it).</summary>
    internal const int MaximumThumbnailBytes = 512 * 1024;
    internal static readonly TimeSpan DecodeTimeoutDefault = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan DecodeTimeoutMaximum = TimeSpan.FromMinutes(2);

    internal static VideoScrubPreviewOptions Default { get; } = new(CacheLimitMiBDefault);

    /// <param name="cacheLimitMiB">Disk budget in MiB; outside <see cref="CacheLimitMiBMinimum"/>..<see cref="CacheLimitMiBMaximum"/> throws.</param>
    /// <param name="decodeTimeout">Bound of one decode after it got the native gate; null is <see cref="DecodeTimeoutDefault"/>.</param>
    internal VideoScrubPreviewOptions(int cacheLimitMiB, TimeSpan? decodeTimeout = null)
    {
        if (!IsSupportedCacheLimit(cacheLimitMiB))
            throw new ArgumentOutOfRangeException(nameof(cacheLimitMiB), cacheLimitMiB, "The scrub preview cache limit is outside its supported range ("
                + CacheLimitMiBMinimum + " to " + CacheLimitMiBMaximum + " MiB).");
        TimeSpan timeout = decodeTimeout ?? DecodeTimeoutDefault;
        if (timeout <= TimeSpan.Zero || timeout > DecodeTimeoutMaximum)
            throw new ArgumentOutOfRangeException(nameof(decodeTimeout), timeout, "The scrub decode timeout must be positive and at most two minutes.");
        CacheLimitMiB = cacheLimitMiB;
        DecodeTimeout = timeout;
    }

    internal int CacheLimitMiB { get; }
    internal long CacheLimitBytes => (long)CacheLimitMiB * 1024 * 1024;
    internal TimeSpan DecodeTimeout { get; }

    internal static bool IsSupportedCacheLimit(int cacheLimitMiB) => cacheLimitMiB is >= CacheLimitMiBMinimum and <= CacheLimitMiBMaximum;

    /// <summary>For callers that load an older or hand-edited value and prefer the nearest supported one over an error.</summary>
    internal static int ClampCacheLimit(int cacheLimitMiB) => Math.Clamp(cacheLimitMiB, CacheLimitMiBMinimum, CacheLimitMiBMaximum);
}

/// <summary>Scrub positions in 5 % steps: slot <c>k</c> covers [k·5 %, (k+1)·5 %) and shows the frame at k·5 %.</summary>
internal static class VideoScrubSlots
{
    internal const int StepPercent = 5;
    internal const int Count = 100 / StepPercent;

    /// <summary>The slot of a seek-bar fraction; NaN and values outside [0, 1] are clamped, so 1.0 maps to the last slot.</summary>
    internal static int FromFraction(double fraction)
    {
        if (double.IsNaN(fraction) || fraction <= 0) return 0;
        if (fraction >= 1) return Count - 1;
        return Math.Min(Count - 1, (int)Math.Floor(fraction * Count));
    }

    internal static double Fraction(int slot) => Validate(slot) / (double)Count;

    /// <summary>The decode position of <paramref name="slot"/> in a video of <paramref name="duration"/>.</summary>
    internal static TimeSpan Start(int slot, TimeSpan duration)
    {
        Validate(slot);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        return TimeSpan.FromTicks(duration.Ticks / Count * slot);
    }

    internal static int Validate(int slot) =>
        slot is >= 0 and < Count ? slot : throw new ArgumentOutOfRangeException(nameof(slot), slot, "A scrub slot is 0 to " + (Count - 1) + ".");
}

/// <summary>
/// The persistent identity of one scrub thumbnail: verified content (SHA-256 and length of the recorded original, so a changed
/// file is a different revision), slot, thumbnail bounds and format version. Paths and names are not part of it: the same bytes
/// saved twice share their thumbnails, and the original is still verified before any of them is shown.
/// </summary>
internal readonly record struct VideoScrubFrameKey(string Sha256, long Length, int Slot)
{
    internal const int FormatVersion = 1;

    internal string FileName => string.Create(CultureInfo.InvariantCulture,
        $"v{FormatVersion}-{Sha256}-{Length}-{VideoScrubPreviewOptions.ThumbnailMaximumWidth}x{VideoScrubPreviewOptions.ThumbnailMaximumHeight}-s{Slot:D2}.png");

    /// <summary>Only a recorded video of this manifest with a well-formed checksum and a supported length has scrub previews.</summary>
    internal static bool IsEligible(MediaAssetSnapshot manifest, MediaAssetReference asset) =>
        manifest.FindById(asset.AssetId) == asset
        && SavedMediaTypes.Classify(asset.OriginalFileName ?? asset.Path) == SavedMediaKind.Video
        && asset.Length > 0 && asset.Length <= VideoPosterCache.MaximumVideoBytes
        && asset.Sha256.Length == 64 && asset.Sha256.All(char.IsAsciiHexDigit);

    internal static VideoScrubFrameKey For(MediaAssetReference asset, int slot) =>
        new(asset.Sha256.ToLowerInvariant(), asset.Length, VideoScrubSlots.Validate(slot));
}
