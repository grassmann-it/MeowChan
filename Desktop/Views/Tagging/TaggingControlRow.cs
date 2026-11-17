using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowChan.Desktop;

/// <summary>
/// Measures intact task groups, not the window width. A wrapped group rejoins only with one group-gap of spare space;
/// shrinking still wraps immediately before overlap. Children never change kind, parent or order during resize.
/// </summary>
internal sealed class TaggingControlRow : Panel
{
    public static readonly StyledProperty<double> GroupSpacingProperty =
        AvaloniaProperty.Register<TaggingControlRow, double>(nameof(GroupSpacing), 16);
    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<TaggingControlRow, double>(nameof(LineSpacing), 8);
    private readonly HashSet<Control> _lineStarts = new();
    private readonly List<(Control Control, Rect Bounds)> _placements = new();

    static TaggingControlRow() => AffectsMeasure<TaggingControlRow>(GroupSpacingProperty, LineSpacingProperty);
    public double GroupSpacing { get => GetValue(GroupSpacingProperty); set => SetValue(GroupSpacingProperty, value); }
    public double LineSpacing { get => GetValue(LineSpacingProperty); set => SetValue(LineSpacingProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (Control child in Children)
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        return Place(availableSize.Width, arrange: false);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, arrange: true);
        foreach ((Control child, Rect bounds) in _placements) child.Arrange(bounds);
        return finalSize;
    }

    private Size Place(double width, bool arrange)
    {
        HashSet<Control> nextStarts = new();
        _placements.Clear();
        double x = 0, y = 0, lineHeight = 0, usedWidth = 0;
        foreach (Control child in Children)
        {
            if (!child.IsVisible) continue;
            Size size = child.DesiredSize;
            double gap = x > 0 ? GroupSpacing : 0;
            double reserve = _lineStarts.Contains(child) ? GroupSpacing : 0;
            if (x > 0 && x + gap + size.Width + reserve > width)
            {
                nextStarts.Add(child);
                y += lineHeight + LineSpacing;
                x = lineHeight = gap = 0;
            }
            _placements.Add((child, new Rect(x + gap, y, size.Width, size.Height)));
            x += gap + size.Width;
            usedWidth = Math.Max(usedWidth, x);
            lineHeight = Math.Max(lineHeight, size.Height);
        }
        _lineStarts.Clear();
        _lineStarts.UnionWith(nextStarts);
        if (arrange && _placements.Count > 0 && double.IsFinite(width))
        {
            // The last (presentation/feedback) group owns the trailing edge, not a dead strip outside the row.
            var last = _placements[^1];
            _placements[^1] = (last.Control, new Rect(Math.Max(last.Bounds.X, width - last.Bounds.Width),
                last.Bounds.Y, last.Bounds.Width, last.Bounds.Height));
        }
        return new Size(usedWidth, y + lineHeight);
    }
}
