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
    // The media sidebar of the large reader (R8): its width (also dragged with the splitter) and the reader width below which it
    // gives way to the posts. In device-independent pixels.
    internal const int MediaSidebarInfoMinimumFreeDefault = 160, MediaSidebarInfoMinimumFreeMinimum = 96, MediaSidebarInfoMinimumFreeMaximum = 480;
    internal const int MediaSidebarInfoMaximumShareDefault = 40, MediaSidebarInfoMaximumShareMinimum = 20, MediaSidebarInfoMaximumShareMaximum = 60;
    internal const int MediaSidebarWidthDefault = 480;
    internal const int MediaSidebarWidthMinimum = 240;
    internal const int MediaSidebarWidthMaximum = 1200;
    internal const int MediaSidebarReaderWidthDefault = 1280;
    internal const int MediaSidebarReaderWidthMinimum = 800;
    internal const int MediaSidebarReaderWidthMaximum = 3840;
    // Several media in one post (R11). The sidebar's thumbnail strip shows at most this many files at once; a post with a little text
    // (at most this many characters, whitespace trimmed) shows its images as a two-column grid instead of a long column.
    internal const int MediaStripLimitDefault = 12;
    internal const int MediaStripLimitMinimum = 2;
    internal const int MediaStripLimitMaximum = 40;
    internal const int MediaGridTextLimitDefault = 280;
    internal const int MediaGridTextLimitMinimum = 0;
    internal const int MediaGridTextLimitMaximum = 2000;
    // The reading history (R5): places of the last threads read, one per thread.
    // RH1: 50 threads by default (10 to 500), so "Show full history…" holds more than the ten of the menu.
    internal const int ReadingHistoryLimitDefault = 50;
    internal const int ReadingHistoryLimitMinimum = 10;
    internal const int ReadingHistoryLimitMaximum = 500;
    // RH1: how many of those threads the "Continue reading" menu lists (the history window shows all of them).
    internal const int ReadingHistoryMenuEntriesDefault = 10;
    internal const int ReadingHistoryMenuEntriesMinimum = 3;
    internal const int ReadingHistoryMenuEntriesMaximum = 25;

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

    /// <summary>The media sidebar of the large reader (R8) presents the focused post's media in its own column; on by default.</summary>
    internal bool Sidebar { get; init; } = true;
    /// <summary>The switched-on sidebar shows also beside the posts of the archive view, not only in the large reader (Off / Large reader only / Always).</summary>
    internal bool SidebarAlways { get; init; }
    /// <summary>The sidebar sits right of the posts instead of left.</summary>
    internal string MediaSidebarInfo { get; init; } = "Off";
    internal int MediaSidebarInfoMinimumFree { get; init; } = ReaderDisplayPreferences.MediaSidebarInfoMinimumFreeDefault;
    internal int MediaSidebarInfoMaximumShare { get; init; } = ReaderDisplayPreferences.MediaSidebarInfoMaximumShareDefault;
    internal bool SidebarRight { get; init; }
    internal int SidebarWidth { get; init; } = ReaderDisplayPreferences.MediaSidebarWidthDefault;
    /// <summary>Below this reader width the sidebar gives way to the posts (it stays chosen and returns when the reader is wider).</summary>
    internal int SidebarMinimumReaderWidth { get; init; } = ReaderDisplayPreferences.MediaSidebarReaderWidthDefault;
    /// <summary>The most files the sidebar's thumbnail strip shows at once (R11); acts live.</summary>
    internal int StripLimit { get; init; } = ReaderDisplayPreferences.MediaStripLimitDefault;
    /// <summary>Posts with several images and little text show them as a two-column grid (R11); rebuilds the posts.</summary>
    internal bool MediaGrid { get; init; } = true;
    /// <summary>"Little text": at most this many characters.</summary>
    internal int GridTextLimit { get; init; } = ReaderDisplayPreferences.MediaGridTextLimitDefault;
    /// <summary>Videos in the reader and "Clicking a medium" (R10): sidebar by default; acts live (no post is rebuilt).</summary>
    internal ReaderInlineVideoOptions Video { get; init; } = ReaderInlineVideoOptions.Default with { Enabled = true };

    internal static ReaderMediaPreferences From(Config config) => new(config.ReaderTextlessLargeImages, config.ReaderTextlessMediaWidth,
        config.ReaderFocusedMediaEnlarge, config.ReaderFocusedMediaScalePercent, config.ReaderHoverEnlarge, config.ReaderHoverEnlargeSize,
        config.ReaderHoverEnlargeDelayMs)
    {
        Sidebar = config.ReaderMediaSidebar,
        SidebarAlways = config.ReaderMediaSidebarScope == Config.ReaderMediaSidebarScopeAlways,
        SidebarRight = config.ReaderMediaSidebarSide == Config.ReaderMediaSidebarSideRight,
        MediaSidebarInfo = config.MediaSidebarInfo,
        MediaSidebarInfoMinimumFree = config.MediaSidebarInfoMinimumFree,
        MediaSidebarInfoMaximumShare = config.MediaSidebarInfoMaximumShare,
        SidebarWidth = config.ReaderMediaSidebarWidth,
        SidebarMinimumReaderWidth = config.ReaderMediaSidebarMinimumWidth,
        Video = ReaderInlineVideoOptions.From(config),
        StripLimit = config.ReaderMediaStripLimit,
        MediaGrid = config.ReaderMediaGrid,
        GridTextLimit = config.ReaderMediaGridTextLimit
    };

    internal ReaderMediaPreferences Clamped() => this with
    {
        TextlessWidth = Math.Clamp(TextlessWidth, ReaderDisplayPreferences.TextlessMediaWidthMinimum, ReaderDisplayPreferences.TextlessMediaWidthMaximum),
        FocusedScalePercent = Math.Clamp(FocusedScalePercent, ReaderDisplayPreferences.FocusedMediaScaleMinimum, ReaderDisplayPreferences.FocusedMediaScaleMaximum),
        HoverSize = Math.Clamp(HoverSize, ReaderDisplayPreferences.HoverEnlargeSizeMinimum, ReaderDisplayPreferences.HoverEnlargeSizeMaximum),
        HoverDelayMs = Math.Clamp(HoverDelayMs, ReaderDisplayPreferences.HoverEnlargeDelayMinimum, ReaderDisplayPreferences.HoverEnlargeDelayMaximum),
        MediaSidebarInfo = Config.NormalizeSidebarInfo(MediaSidebarInfo),
        MediaSidebarInfoMinimumFree = Math.Clamp(MediaSidebarInfoMinimumFree, Config.MediaSidebarInfoMinimumFreeMinimum, Config.MediaSidebarInfoMinimumFreeMaximum),
        MediaSidebarInfoMaximumShare = Math.Clamp(MediaSidebarInfoMaximumShare, Config.MediaSidebarInfoMaximumShareMinimum, Config.MediaSidebarInfoMaximumShareMaximum),
        SidebarWidth = Math.Clamp(SidebarWidth, ReaderDisplayPreferences.MediaSidebarWidthMinimum, ReaderDisplayPreferences.MediaSidebarWidthMaximum),
        SidebarMinimumReaderWidth = Math.Clamp(SidebarMinimumReaderWidth, ReaderDisplayPreferences.MediaSidebarReaderWidthMinimum,
            ReaderDisplayPreferences.MediaSidebarReaderWidthMaximum),
        Video = Video.Clamped(),
        StripLimit = Math.Clamp(StripLimit, ReaderDisplayPreferences.MediaStripLimitMinimum, ReaderDisplayPreferences.MediaStripLimitMaximum),
        GridTextLimit = Math.Clamp(GridTextLimit, ReaderDisplayPreferences.MediaGridTextLimitMinimum, ReaderDisplayPreferences.MediaGridTextLimitMaximum)
    };

    /// <summary>What changes the posts' own images (and so rebuilds them): the focus and hover options act live.</summary>
    internal bool SamePostImages(ReaderMediaPreferences other) =>
        TextlessLarge == other.TextlessLarge && (!TextlessLarge || TextlessWidth == other.TextlessWidth)
        && MediaGrid == other.MediaGrid && (!MediaGrid || GridTextLimit == other.GridTextLimit);
}

internal enum ReaderDateStyle
{
    System = 0,
    Long = 1,
    SinceOp = 2,
    RelativeToNow = 3
}
