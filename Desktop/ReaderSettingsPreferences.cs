using System;

namespace MeowChan.Desktop;

internal static class ReaderSettingsPreferences
{
    internal static ReaderDateStyle DateStyle(Config config) => Enum.TryParse(config.ReaderDateStyle, out ReaderDateStyle style) && Enum.IsDefined(style)
        ? style : ReaderDateStyle.System;
    internal static ReaderSpoilerMode Spoilers(Config config) => Enum.TryParse(config.ReaderSpoilers, out ReaderSpoilerMode mode) && Enum.IsDefined(mode)
        ? mode : ReaderSpoilerMode.Hide;

    internal static GalleryFitPreferences Gallery(Config config) => new(config.GalleryFitUpscaleLimited,
        GalleryFitPreferences.ClampPercent(config.GalleryFitMaxUpscalePercent));

    internal static void Apply(ArchivePage reader, Config config)
    {
        // Capture the original reader place before any of the inherited layout preferences are updated.
        reader.ApplyReaderDisplayPreferences(config.ReaderThumbnailWidth, DateStyle(config), config.ReaderPageSize,
            config.ReaderThreadingDepth, ReaderMediaPreferences.From(config));
        reader.ReaderFoldLinesPreference = config.ReaderFoldLines;
        reader.ReaderSpoilerModePreference = Spoilers(config);
        reader.QuotePreviewMaxLines = config.ReaderPreviewLines;
        reader.QuotePreviewDelayMilliseconds = config.ReaderPreviewDelayMs;
        reader.QuotePreviewVideoAutoplay = config.ReaderQuotePreviewAutoplay;
        reader.QuotePreviewVideoMuted = config.ReaderQuotePreviewMuted;
        reader.InlineReplyPageSize = config.ReaderReplyStep;
        reader.ReplyCardMaxLines = config.ReaderReplyLines;
        reader.ReaderExpandRepliesPreference = config.ReaderExpandReplies;
        reader.ReaderMediaSidebarFit = Gallery(config);
        ReaderPreviewAspectMemo.Shared.Capacity = config.ReaderPreviewSizeMemory;
        reader.ReaderPreviewPrefetchPosts = config.ReaderPreviewPrefetchPosts;
    }
}
