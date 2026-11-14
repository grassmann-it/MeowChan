using System;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// Posts with several files (R11), the rules that need no window: the two-column grid of the inline layout, the sidebar's thumbnail
/// strip window, and the hint that makes the sidebar discoverable.
/// </summary>
internal static class ReaderMediaMultiRules
{
    /// <summary>The gap between two tiles of the grid (layout constant, also the item spacing of the wrap panel).</summary>
    internal const double GridSpacing = 8;
    /// <summary>The grid has two columns; a third file starts the next row.</summary>
    internal const int GridColumns = 2;

    /// <summary>
    /// Whether the images of a post are shown as a grid: the option is on, the post has several images, and its text (trimmed) is at most
    /// <see cref="ReaderMediaPreferences.GridTextLimit"/> characters. Hidden media and stubs never get here (the caller passes 0 files).
    /// </summary>
    internal static bool UseGrid(ReaderMediaPreferences media, int attachmentCount, string? text) =>
        media.MediaGrid && attachmentCount >= GridColumns
        && (text ?? string.Empty).AsSpan().Trim().Length <= Math.Clamp(media.GridTextLimit,
            ReaderDisplayPreferences.MediaGridTextLimitMinimum, ReaderDisplayPreferences.MediaGridTextLimitMaximum);

    /// <summary>The width of one tile of the grid inside a media column of <paramref name="columnWidth"/> (two tiles and the gap fill it).</summary>
    internal static int TileWidth(int columnWidth) => Math.Max(1, (int)Math.Floor((Math.Max(1, columnWidth) - GridSpacing) / GridColumns));

    // ---- the sidebar's thumbnail strip ---------------------------------------------------------------------------------------------

    /// <summary>One thumbnail of the strip, in logical pixels.</summary>
    internal const double ThumbnailWidth = 64, ThumbnailHeight = 48, ThumbnailSpacing = 4;
    /// <summary>The decode bounds of a strip thumbnail (about twice its size; a kept preview of this size is shared through the thumbnail cache).</summary>
    internal const int ThumbnailDecodeWidth = 128, ThumbnailDecodeHeight = 96;

    /// <summary>The strip appears for a post with more than one shown file.</summary>
    internal static bool ShowsStrip(int count) => count > 1;

    /// <summary>
    /// The files the strip shows: at most <paramref name="limit"/> in a row. A larger post slides the window along; while the current file
    /// stays inside the previous window it does not move (stepping through does not make the thumbnails jump).
    /// </summary>
    internal static (int Start, int Length) Window(int current, int count, int limit, int previousStart = -1)
    {
        if (count <= 0) return (0, 0);
        int length = Math.Min(count, Math.Clamp(limit, ReaderDisplayPreferences.MediaStripLimitMinimum, ReaderDisplayPreferences.MediaStripLimitMaximum));
        current = ReaderMediaSidebarRules.ClampIndex(current, count);
        int last = count - length;
        if (previousStart >= 0 && previousStart <= last && current >= previousStart && current < previousStart + length) return (previousStart, length);
        return (Math.Clamp(current - length / 2, 0, last), length);
    }

    /// <summary>The file a strip key moves to from <paramref name="current"/>: ← → one file, Home and End the ends; the index stays within the post.</summary>
    internal static int KeyTarget(int current, int count, Avalonia.Input.Key key) => ReaderMediaSidebarRules.ClampIndex(key switch
    {
        Avalonia.Input.Key.Left => current - 1,
        Avalonia.Input.Key.Right => current + 1,
        Avalonia.Input.Key.Home => 0,
        Avalonia.Input.Key.End => count - 1,
        _ => current
    }, count);

    /// <summary>"2 of 4", the counter next to the strip.</summary>
    internal static string CounterText(int current, int count) =>
        Texts.T("reader.sidebar.position", ReaderMediaSidebarRules.ClampIndex(current, count) + 1, count);

    /// <summary>The accessible name of one thumbnail: its position and the file name.</summary>
    internal static string ItemName(int index, int count, string name) =>
        Texts.T("reader.sidebar.stripItem", index + 1, count, name.Length > 0 ? name : "—");

    // ---- discoverability -----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The hint on the View menu's sidebar entry while the sidebar is off: only in the large reader, and only when the reader is wide
    /// enough for it (an unknown width counts as wide enough). It names the shortcut the user currently has for it. Nothing turns on by itself.
    /// </summary>
    internal static string? DiscoveryHint(ReaderMediaPreferences media, bool largeReader, double readerWidth, string? shortcut)
    {
        if (media.Sidebar || !largeReader) return null;
        if (double.IsFinite(readerWidth) && readerWidth > 0 && readerWidth < media.Clamped().SidebarMinimumReaderWidth) return null;
        return string.IsNullOrWhiteSpace(shortcut) ? Texts.T("reader.sidebar.discoverNoKey") : Texts.T("reader.sidebar.discover", shortcut);
    }
}
