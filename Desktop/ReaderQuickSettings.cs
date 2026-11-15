using System;
using System.Collections.Generic;
using System.Linq;
using static MeowChan.Desktop.QuickSetting;

namespace MeowChan.Desktop;

/// <summary>
/// The reader's settings popover (the gear "Settings" of the reader toolbar, RL1): the display options readers reach for most often, in
/// four sections: the view of this session (switches of the open view, not saved), media, text and layout, and the thread. Each setting
/// is the same setting as on the settings page, under the same id, label and limits; the popover only offers it closer.
/// <para>
/// A setting joins the popover in two steps: its line in <see cref="Catalog"/> (its <see cref="SettingsFieldId"/>, the name of its input
/// on the settings page without "Input"; the label and hint keys the page already uses; how to read and write it in <see cref="Config"/>),
/// and its place in <see cref="Layout"/> (section, a condition on another on/off setting, the key of its command). Whatever is only in the
/// catalog is reached on the settings page (the popover's last line goes there). The settings search finds every field through the page.
/// </para>
/// </summary>
internal static class ReaderQuickSettings
{
    // Every setting the popover can hold (the display order is Layout's).
    private static readonly IReadOnlyList<QuickSettingField> Catalog = new[]
    {
        // The choice behind a click on a medium (merged with the old inline video switch), first because it is the one readers reach for.
        Choice(SettingsFieldId.ReaderMediaClick, "settings.reader.mediaClick", "settings.reader.mediaClick.hint", Options((ReaderMediaClickRules.SidebarKey, "settings.reader.mediaClick.sidebar"), (ReaderMediaClickRules.PostKey, "settings.reader.mediaClick.post"), (ReaderMediaClickRules.GalleryKey, "settings.reader.mediaClick.gallery")), c => ReaderMediaClickRules.ToKey(c.EffectiveReaderMediaClick), (c, v) => c.ChooseReaderMediaClick(ReaderMediaClickRules.FromKey(v))),
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
        // R8: the media sidebar; with its scope the one choice Off / Large reader only / Always (also beside the posts of the archive view).
        Toggle(SettingsFieldId.ReaderMediaSidebar, "settings.reader.mediaSidebar", "settings.reader.mediaSidebar.hint", c => c.ReaderMediaSidebar, (c, v) => c.ReaderMediaSidebar = v),
        Choice(SettingsFieldId.ReaderMediaSidebarScope, "settings.reader.mediaSidebarScope", "settings.reader.mediaSidebarScope.hint", Options((Config.ReaderMediaSidebarScopeLargeReader, "settings.reader.mediaSidebarScope.large"), (Config.ReaderMediaSidebarScopeAlways, "settings.reader.mediaSidebarScope.always")), c => c.ReaderMediaSidebarScope, (c, v) => c.ReaderMediaSidebarScope = v),
        Choice(SettingsFieldId.ReaderMediaSidebarSide, "settings.reader.mediaSidebarSide", "settings.reader.mediaSidebarSide.hint", Options((Config.ReaderMediaSidebarSideLeft, "settings.reader.mediaSidebarSide.left"), (Config.ReaderMediaSidebarSideRight, "settings.reader.mediaSidebarSide.right")), c => c.ReaderMediaSidebarSide, (c, v) => c.ReaderMediaSidebarSide = v),
        Integer(SettingsFieldId.ReaderMediaSidebarWidth, "settings.reader.mediaSidebarWidth", "settings.reader.mediaSidebarWidth.hint", Config.ReaderMediaSidebarWidthMinimum, Config.ReaderMediaSidebarWidthMaximum, 40, c => c.ReaderMediaSidebarWidth, (c, v) => c.ReaderMediaSidebarWidth = v),
        Integer(SettingsFieldId.ReaderMediaSidebarMinimumWidth, "settings.reader.mediaSidebarMinimumWidth", "settings.reader.mediaSidebarMinimumWidth.hint", Config.ReaderMediaSidebarMinimumWidthMinimum, Config.ReaderMediaSidebarMinimumWidthMaximum, 80, c => c.ReaderMediaSidebarMinimumWidth, (c, v) => c.ReaderMediaSidebarMinimumWidth = v),
        // R10: the players of the reader (in the sidebar and in posts).
        Toggle(SettingsFieldId.ReaderInlineVideoAutoplayMuted, "settings.reader.inlineVideoAutoplay", "settings.reader.inlineVideoAutoplay.hint", c => c.ReaderInlineVideoAutoplayMuted, (c, v) => c.ReaderInlineVideoAutoplayMuted = v),
        Integer(SettingsFieldId.ReaderInlineVideoMaxPlayers, "settings.reader.inlineVideoPlayers", "settings.reader.inlineVideoPlayers.hint", Config.ReaderInlineVideoMaxPlayersMinimum, Config.ReaderInlineVideoMaxPlayersMaximum, 1, c => c.ReaderInlineVideoMaxPlayers, (c, v) => c.ReaderInlineVideoMaxPlayers = v),
        Integer(SettingsFieldId.ReaderInlineVideoMaxFileMiB, "settings.reader.inlineVideoMaxFile", "settings.reader.inlineVideoMaxFile.hint", Config.ReaderInlineVideoMaxFileMiBMinimum, Config.ReaderInlineVideoMaxFileMiBMaximum, 64, c => c.ReaderInlineVideoMaxFileMiB, (c, v) => c.ReaderInlineVideoMaxFileMiB = v),
        // R11: posts with several files.
        Integer(SettingsFieldId.ReaderMediaStripLimit, "settings.reader.mediaStripLimit", "settings.reader.mediaStripLimit.hint", Config.ReaderMediaStripLimitMinimum, Config.ReaderMediaStripLimitMaximum, 1, c => c.ReaderMediaStripLimit, (c, v) => c.ReaderMediaStripLimit = v),
        Toggle(SettingsFieldId.ReaderMediaGrid, "settings.reader.mediaGrid", "settings.reader.mediaGrid.hint", c => c.ReaderMediaGrid, (c, v) => c.ReaderMediaGrid = v),
        Integer(SettingsFieldId.ReaderMediaGridTextLimit, "settings.reader.mediaGridText", "settings.reader.mediaGridText.hint", Config.ReaderMediaGridTextLimitMinimum, Config.ReaderMediaGridTextLimitMaximum, 20, c => c.ReaderMediaGridTextLimit, (c, v) => c.ReaderMediaGridTextLimit = v),
    };

