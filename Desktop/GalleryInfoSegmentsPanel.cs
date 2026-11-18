using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop;

/// <summary>
/// The left column of the gallery's info line (UX spec S1 "Info = eine Zeile"): the segments and their "·" separators stand in one row. When the row
/// is wider than the room, the longest segments give way first and end in an ellipsis (a text's <c>TextTrimming</c>), so the line never wraps onto a
/// second row, never pushes the status column away and never overflows the window (Tom, 16.11.: half-screen windows). A segment that lost
/// characters shows its full text as its tooltip.
/// </summary>
internal sealed class GalleryInfoSegmentsPanel : Panel
{
    /// <summary>The width below which a segment is never shrunk any further (an ellipsis and a character or two): a layout floor, not a setting.</summary>
    internal const double MinimumShrunkWidth = 24;

    /// <summary>The gap between two items of the row, in dip.</summary>
    internal const double ItemSpacing = 8;

    /// <summary>
    /// The widths the items of the row get. They keep their natural width when the row fits in <paramref name="available"/>; otherwise the widest ones
    /// are cut to one common level (the narrow ones stay whole), never below <see cref="MinimumShrunkWidth"/>.
    /// </summary>
    internal static double[] Allocate(IReadOnlyList<double> natural, double available, double spacing)
    {
        double[] widths = natural.ToArray();
        if (widths.Length == 0 || double.IsInfinity(available) || double.IsNaN(available)) return widths;
        double total = widths.Sum() + spacing * (widths.Length - 1);
        double excess = total - Math.Max(0, available);
        if (excess <= 0) return widths;
        double Cut(double level) => widths.Sum(width => width > MinimumShrunkWidth ? Math.Max(0, width - Math.Max(level, MinimumShrunkWidth)) : 0);
        double low = MinimumShrunkWidth, high = widths.Max();
        double chosen = MinimumShrunkWidth;
        if (Cut(low) >= excess)
        {
            // The largest common level whose cut still covers the excess: the cut falls as the level rises.
            for (int step = 0; step < 48; step++)
            {
                double middle = (low + high) / 2;
                if (Cut(middle) >= excess) low = middle; else high = middle;
            }
            chosen = low;
        }
        for (int index = 0; index < widths.Length; index++)
            if (widths[index] > MinimumShrunkWidth) widths[index] = Math.Min(widths[index], chosen);
        return widths;
    }

    private readonly List<Control> _row = new();
    private readonly HashSet<Control> _trimmed = new();
    private readonly HashSet<TextBlock> _tipped = new();
    private double[] _natural = Array.Empty<double>();

    /// <summary>The segments that lost characters in the last layout (tests and smokes).</summary>
    internal IReadOnlyCollection<Control> TrimmedSegments => _trimmed;

    protected override Size MeasureOverride(Size availableSize)
    {
        _row.Clear();
        foreach (Control child in Children)
            if (child.IsVisible) _row.Add(child);
        foreach (Control child in _row) child.Measure(Size.Infinity);
        _natural = _row.Select(child => child.DesiredSize.Width).ToArray();
        double[] widths = Allocate(_natural, availableSize.Width, ItemSpacing);
        double height = 0;
        for (int index = 0; index < _row.Count; index++)
        {
            if (widths[index] < _natural[index] - 0.01) _row[index].Measure(new Size(widths[index], double.PositiveInfinity));
            height = Math.Max(height, _row[index].DesiredSize.Height);
        }
        double used = widths.Sum() + ItemSpacing * Math.Max(0, widths.Length - 1);
        return new Size(double.IsInfinity(availableSize.Width) ? used : Math.Min(used, availableSize.Width), height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double[] widths = Allocate(_natural, finalSize.Width, ItemSpacing);
        _trimmed.Clear();
        double x = 0;
        for (int index = 0; index < _row.Count; index++)
        {
            Control child = _row[index];
            child.Arrange(new Rect(x, 0, Math.Max(0, Math.Min(widths[index], finalSize.Width - x)), finalSize.Height));
            // The tooltip of a text that lost characters is the whole text (an interactive segment, the rating, keeps its own tip).
            if (child is TextBlock { Focusable: false } text)
            {
                bool trimmed = widths[index] < _natural[index] - 0.01;
                if (trimmed) _trimmed.Add(child);
                if (trimmed && text.Text is { Length: > 0 } whole) { ToolTip.SetTip(text, whole); _tipped.Add(text); }
                else if (_tipped.Remove(text)) ToolTip.SetTip(text, null);
            }
            x += widths[index] + ItemSpacing;
        }
        return finalSize;
    }
}
