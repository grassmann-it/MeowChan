using System;

namespace MeowChan.Desktop;

/// <summary>Reader display choices that are mapped to Config by P1.</summary>
internal sealed class ReaderDisplayPreferences
{
    // These are the product defaults and preference bounds. They deliberately live outside Config until P1 owns
    // persistence, but the reader uses the same clamping seam now so an invalid value can never reach layout/decoding.
    internal const int ReaderThumbnailWidthDefault = 240;
    internal const int ReaderThumbnailWidthMinimum = 120;
    internal const int ReaderThumbnailWidthMaximum = 480;

    internal int ReaderThumbnailWidth
    {
        get => _readerThumbnailWidth;
        set => _readerThumbnailWidth = ClampThumbnailWidth(value);
    }

    internal ReaderDateStyle ReaderDateStyle
    {
        get => _readerDateStyle;
        set => _readerDateStyle = Enum.IsDefined(value) ? value : ReaderDateStyle.System;
    }

    private int _readerThumbnailWidth = ReaderThumbnailWidthDefault;
    private ReaderDateStyle _readerDateStyle;

    internal static int ClampThumbnailWidth(int value) =>
        Math.Clamp(value, ReaderThumbnailWidthMinimum, ReaderThumbnailWidthMaximum);

    /// <summary>Returns the image width used by the normal or detached large reader.</summary>
    internal static int EffectiveThumbnailWidth(int value, bool largeReader)
    {
        int width = ClampThumbnailWidth(value);
        return largeReader ? width * 3 / 2 : width;
    }

    /// <summary>
    /// Returns decoder bounds for a reader thumbnail. Normal thumbnails retain the 240-pixel floor and a 3/4 height
    /// bound; the large reader keeps its established 1040×720 decode budget.
    /// </summary>
    internal static ThumbnailDecodeBounds DecodeBounds(int value, bool largeReader)
    {
        int width = ClampThumbnailWidth(value);
        return largeReader
            ? new ThumbnailDecodeBounds(1040, 720)
            : new ThumbnailDecodeBounds(Math.Max(240, width), Math.Max(1, Math.Max(240, width) * 3 / 4));
    }

    internal readonly record struct ThumbnailDecodeBounds(int MaximumWidth, int MaximumHeight);
}

internal enum ReaderDateStyle
{
    System = 0,
    Long = 1,
    SinceOp = 2,
    RelativeToNow = 3
}
