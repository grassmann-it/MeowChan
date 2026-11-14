namespace MeowChan.Desktop;

/// <summary>
/// The identity of a setting, shared by the settings page, its search and every quick settings popover. A setting is named by
/// the input control that edits it: the input <c>ReaderPostWidthInput</c> on the settings page has the id <c>ReaderPostWidth</c>
/// (the same key the settings search indexes and its aliases are written against). A popover field carries the id of the
/// setting it edits, so "All reader settings" can reveal the very control on the page, the search index needs no second list,
/// and a setting added to the page appears in a popover with one registration line (see <see cref="QuickSettingsSurfaces"/>).
/// </summary>
internal static class SettingsFieldId
{
    internal const string ReadingFontSize = nameof(ReadingFontSize);
    internal const string ReaderPostWidth = nameof(ReaderPostWidth);
    internal const string ReaderThumbnailWidth = nameof(ReaderThumbnailWidth);
    internal const string ReaderDateStyle = nameof(ReaderDateStyle);
    internal const string ReaderSpoilers = nameof(ReaderSpoilers);
    internal const string ReaderPageSize = nameof(ReaderPageSize);

    /// <summary>The suffix of an input control's name on the settings page.</summary>
    internal const string InputSuffix = "Input";

    /// <summary>The name of the input control that edits the setting <paramref name="id"/> on the settings page.</summary>
    internal static string InputName(string id) => id + InputSuffix;

    /// <summary>The id of the setting an input control name stands for ("ReaderPageSizeInput" gives "ReaderPageSize"); other names are returned as they are.</summary>
    internal static string? FromInputName(string? name) =>
        name != null && name.EndsWith(InputSuffix, System.StringComparison.Ordinal) && name.Length > InputSuffix.Length
            ? name[..^InputSuffix.Length] : name;
}
