using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

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
    public Control? TrailingChild { get; set; }
    protected override Size MeasureOverride(Size availableSize)
    {
        Size constraint = new(availableSize.Width, double.PositiveInfinity);
        foreach (Control child in Children) child.Measure(constraint);
        return Place(availableSize.Width);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width);
        for (int start = 0; start < _placements.Count;)
        {
            double y = _placements[start].Bounds.Y;
            double height = _placements[start].Bounds.Height;
            int end = start + 1;
            while (end < _placements.Count && _placements[end].Bounds.Y == y)
            {
                height = Math.Max(height, _placements[end].Bounds.Height);
                end++;
            }
            for (int i = start; i < end; i++)
            {
                (Control child, Rect bounds) = _placements[i];
                child.Arrange(new Rect(bounds.X, y + (height - bounds.Height) / 2, bounds.Width, bounds.Height));
            }
            start = end;
        }
        return finalSize;
    }

    private Size Place(double width)
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
        if (TrailingChild != null && double.IsFinite(width))
        {
            int index = _placements.FindIndex(item => ReferenceEquals(item.Control, TrailingChild));
            if (index >= 0)
            {
                var trailing = _placements[index];
                _placements[index] = (trailing.Control, new Rect(Math.Max(trailing.Bounds.X, width - trailing.Bounds.Width),
                    trailing.Bounds.Y, trailing.Bounds.Width, trailing.Bounds.Height));
            }
        }
        return new Size(usedWidth, y + lineHeight);
    }
}

/// <summary>
/// Keeps five header groups in reading order, with a flexible walk group and a
/// flexible gap after the filters on the second line. Measure chooses the detail
/// and line breaks; arrange only distributes the remaining space.
/// </summary>
internal sealed class TaggingHeader : Panel
{
    internal const double GroupSpacing = 16;
    internal const double LineSpacing = 8;
    private const double WideWidth = 1376;
    private const double RestoreReserve = 8;

    [Flags]
    private enum Compact { None = 0, View = 1, Count = 2, Stamp = 4 }

    private readonly Control _walk, _stamp, _presentation, _actions;
    private readonly Grid _filters;
    private readonly Control _count;
    private readonly Action<bool> _setViewCompact, _setCountCompact, _setStampCompact;
    private readonly List<Line> _lines = new();
    private Compact _compact;

    private sealed record Line(Control[] Groups, double Width, double Height, double WalkWidth);

    internal TaggingHeader(Control walk, Control stamp, Grid filters, Control count, Control presentation, Control actions,
        Action<bool> setViewCompact, Action<bool> setCountCompact, Action<bool> setStampCompact)
    {
        _walk = walk;
        _stamp = stamp;
        _filters = filters;
        _count = count;
        _presentation = presentation;
        _actions = actions;
        _setViewCompact = setViewCompact;
        _setCountCompact = setCountCompact;
        _setStampCompact = setStampCompact;
        Children.AddRange([walk, stamp, filters, presentation, actions]);
    }

    internal bool ResultCountCompact => _compact.HasFlag(Compact.Count);

    // These content changes happen during the header's own measure. Refresh the path
    // synchronously so the next fit attempt sees the new width through the templates.
    internal static void InvalidateGroupMeasure(Control content)
    {
        for (Visual? visual = content; visual is Layoutable layout && visual is not TaggingHeader; visual = visual.GetVisualParent())
            layout.InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = availableSize.Width;
        _lines.Clear();

        Control[] all = Visible(_walk, _stamp, _filters, _presentation, _actions);
        if (width >= WideWidth && Fit(all, width, [_setViewCompact, _setCountCompact, _setStampCompact],
                [Compact.View, Compact.Count, Compact.Stamp], out Compact wide))
        {
            _compact = wide;
            AddLines(all, width);
        }
        else
        {
            Control[] top = Visible(_walk, _stamp);
            Control[] bottom = Visible(_filters, _presentation, _actions);
            Fit(top, width, [_setStampCompact], [Compact.Stamp], out Compact upper);
            Fit(bottom, width, [_setViewCompact, _setCountCompact], [Compact.View, Compact.Count], out Compact lower);
            _compact = upper | lower;
            AddLines(top, width);
            AddLines(bottom, width);
        }

        double height = 0, usedWidth = 0;
        foreach (Line line in _lines)
        {
            height += line.Height;
            usedWidth = Math.Max(usedWidth, line.Width);
        }
        return new Size(usedWidth, height + LineSpacing * Math.Max(0, _lines.Count - 1));
    }

    private bool Fit(Control[] groups, double width, Action<bool>[] setters, Compact[] steps, out Compact compact)
    {
        Compact details = Compact.None;
        for (int i = 0; i < steps.Length; i++)
        {
            setters[i](false);
            details |= steps[i];
        }

        compact = Compact.None;
        for (int i = 0; ; i++)
        {
            double reserve = (_compact & details & ~compact) != Compact.None ? RestoreReserve : 0;
            foreach (Control group in groups) group.Measure(Size.Infinity);

            double measured = LineWidth(groups);
            if (measured + reserve <= width) return true;
            if (Array.IndexOf(groups, _filters) >= 0)
            {
                double minimum = _filters.MinWidth + (_count.IsVisible ? _count.DesiredSize.Width : 0);
                double budget = Math.Max(minimum, _filters.DesiredSize.Width - (measured + reserve - width));
                _filters.Measure(new Size(budget, double.PositiveInfinity));
                if (LineWidth(groups) + reserve <= width) return true;
            }

            if (i == steps.Length) return false;
            compact |= steps[i];
            setters[i](true);
        }
    }

    private static Control[] Visible(params Control[] groups) => Array.FindAll(groups, group => group.IsVisible);

    private static double LineWidth(ReadOnlySpan<Control> groups)
    {
        double width = GroupSpacing * Math.Max(0, groups.Length - 1);
        foreach (Control group in groups) width += group.DesiredSize.Width;
        return width;
    }

    private void AddLines(ReadOnlySpan<Control> groups, double width)
    {
        while (!groups.IsEmpty)
        {
            int count = 1;
            while (count < groups.Length && LineWidth(groups[..(count + 1)]) <= width) count++;
            ReadOnlySpan<Control> line = groups[..count];
            double minimum = LineWidth(line);
            double extra = double.IsFinite(width) ? Math.Max(0, width - minimum) : 0;
            double walkWidth = 0;
            if (ReferenceEquals(line[0], _walk))
            {
                walkWidth = _walk.DesiredSize.Width;
                _walk.Measure(new Size(walkWidth + extra, double.PositiveInfinity));
            }
            double height = 0;
            foreach (Control group in line) height = Math.Max(height, group.DesiredSize.Height);
            _lines.Add(new Line(line.ToArray(), minimum, height, walkWidth));
            groups = groups[count..];
        }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (Line line in _lines)
        {
            double x = 0;
            double extra = Math.Max(0, finalSize.Width - line.Width);
            bool hasWalk = ReferenceEquals(line.Groups[0], _walk);
            foreach (Control group in line.Groups)
            {
                if (!hasWalk && (ReferenceEquals(group, _presentation) || ReferenceEquals(group, _actions)))
                {
                    x += extra;
                    extra = 0;
                }

                Size size = group.DesiredSize;
                double width = ReferenceEquals(group, _walk) ? line.WalkWidth + extra : size.Width;
                group.Arrange(new Rect(x, y + (line.Height - size.Height) / 2, width, size.Height));
                x += width + GroupSpacing;
            }
            y += line.Height + LineSpacing;
        }
        return finalSize;
    }
}
