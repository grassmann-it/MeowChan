using System;
using System.Collections.Generic;
using Avalonia;

namespace MeowChan.Desktop;

/// <summary>
/// The pure geometry of the outline around a hidden spoiler (Light/Dark review 2026-11-13, item 8). A hidden spoiler is a
/// run that paints its text in its own surface colour; that area is only 1.10:1 (Light) and 1.09:1 (Dark) against the active
/// card, and a run background cannot carry a border. <see cref="ReaderBodyTextBlock"/> therefore draws a 1-dip interactive
/// outline (<c>Workspace.ControlBorder</c>, at least 3:1 on every post surface) over the areas these helpers compute.
/// </summary>
internal static class ReaderSpoilerOutline
{
    // Runs whose edges are this close (in dip) belong to one area: a spoiler cut by a link or a reference stays one box.
    private const double Touch = 0.5;

    /// <summary>
    /// The spoiler character ranges of a body, adjacent spoiler segments joined: a spoiler that a link, a reference or a
    /// style change splits into several segments is still one area. Fills <paramref name="ranges"/> (cleared first).
    /// </summary>
    internal static void Ranges(ReaderBodyDocument? document, List<(int Start, int End)> ranges)
    {
        ranges.Clear();
        if (document is not { IsFormatted: true }) return;
        foreach (ReaderBodySegment segment in document.Segments)
        {
            if (!segment.IsSpoiler || segment.Length <= 0) continue;
            if (ranges.Count > 0 && ranges[^1].End == segment.Start) ranges[^1] = (ranges[^1].Start, segment.End);
            else ranges.Add((segment.Start, segment.End));
        }
    }

    /// <summary>
    /// Adds the painted area of one spoiler run to the areas of its line (those at <paramref name="lineFirst"/> and after):
    /// areas that touch or overlap horizontally become one box, so a spoiler cut into several runs gets one outline per line.
    /// </summary>
    internal static void Add(List<Rect> areas, int lineFirst, Rect area)
    {
        if (area.Width < Touch || area.Height <= 0) return;
        for (int index = areas.Count - 1; index >= lineFirst; index--)
        {
            Rect other = areas[index];
            if (area.X > other.Right + Touch || area.Right < other.X - Touch) continue;
            area = area.Union(other);
            areas.RemoveAt(index);
            index = areas.Count; // The wider box may now touch an area checked before.
        }
        areas.Add(area);
    }

    /// <summary>
    /// The rectangle to stroke with a 1-dip pen so the line covers exactly the outermost device pixels of the area: the edges
    /// snap to device pixels at <paramref name="scale"/> and the stroke centre sits half a dip inside them.
    /// </summary>
    internal static Rect Stroke(Rect area, double scale)
    {
        if (!(scale > 0) || double.IsInfinity(scale)) scale = 1;
        double left = Math.Round(area.X * scale) / scale, top = Math.Round(area.Y * scale) / scale;
        double right = Math.Round(area.Right * scale) / scale, bottom = Math.Round(area.Bottom * scale) / scale;
        return new Rect(left + 0.5, top + 0.5, Math.Max(0, right - left - 1), Math.Max(0, bottom - top - 1));
    }
}
