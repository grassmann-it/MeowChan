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

    // Reader media size (R6). "Textless" is a post with no text of its own: its image is shown larger. The focused card's media can
    // be enlarged by a percentage, and an image can open an enlarged preview while the pointer rests on it.
    internal const int TextlessMediaWidthDefault = 360;
    internal const int TextlessMediaWidthMinimum = 120;
    internal const int TextlessMediaWidthMaximum = 960;
    internal const int FocusedMediaScaleDefault = 200;
    internal const int FocusedMediaScaleMinimum = 100;
    internal const int FocusedMediaScaleMaximum = 400;
    internal const int HoverEnlargeSizeDefault = 640;
    internal const int HoverEnlargeSizeMinimum = 240;
    internal const int HoverEnlargeSizeMaximum = 1600;
    internal const int HoverEnlargeDelayDefault = 350;
    internal const int HoverEnlargeDelayMinimum = 100;
    internal const int HoverEnlargeDelayMaximum = 2000;
    // The reading history (R5): places of the last threads read, one per thread.
    internal const int ReadingHistoryLimitDefault = 10;
    internal const int ReadingHistoryLimitMinimum = 5;
    internal const int ReadingHistoryLimitMaximum = 25;

    internal int ReaderThumbnailWidth
    {
        get => _readerThumbnailWidth;
        set => _readerThumbnailWidth = ClampThumbnailWidth(value);
    }

    /// <summary>The reader media size switches and their numbers (R6); applied live, only the textless part rebuilds the posts.</summary>
    internal ReaderMediaPreferences Media
    {
        get => _media;
        set => _media = value.Clamped();
    }

    internal ReaderDateStyle ReaderDateStyle
    {
        get => _readerDateStyle;
        set => _readerDateStyle = Enum.IsDefined(value) ? value : ReaderDateStyle.System;
    }

    private ReaderMediaPreferences _media = ReaderMediaPreferences.Default;
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

    /// <summary>
    /// Decoder bounds for a preview drawn <paramref name="displayWidth"/> wide (the effective width, already scaled for the large reader).
    /// For the ordinary widths this is exactly <see cref="DecodeBounds"/>; wider previews (textless posts, the enlarged focused card)
    /// grow the bounds with them, so a larger image is never a stretched small one.
    /// </summary>
    internal static ThumbnailDecodeBounds DecodeBoundsForWidth(int displayWidth, bool largeReader)
    {
        int width = Math.Max(1, displayWidth);
        return largeReader
            ? new ThumbnailDecodeBounds(Math.Max(1040, width), Math.Max(720, width * 3 / 4))
            : new ThumbnailDecodeBounds(Math.Max(240, width), Math.Max(1, Math.Max(240, width) * 3 / 4));
    }

    /// <summary>
    /// The image width of a post that has no text of its own: the textless width (scaled like the thumbnail in the large reader), never
    /// narrower than the ordinary preview width. With "larger images" off it is the ordinary width.
    /// </summary>
    internal static int EffectiveTextlessWidth(int thumbnailWidth, ReaderMediaPreferences media, bool largeReader)
    {
        int ordinary = EffectiveThumbnailWidth(thumbnailWidth, largeReader);
        if (!media.TextlessLarge) return ordinary;
        int textless = Math.Clamp(media.TextlessWidth, TextlessMediaWidthMinimum, TextlessMediaWidthMaximum);
        return Math.Max(ordinary, largeReader ? textless * 3 / 2 : textless);
    }

    /// <summary>The percentage the focused card's media is drawn at: 100 while the option is off.</summary>
    internal static int FocusedScalePercent(ReaderMediaPreferences media) =>
        media.FocusedEnlarge ? Math.Clamp(media.FocusedScalePercent, FocusedMediaScaleMinimum, FocusedMediaScaleMaximum) : 100;

    internal readonly record struct ThumbnailDecodeBounds(int MaximumWidth, int MaximumHeight);
}

/// <summary>
/// The reader's media size choices as one value. The three switches are also quick toggles in the reader's View menu; the numbers are
/// settings with default, minimum and maximum. "Focused" media is the single media presentation the reader shows enlarged: the inline
/// card today, the full-height media sidebar later (<see cref="ReaderFocusedMedia"/> is the shared seam).
/// </summary>
internal readonly record struct ReaderMediaPreferences(bool TextlessLarge, int TextlessWidth, bool FocusedEnlarge,
    int FocusedScalePercent, bool HoverEnlarge, int HoverSize, int HoverDelayMs)
{
    internal static ReaderMediaPreferences Default { get; } = new(true, ReaderDisplayPreferences.TextlessMediaWidthDefault,
        false, ReaderDisplayPreferences.FocusedMediaScaleDefault, false, ReaderDisplayPreferences.HoverEnlargeSizeDefault,
        ReaderDisplayPreferences.HoverEnlargeDelayDefault);

    internal static ReaderMediaPreferences From(Config config) => new(config.ReaderTextlessLargeImages, config.ReaderTextlessMediaWidth,
        config.ReaderFocusedMediaEnlarge, config.ReaderFocusedMediaScalePercent, config.ReaderHoverEnlarge, config.ReaderHoverEnlargeSize,
        config.ReaderHoverEnlargeDelayMs);

    internal ReaderMediaPreferences Clamped() => this with
    {
        TextlessWidth = Math.Clamp(TextlessWidth, ReaderDisplayPreferences.TextlessMediaWidthMinimum, ReaderDisplayPreferences.TextlessMediaWidthMaximum),
        FocusedScalePercent = Math.Clamp(FocusedScalePercent, ReaderDisplayPreferences.FocusedMediaScaleMinimum, ReaderDisplayPreferences.FocusedMediaScaleMaximum),
        HoverSize = Math.Clamp(HoverSize, ReaderDisplayPreferences.HoverEnlargeSizeMinimum, ReaderDisplayPreferences.HoverEnlargeSizeMaximum),
        HoverDelayMs = Math.Clamp(HoverDelayMs, ReaderDisplayPreferences.HoverEnlargeDelayMinimum, ReaderDisplayPreferences.HoverEnlargeDelayMaximum)
    };

    /// <summary>What changes the posts' own images (and so rebuilds them): the focus and hover options act live.</summary>
    internal bool SamePostImages(ReaderMediaPreferences other) =>
        TextlessLarge == other.TextlessLarge && (!TextlessLarge || TextlessWidth == other.TextlessWidth);
}

internal enum ReaderDateStyle
{
    System = 0,
    Long = 1,
    SinceOp = 2,
    RelativeToNow = 3
}