    /// <summary>The field of "Clicking a medium" (Config.ReaderMediaClick: Sidebar / Post / Gallery), the first row of the popover's Media section.</summary>
    internal const string MediaClickFieldId = SettingsFieldId.ReaderMediaClick;

    private static QuickSettingField Setting(string id) => Catalog.Single(field => string.Equals(field.Id, id, StringComparison.Ordinal));
    private static QuickSettingsFieldEntry Row(string id, string? when = null, string? shortcut = null) => new(Setting(id), when, shortcut);

    /// <summary>The popover's lines in order. Session rows are the commands of the open view (the former View menu); the rest are settings.</summary>
    internal static readonly IReadOnlyList<QuickSettingsEntry> Layout = new QuickSettingsEntry[]
    {
        new QuickSettingsSection("quicksettings.reader.section.view"),
        new QuickSettingsSessionEntry("reader.view.media-only"),
        new QuickSettingsSessionEntry("reader.view.hide-media"),
        new QuickSettingsSessionEntry("reader.view.conversation"),
        new QuickSettingsSessionEntry("reader.post-filters", IsAction: true),
        new QuickSettingsSection("quicksettings.reader.section.media"),
        // "Clicking a medium" (Config.ReaderMediaClick; Sidebar / Post / Gallery, feature/reader-inline-video-minimal) replaces the old inline video switch here.
        Row(MediaClickFieldId),
        Row(SettingsFieldId.ReaderMediaSidebar, shortcut: "reader.sidebar.toggle"),
        Row(SettingsFieldId.ReaderMediaSidebarScope, when: SettingsFieldId.ReaderMediaSidebar),
        Row(SettingsFieldId.ReaderMediaSidebarSide, when: SettingsFieldId.ReaderMediaSidebar),
        Row(SettingsFieldId.ReaderTextlessLargeImages, shortcut: "reader.view.textless-large"),
        Row(SettingsFieldId.ReaderTextlessMediaWidth, when: SettingsFieldId.ReaderTextlessLargeImages),
        Row(SettingsFieldId.ReaderFocusedMediaEnlarge, shortcut: "reader.view.focused-enlarge"),
        Row(SettingsFieldId.ReaderFocusedMediaScalePercent, when: SettingsFieldId.ReaderFocusedMediaEnlarge),
        Row(SettingsFieldId.ReaderHoverEnlarge, shortcut: "reader.view.hover-enlarge"),
        Row(SettingsFieldId.ReaderThumbnailWidth),
        new QuickSettingsSection("quicksettings.reader.section.text"),
        Row(SettingsFieldId.ReaderPostWidth),
        Row(SettingsFieldId.ReadingFontSize),
        Row(SettingsFieldId.ReaderDateStyle),
        Row(SettingsFieldId.ReaderSpoilers),
        Row(SettingsFieldId.ReaderPageSize),
        new QuickSettingsSection("quicksettings.reader.section.thread"),
        new QuickSettingsSessionEntry("watch.follow-live"),
    };

    /// <summary>The settings in the popover, in its order.</summary>
    internal static readonly IReadOnlyList<QuickSettingField> Fields = Layout.OfType<QuickSettingsFieldEntry>().Select(entry => entry.Field).ToArray();

    internal static readonly QuickSettingsSpec Spec = QuickSettingsSpec.WithLayout(QuickSettingsSurfaces.Reader, "quicksettings.reader.title", "quicksettings.reader.all", Layout);
}
