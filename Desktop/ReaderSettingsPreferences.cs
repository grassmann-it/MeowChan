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
        reader.ReaderFoldLinesPreference = config.ReaderFoldLines;
        reader.ReaderSpoilerModePreference = Spoilers(config);
        reader.QuotePreviewMaxLines = config.ReaderPreviewLines;
        reader.QuotePreviewDelayMilliseconds = config.ReaderPreviewDelayMs;
        reader.InlineReplyPageSize = config.ReaderReplyStep;
        reader.ReplyCardMaxLines = config.ReaderReplyLines;
        reader.ReaderThreadingDepth = config.ReaderThreadingDepth;
        reader.ApplyReaderDisplayPreferences(config.ReaderThumbnailWidth, DateStyle(config), config.ReaderPageSize);
    }
}
