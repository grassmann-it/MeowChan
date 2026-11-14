using System;
using System.Collections.Generic;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The arithmetic of the gallery filmstrip (V3): which tiles of a long row are visible, which ones are decoded in advance, where the
/// row scrolls to so the shown medium is centred, and how the three settings are clamped. Pure (no Avalonia), so the plan for a list of
/// 5000 entries is tested without a display. The tiles are square with a fixed gap, so every position is a multiplication, never a
/// measure of an entry.
/// </summary>
internal static class GalleryFilmstripPlan
{
    /// <summary>Gap between two tiles in dip (a design constant, not a user limit).</summary>
    internal const double TileGap = 4;
    /// <summary>Vertical room above and below the tiles inside the band, in dip.</summary>
    internal const double BandPadding = 4;

    /// <summary>Where the band sits in the gallery's tree: over the picture (Top), first in the docked dock, or first in the floating footer.</summary>
    internal enum Host { Picture, Dock, OverlayFooter }

    /// <summary>
    /// The place of the band (GL1): which parent holds it, its margins (dip), how far the docked picture is pushed down, and whether
    /// the top edge of the viewer shows a hidden band (peek and edge zone; the bottom band comes and goes with the controls).
    /// </summary>
    internal readonly record struct Placement(Host Host, double Left, double Top, double Right, double Bottom, double PictureTopInset, bool PeeksAtTopEdge);

    /// <summary>Margin of the band inside the dock or the footer: the page margin of the toolbar below it, so both stand on one line.</summary>
    internal const double DockMargin = 8;

    /// <summary>
    /// The top margin of the bottom band. Docked it is the dock's first row and touches the picture as the video bar and the tools do
    /// (GL2: no gap under the picture); the Windows transparency fallback keeps the 8 dip below its full-width video strip. In the
    /// floating footer the band keeps its 8 dip from the picture it floats over.
    /// </summary>
    internal static double BottomBandTop(bool overlay, bool transparencyFallback) => overlay || transparencyFallback ? DockMargin : 0;

    /// <summary>Margin at the sides of the band over the picture (below the top row of the way back and the star).</summary>
    internal const double PictureMargin = 12;

    /// <summary>
    /// Where the band goes. <b>Bottom</b> (Tom's default, E1): the first child of the dock (docked) or of the floating footer (overlay), so
    /// it lies above the video bar and the tools, never touches the top row or the side panel, leaves the picture its whole height and
    /// has no peek. <b>Top</b>: under the way back and the star; docked it pushes the picture down by its band, as a floating strip it peeks
    /// in at the top edge. <paramref name="panelRight"/> is the width of an open side panel the top band must stop before.
    /// <paramref name="transparencyFallback"/>: the Windows fallback keeps the video bar under the picture, the dock starts 8 dip below it.
    /// </summary>
    internal static Placement Place(GalleryFilmstripDock dock, bool overlay, bool show, double tile, double topClearance, double panelRight,
        bool transparencyFallback = false)
    {
        if (dock == GalleryFilmstripDock.Bottom)
            return new(overlay ? Host.OverlayFooter : Host.Dock, DockMargin, BottomBandTop(overlay, transparencyFallback), DockMargin, 0, 0, false);
        double inset = show && !overlay ? topClearance + BandHeight(tile) + 2 : 0;
        return new(Host.Picture, PictureMargin, topClearance, PictureMargin + Math.Max(0, panelRight), 0, inset, overlay);
    }

    /// <summary>A closed range of tile indices; <see cref="Empty"/> when it holds none.</summary>
    internal readonly record struct TileRange(int First, int Last)
    {
        internal static TileRange Empty { get; } = new(0, -1);
        internal bool IsEmpty => Last < First;
        internal int Count => IsEmpty ? 0 : Last - First + 1;
        internal bool Contains(int index) => index >= First && index <= Last;
    }

    internal static int ClampHeight(int height) =>
        Math.Clamp(height, Config.GalleryFilmstripHeightMinimum, Config.GalleryFilmstripHeightMaximum);

    internal static int ClampPreload(int preload) =>
        Math.Clamp(preload, Config.GalleryFilmstripPreloadMinimum, Config.GalleryFilmstripPreloadMaximum);

    /// <summary>The distance between the left edges of two neighbouring tiles.</summary>
    internal static double Pitch(double tile) => Math.Max(1, tile) + TileGap;

    /// <summary>Height of the band for tiles of <paramref name="tile"/> dip.</summary>
    internal static double BandHeight(double tile) => Math.Max(1, tile) + 2 * BandPadding;

