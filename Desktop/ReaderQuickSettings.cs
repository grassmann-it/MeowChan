using System.Collections.Generic;
using static MeowChan.Desktop.QuickSetting;

namespace MeowChan.Desktop;

/// <summary>
/// The reader's quick settings popover (View menu, "Reader settings…"): the display options readers reach for most often. Each
/// field is the same setting as on the settings page, under the same id, label and limits; the popover only offers it closer.
/// <para>
/// A setting joins the popover with one more line in <see cref="Fields"/>: its <see cref="SettingsFieldId"/> (the name of its input
/// on the settings page without "Input"), the label and hint keys the page already uses, and how to read and write it in
/// <see cref="Config"/>. Order below is display order. The settings search finds the field through the page, as before.
/// </para>
/// </summary>
internal static class ReaderQuickSettings
{
    internal static readonly IReadOnlyList<QuickSettingField> Fields = new[]
    {
        Choice(SettingsFieldId.ReaderPostWidth, "reader.postWidth", "reader.postWidth.hint", Options((Config.ReaderPostWidthFit, "reader.postWidth.fit"), (Config.ReaderPostWidthFull, "reader.postWidth.full"), (Config.ReaderPostWidthColumn, "reader.postWidth.column")), c => c.ReaderPostWidth, (c, v) => c.ReaderPostWidth = v),
        Integer(SettingsFieldId.ReaderThumbnailWidth, "settings.reader.thumbnailWidth", "settings.reader.thumbnailWidth.hint", Config.ReaderThumbnailWidthMinimum, Config.ReaderThumbnailWidthMaximum, 10, c => c.ReaderThumbnailWidth, (c, v) => c.ReaderThumbnailWidth = v),
        Choice(SettingsFieldId.ReadingFontSize, "r08d.settings.readingFontSize", null, Options(("Small", "r08d.settings.fontSmall"), ("Normal", "r08d.settings.fontNormal"), ("Large", "r08d.settings.fontLarge")), c => c.ReadingFontSize, (c, v) => c.ReadingFontSize = v),
        Choice(SettingsFieldId.ReaderDateStyle, "settings.reader.dateStyle", "settings.reader.dateStyle.hint", Options(("System", "settings.reader.dateStyle.system"), ("Long", "settings.reader.dateStyle.long"), ("SinceOp", "settings.reader.dateStyle.sinceOp"), ("RelativeToNow", "settings.reader.dateStyle.relative")), c => c.ReaderDateStyle, (c, v) => c.ReaderDateStyle = v),
        Choice(SettingsFieldId.ReaderSpoilers, "settings.reader.spoilers", "settings.reader.spoilers.hint", Options(("Hide", "settings.reader.spoilers.hide"), ("Hover", "settings.reader.spoilers.hover"), ("Always", "settings.reader.spoilers.always")), c => c.ReaderSpoilers, (c, v) => c.ReaderSpoilers = v),
        Integer(SettingsFieldId.ReaderPageSize, "settings.reader.pageSize", "settings.reader.pageSize.hint", Config.ReaderPageSizeMinimum, Config.ReaderPageSizeMaximum, 10, c => c.ReaderPageSize, (c, v) => c.ReaderPageSize = v),
        // R5/R6: the reading history and the reader media sizes, same ids and steps as on the settings page.
        Integer(SettingsFieldId.ReadingHistoryLimit, "settings.reader.historyLimit", "settings.reader.historyLimit.hint", Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum, 1, c => c.ReadingHistoryLimit, (c, v) => c.ReadingHistoryLimit = v),
        Integer(SettingsFieldId.ReadingHistoryMenuEntries, "settings.reader.historyMenuEntries", "settings.reader.historyMenuEntries.hint", Config.ReadingHistoryMenuEntriesMinimum, Config.ReadingHistoryMenuEntriesMaximum, 1, c => c.ReadingHistoryMenuEntries, (c, v) => c.ReadingHistoryMenuEntries = v),
        Toggle(SettingsFieldId.ReaderTextlessLargeImages, "settings.reader.textlessLarge", "settings.reader.textlessLarge.hint", c => c.ReaderTextlessLargeImages, (c, v) => c.ReaderTextlessLargeImages = v),
        Integer(SettingsFieldId.ReaderTextlessMediaWidth, "settings.reader.textlessWidth", "settings.reader.textlessWidth.hint", Config.ReaderTextlessMediaWidthMinimum, Config.ReaderTextlessMediaWidthMaximum, 20, c => c.ReaderTextlessMediaWidth, (c, v) => c.ReaderTextlessMediaWidth = v),
        Toggle(SettingsFieldId.ReaderFocusedMediaEnlarge, "settings.reader.focusedEnlarge", "settings.reader.focusedEnlarge.hint", c => c.ReaderFocusedMediaEnlarge, (c, v) => c.ReaderFocusedMediaEnlarge = v),
        Integer(SettingsFieldId.ReaderFocusedMediaScalePercent, "settings.reader.focusedScale", "settings.reader.focusedScale.hint", Config.ReaderFocusedMediaScalePercentMinimum, Config.ReaderFocusedMediaScalePercentMaximum, 25, c => c.ReaderFocusedMediaScalePercent, (c, v) => c.ReaderFocusedMediaScalePercent = v),
        Toggle(SettingsFieldId.ReaderHoverEnlarge, "settings.reader.hoverEnlarge", "settings.reader.hoverEnlarge.hint", c => c.ReaderHoverEnlarge, (c, v) => c.ReaderHoverEnlarge = v),
        Integer(SettingsFieldId.ReaderHoverEnlargeSize, "settings.reader.hoverSize", "settings.reader.hoverSize.hint", Config.ReaderHoverEnlargeSizeMinimum, Config.ReaderHoverEnlargeSizeMaximum, 40, c => c.ReaderHoverEnlargeSize, (c, v) => c.ReaderHoverEnlargeSize = v),
        Integer(SettingsFieldId.ReaderHoverEnlargeDelayMs, "settings.reader.hoverDelay", "settings.reader.hoverDelay.hint", Config.ReaderHoverEnlargeDelayMsMinimum, Config.ReaderHoverEnlargeDelayMsMaximum, 50, c => c.ReaderHoverEnlargeDelayMs, (c, v) => c.ReaderHoverEnlargeDelayMs = v),
        // R8: the media sidebar of the large reader.
        Toggle(SettingsFieldId.ReaderMediaSidebar, "settings.reader.mediaSidebar", "settings.reader.mediaSidebar.hint", c => c.ReaderMediaSidebar, (c, v) => c.ReaderMediaSidebar = v),
        Choice(SettingsFieldId.ReaderMediaSidebarSide, "settings.reader.mediaSidebarSide", "settings.reader.mediaSidebarSide.hint", Options((Config.ReaderMediaSidebarSideLeft, "settings.reader.mediaSidebarSide.left"), (Config.ReaderMediaSidebarSideRight, "settings.reader.mediaSidebarSide.right")), c => c.ReaderMediaSidebarSide, (c, v) => c.ReaderMediaSidebarSide = v),
        Integer(SettingsFieldId.ReaderMediaSidebarWidth, "settings.reader.mediaSidebarWidth", "settings.reader.mediaSidebarWidth.hint", Config.ReaderMediaSidebarWidthMinimum, Config.ReaderMediaSidebarWidthMaximum, 40, c => c.ReaderMediaSidebarWidth, (c, v) => c.ReaderMediaSidebarWidth = v),
        Integer(SettingsFieldId.ReaderMediaSidebarMinimumWidth, "settings.reader.mediaSidebarMinimumWidth", "settings.reader.mediaSidebarMinimumWidth.hint", Config.ReaderMediaSidebarMinimumWidthMinimum, Config.ReaderMediaSidebarMinimumWidthMaximum, 80, c => c.ReaderMediaSidebarMinimumWidth, (c, v) => c.ReaderMediaSidebarMinimumWidth = v),
        // R10: inline video in reader posts and the media sidebar.
        Toggle(SettingsFieldId.ReaderInlineVideo, "settings.reader.inlineVideo", "settings.reader.inlineVideo.hint", c => c.ReaderInlineVideo, (c, v) => c.ReaderInlineVideo = v),
        Toggle(SettingsFieldId.ReaderInlineVideoAutoplayMuted, "settings.reader.inlineVideoAutoplay", "settings.reader.inlineVideoAutoplay.hint", c => c.ReaderInlineVideoAutoplayMuted, (c, v) => c.ReaderInlineVideoAutoplayMuted = v),
        Integer(SettingsFieldId.ReaderInlineVideoMaxPlayers, "settings.reader.inlineVideoPlayers", "settings.reader.inlineVideoPlayers.hint", Config.ReaderInlineVideoMaxPlayersMinimum, Config.ReaderInlineVideoMaxPlayersMaximum, 1, c => c.ReaderInlineVideoMaxPlayers, (c, v) => c.ReaderInlineVideoMaxPlayers = v),
        Integer(SettingsFieldId.ReaderInlineVideoMaxFileMiB, "settings.reader.inlineVideoMaxFile", "settings.reader.inlineVideoMaxFile.hint", Config.ReaderInlineVideoMaxFileMiBMinimum, Config.ReaderInlineVideoMaxFileMiBMaximum, 64, c => c.ReaderInlineVideoMaxFileMiB, (c, v) => c.ReaderInlineVideoMaxFileMiB = v),
        // R11: posts with several files.
        Integer(SettingsFieldId.ReaderMediaStripLimit, "settings.reader.mediaStripLimit", "settings.reader.mediaStripLimit.hint", Config.ReaderMediaStripLimitMinimum, Config.ReaderMediaStripLimitMaximum, 1, c => c.ReaderMediaStripLimit, (c, v) => c.ReaderMediaStripLimit = v),
        Toggle(SettingsFieldId.ReaderMediaGrid, "settings.reader.mediaGrid", "settings.reader.mediaGrid.hint", c => c.ReaderMediaGrid, (c, v) => c.ReaderMediaGrid = v),
        Integer(SettingsFieldId.ReaderMediaGridTextLimit, "settings.reader.mediaGridText", "settings.reader.mediaGridText.hint", Config.ReaderMediaGridTextLimitMinimum, Config.ReaderMediaGridTextLimitMaximum, 20, c => c.ReaderMediaGridTextLimit, (c, v) => c.ReaderMediaGridTextLimit = v),
    };

    internal static readonly QuickSettingsSpec Spec = new(QuickSettingsSurfaces.Reader, "quicksettings.reader.title", "quicksettings.reader.all", Fields);
}
