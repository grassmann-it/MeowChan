using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
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

    /// <summary>
    /// <see cref="UseGrid(ReaderMediaPreferences, int, string?)"/> for a media column of <paramref name="columnWidth"/>: also only when two tiles
    /// fit into it. A reader block is never narrower than <see cref="MediaSlotGeometry.MinimumBlockWidth"/> (the file line keeps that), so a
    /// tile below it would not be half the column and the "grid" would silently be one column; the images then keep their ordinary width.
    /// </summary>
    internal static bool UseGrid(ReaderMediaPreferences media, int attachmentCount, string? text, int columnWidth) =>
        UseGrid(media, attachmentCount, text) && TilesFit(columnWidth);

    /// <summary>Whether two tiles and the gap fit into a media column of <paramref name="columnWidth"/> without a tile going below the block minimum.</summary>
    internal static bool TilesFit(int columnWidth) => TileWidth(columnWidth) >= MediaSlotGeometry.MinimumBlockWidth;

    /// <summary>The width of one tile of the grid inside a media column of <paramref name="columnWidth"/> (two tiles and the gap fill it).</summary>
    internal static int TileWidth(int columnWidth) => Math.Max(1, (int)Math.Floor((Math.Max(1, columnWidth) - GridSpacing) / GridColumns));

    /// <summary>
    /// Where the tiles of a post's media column go (the pure rule of <see cref="ReaderMediaPanel"/>). With the grid they wrap into lines
    /// (<see cref="GridSpacing"/> between two tiles of a line, a line as high as its highest tile); without it every tile has a line of its own,
    /// as a vertical stack: a narrow portrait picture never takes the place beside it that the media column has left over.
    /// </summary>
    internal static Rect[] Place(IReadOnlyList<Size> tiles, double availableWidth, bool grid)
    {
        Rect[] places = new Rect[tiles.Count];
        double x = 0, y = 0, lineHeight = 0;
        int lineStart = 0;
        for (int i = 0; i < tiles.Count; i++)
        {
            Size tile = tiles[i];
            if (i > lineStart && (!grid || x + tile.Width > availableWidth + 0.01))
            {
                for (int k = lineStart; k < i; k++) places[k] = places[k].WithHeight(lineHeight);
                y += lineHeight;
                x = 0; lineHeight = 0; lineStart = i;
            }
            places[i] = new Rect(x, y, tile.Width, tile.Height);
            x += tile.Width + GridSpacing;
            lineHeight = Math.Max(lineHeight, tile.Height);
        }
        for (int k = lineStart; k < tiles.Count; k++) places[k] = places[k].WithHeight(lineHeight);
        return places;
    }

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
    /// enough for it (an unknown width counts as wide enough). It names the shortcut the user currently has for it.
    /// </summary>
    internal static string? DiscoveryHint(ReaderMediaPreferences media, bool largeReader, double readerWidth, string? shortcut)
    {
        if (media.Sidebar || !largeReader) return null;
        if (ReaderMediaSidebarRules.IsCollapsedByWidth(media, readerWidth)) return null;
        return string.IsNullOrWhiteSpace(shortcut) ? Texts.T("reader.sidebar.discoverNoKey") : Texts.T("reader.sidebar.discover", shortcut);
    }
}

/// <summary>
/// The panel of a post's media column (R11): the tiles wrap into a grid of two columns only for a post that is laid out as a grid
/// (<see cref="ArchivePostRow.MediaGrid"/>, read from the data context of the post), else every tile stands on its own line as before.
/// A wrap panel for all posts would put narrow portrait images side by side in posts without a grid (long text, grid switched off).
/// </summary>
public sealed class ReaderMediaPanel : Panel
{
    public static readonly StyledProperty<bool> IsGridProperty = AvaloniaProperty.Register<ReaderMediaPanel, bool>(nameof(IsGrid));

    static ReaderMediaPanel() => AffectsMeasure<ReaderMediaPanel>(IsGridProperty);

    /// <summary>Wrap into lines (the post is a grid) instead of one tile per line.</summary>
    public bool IsGrid { get => GetValue(IsGridProperty); set => SetValue(IsGridProperty, value); }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        IsGrid = DataContext is ArchivePostRow { MediaGrid: true };
    }

    private Rect[] PlaceChildren(double width, bool measuring)
    {
        List<Size> sizes = new(Children.Count);
        foreach (Control child in Children)
        {
            if (measuring) child.Measure(new Size(width, double.PositiveInfinity));
            sizes.Add(child.IsVisible ? child.DesiredSize : default);
        }
        return ReaderMediaMultiRules.Place(sizes, width, IsGrid);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Rect[] places = PlaceChildren(availableSize.Width, measuring: true);
        double width = 0, height = 0;
        foreach (Rect place in places) { width = Math.Max(width, place.Right); height = Math.Max(height, place.Bottom); }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Rect[] places = PlaceChildren(finalSize.Width, measuring: false);
        for (int i = 0; i < places.Length; i++)
        {
            Rect place = places[i];
            // A stacked tile gets the full width like a vertical stack panel gives it; a wrapped one keeps its own.
            Children[i].Arrange(IsGrid ? place : place.WithWidth(Math.Max(finalSize.Width, place.Width)));
        }
        return finalSize;
    }
}
