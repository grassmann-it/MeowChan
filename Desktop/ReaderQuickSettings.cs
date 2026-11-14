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
    };

    internal static readonly QuickSettingsSpec Spec = new(QuickSettingsSurfaces.Reader, "quicksettings.reader.title", "quicksettings.reader.all", Fields);
}