    /// <summary>The width of the whole row of <paramref name="count"/> tiles.</summary>
    internal static double Extent(int count, double tile) => count <= 0 ? 0 : count * Pitch(tile) - TileGap;

    /// <summary>The furthest the row can scroll: its extent less the viewport, never below zero.</summary>
    internal static double MaximumOffset(int count, double tile, double viewport) => Math.Max(0, Extent(count, tile) - Math.Max(0, viewport));

    internal static double ClampOffset(double offset, int count, double tile, double viewport) =>
        double.IsFinite(offset) ? Math.Clamp(offset, 0, MaximumOffset(count, tile, viewport)) : 0;

    /// <summary>The tiles that are at least partly inside a viewport of <paramref name="viewport"/> dip scrolled by <paramref name="offset"/>.</summary>
    internal static TileRange Visible(double offset, double viewport, double tile, int count)
    {
        if (count <= 0 || viewport <= 0 || !double.IsFinite(offset) || !double.IsFinite(viewport)) return TileRange.Empty;
        double pitch = Pitch(tile);
        double start = Math.Max(0, offset);
        int first = (int)Math.Floor(start / pitch);
        // A tile ends TileGap before its pitch ends: the gap past the last pixel of a tile is not part of it.
        if (first < count && first * pitch + tile <= start) first++;
        int last = (int)Math.Floor((start + viewport - 0.001) / pitch);
        first = Math.Clamp(first, 0, count);
        last = Math.Min(last, count - 1);
        return last < first ? TileRange.Empty : new(first, last);
    }

    /// <summary>The tiles whose thumbnails are kept: the visible ones and <paramref name="preload"/> on each side, within the list.</summary>
    internal static TileRange Decode(TileRange visible, int preload, int count)
    {
        if (visible.IsEmpty || count <= 0) return TileRange.Empty;
        preload = ClampPreload(preload);
        return new(Math.Max(0, visible.First - preload), Math.Min(count - 1, visible.Last + preload));
    }

    /// <summary>
    /// The indices of <paramref name="range"/> from the one nearest <paramref name="centre"/> outwards, the nearer side first on a tie:
    /// what the user is about to look at is decoded before what lies further away.
    /// </summary>
    internal static IEnumerable<int> OutwardOrder(TileRange range, int centre)
    {
        if (range.IsEmpty) yield break;
        centre = Math.Clamp(centre, range.First, range.Last);
        yield return centre;
        for (int distance = 1; centre - distance >= range.First || centre + distance <= range.Last; distance++)
        {
            if (centre - distance >= range.First) yield return centre - distance;
            if (centre + distance <= range.Last) yield return centre + distance;
        }
    }

    /// <summary>The offset that centres tile <paramref name="index"/> in the viewport, clamped to what the row can scroll.</summary>
    internal static double CentreOffset(int index, int count, double tile, double viewport)
    {
        if (count <= 0) return 0;
        index = Math.Clamp(index, 0, count - 1);
        double centre = index * Pitch(tile) + tile / 2;
        return ClampOffset(centre - viewport / 2, count, tile, viewport);
    }

    /// <summary>The nearest offset that brings the whole of tile <paramref name="index"/> into view (unchanged when it already is).</summary>
    internal static double RevealOffset(double offset, int index, int count, double tile, double viewport)
    {
        if (count <= 0) return 0;
        index = Math.Clamp(index, 0, count - 1);
        double left = index * Pitch(tile), right = left + tile;
        if (left < offset) offset = left;
        else if (right > offset + viewport) offset = right - viewport;
        return ClampOffset(offset, count, tile, viewport);
    }

    /// <summary>The offset one page further (<paramref name="direction"/> 1) or back (-1): a viewport less one tile, so one tile stays as a landmark.</summary>
    internal static double PageOffset(double offset, int direction, int count, double tile, double viewport)
    {
        double page = Math.Max(Pitch(tile), viewport - Pitch(tile));
        return ClampOffset(offset + Math.Sign(direction) * page, count, tile, viewport);
    }

    /// <summary>The tile under the x position of the row (in the viewport's coordinates), or -1 over a gap or past the ends.</summary>
    internal static int TileAt(double x, double offset, double tile, int count)
    {
        if (count <= 0 || !double.IsFinite(x)) return -1;
        double position = x + offset;
        if (position < 0) return -1;
        double pitch = Pitch(tile);
        int index = (int)Math.Floor(position / pitch);
        return index < count && position - index * pitch < tile ? index : -1;
    }
}
