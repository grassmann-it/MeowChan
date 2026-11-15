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
    internal const string ReadingHistoryLimit = nameof(ReadingHistoryLimit);
    internal const string ReadingHistoryMenuEntries = nameof(ReadingHistoryMenuEntries);
    internal const string ReaderTextlessLargeImages = nameof(ReaderTextlessLargeImages);
    internal const string ReaderTextlessMediaWidth = nameof(ReaderTextlessMediaWidth);
    internal const string ReaderFocusedMediaEnlarge = nameof(ReaderFocusedMediaEnlarge);
    internal const string ReaderFocusedMediaScalePercent = nameof(ReaderFocusedMediaScalePercent);
    internal const string ReaderHoverEnlarge = nameof(ReaderHoverEnlarge);
    internal const string ReaderHoverEnlargeSize = nameof(ReaderHoverEnlargeSize);
    internal const string ReaderHoverEnlargeDelayMs = nameof(ReaderHoverEnlargeDelayMs);
    internal const string ReaderMediaSidebar = nameof(ReaderMediaSidebar);
    internal const string ReaderMediaSidebarScope = nameof(ReaderMediaSidebarScope);
    internal const string ReaderMediaSidebarSide = nameof(ReaderMediaSidebarSide);
    internal const string ReaderMediaSidebarWidth = nameof(ReaderMediaSidebarWidth);
    internal const string ReaderMediaSidebarMinimumWidth = nameof(ReaderMediaSidebarMinimumWidth);
    internal const string ReaderMediaClick = nameof(ReaderMediaClick);
    internal const string ReaderInlineVideoAutoplayMuted = nameof(ReaderInlineVideoAutoplayMuted);
    internal const string ReaderKeepImagesExpanded = nameof(ReaderKeepImagesExpanded);
    /// <summary>The three switches of "Videos in the media sidebar" (the one viewer shared by the reader, the archive view and the Media page's panel).</summary>
    internal const string ReaderSidebarVideoAutoplay = nameof(ReaderSidebarVideoAutoplay);
    internal const string ReaderSidebarVideoLoop = nameof(ReaderSidebarVideoLoop);
    internal const string ReaderSidebarVideoStartMuted = nameof(ReaderSidebarVideoStartMuted);
    /// <summary>"Start videos muted" (the gallery's own setting; videos in the reader's posts follow it).</summary>
    internal const string VideoStartMuted = nameof(VideoStartMuted);
    /// <summary>The remembered video volume (the gallery's own setting; the reader's inline videos use it too).</summary>
    internal const string VideoVolume = nameof(VideoVolume);
    /// <summary>"Repeat video" (the gallery's own setting; the reader's inline videos follow it).</summary>
    internal const string GalleryVideoRepeat = nameof(GalleryVideoRepeat);
    internal const string ReaderInlineVideoMaxPlayers = nameof(ReaderInlineVideoMaxPlayers);
    internal const string ReaderInlineVideoMaxFileMiB = nameof(ReaderInlineVideoMaxFileMiB);
    internal const string ReaderMediaStripLimit = nameof(ReaderMediaStripLimit);
    internal const string ReaderMediaGrid = nameof(ReaderMediaGrid);
    internal const string ReaderMediaGridTextLimit = nameof(ReaderMediaGridTextLimit);

    /// <summary>The "Media folders" group of Storage and limits (a group, not an input: the Plugins page links to it).</summary>
    internal const string MediaFolders = nameof(MediaFolders);

    /// <summary>The suffix of an input control's name on the settings page.</summary>
    internal const string InputSuffix = "Input";

    /// <summary>The name of the input control that edits the setting <paramref name="id"/> on the settings page.</summary>
    internal static string InputName(string id) => id + InputSuffix;

    /// <summary>The id of the setting an input control name stands for ("ReaderPageSizeInput" gives "ReaderPageSize"); other names are returned as they are.</summary>
    internal static string? FromInputName(string? name) =>
        name != null && name.EndsWith(InputSuffix, System.StringComparison.Ordinal) && name.Length > InputSuffix.Length
            ? name[..^InputSuffix.Length] : name;
}
