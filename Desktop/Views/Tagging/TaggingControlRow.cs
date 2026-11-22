using System;
using System.Collections.Generic;
using System.Linq;
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

    internal static readonly StyledProperty<bool> ShowActionLabelsProperty =
        AvaloniaProperty.Register<TaggingControlRow, bool>("ShowActionLabels", true);
    internal Control? CompactActions { get; set; }
    protected override Size MeasureOverride(Size availableSize)
    {
        Size constraint = new(availableSize.Width, double.PositiveInfinity);
        if (CompactActions is { } actions)
        {
            SetActionLabels(true, actions);
            foreach (Control child in Children) child.Measure(Size.Infinity);
            double preferredWidth = 0;
            foreach (Control child in Children)
                if (child.IsVisible) preferredWidth += child.DesiredSize.Width + GroupSpacing;
            if (preferredWidth - GroupSpacing > availableSize.Width)
                SetActionLabels(false, actions);
        }
        foreach (Control child in Children) child.Measure(constraint);
        return Place(availableSize.Width);
    }

    private void SetActionLabels(bool visible, Control actions)
    {
        if (visible == GetValue(ShowActionLabelsProperty)) return;
        SetValue(ShowActionLabelsProperty, visible);
        foreach (TextBlock label in actions.GetVisualDescendants().OfType<TextBlock>())
        {
            for (Visual? visual = label; visual is Layoutable layout && visual != this; visual = visual.GetVisualParent())
                layout.InvalidateMeasure();
        }
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
/// Measures the walk, tools and view controls in reading order. The walk takes
/// the spare space on its line and the view controls dock to the right.
/// </summary>
internal sealed class TaggingHeader : Panel
{
    internal const double GroupSpacing = 16;
    internal const double LineSpacing = 8;

    [Flags]
    private enum Compact { None = 0, View = 1, Stamp = 2 }

    private readonly Control _walk, _addAndHelp, _stamp, _filters, _more, _presentation;
    private readonly Action<bool> _setStampCompact, _setViewCompact;
    private readonly Action<double> _trimStartLabel;
    private readonly List<Line> _lines = new();
    private Compact _compact;

    private sealed record Line(Control[] Groups, double Width, double Height, double WalkWidth);

    internal TaggingHeader(Control walk, Control addAndHelp, Control stamp, Control filters, Control more, Control presentation,
        Action<bool> setStampCompact, Action<bool> setViewCompact, Action<double> trimStartLabel)
    {
        _walk = walk;
        _addAndHelp = addAndHelp;
        _stamp = stamp;
        _filters = filters;
        _more = more;
        _presentation = presentation;
        _setStampCompact = setStampCompact;
        _setViewCompact = setViewCompact;
        _trimStartLabel = trimStartLabel;
        Children.AddRange([walk, addAndHelp, stamp, filters, more, presentation]);
    }

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

        MeasureFullWalk();
        Control[] tools = Visible(_stamp, _filters, _more, _presentation);
        Control[] all = [_walk, _addAndHelp, ..tools];
        bool wide = width >= TaggingHeaderSettings.SingleRowMinWidth;
        if (wide && Fit(all, width))
        {
            AddLine(all, width);
        }
        else
        {
            if (wide) MeasureFullWalk();
            _addAndHelp.Measure(Size.Infinity);
            Control[] upper = [_walk, _addAndHelp];
            Control[] lower = tools;
            TrimWalk(LineWidth(upper) - width);
            if (LineWidth(upper) > width)
            {
                // Keep the full resume label when the actions need their own line anyway.
                MeasureFullWalk();
                TrimWalk(_walk.DesiredSize.Width - width);
                upper = [_walk];
                lower = [_addAndHelp, ..tools];
            }
            Fit(lower, width);

            // At the fallback width, pack the final line from the right so the
            // presentation and More stay together. Earlier tools keep their tab order.
            int split = lower.Length - 1;
            while (split > 0 && LineWidth(lower.AsSpan(split - 1)) <= width) split--;
            Wrap([..upper, ..lower.AsSpan(0, split)], width);
            AddLine(lower.AsSpan(split), width);
        }

        double height = 0, usedWidth = 0;
        foreach (Line line in _lines)
        {
            height += line.Height;
            usedWidth = Math.Max(usedWidth, line.Width);
        }
        return new Size(usedWidth, height + LineSpacing * (_lines.Count - 1));
    }

    private bool Fit(Control[] groups, double width)
    {
        _setViewCompact(false);
        _setStampCompact(false);
        Compact previous = _compact;
        _compact = Compact.None;
        foreach (Control group in groups) group.Measure(Size.Infinity);
        if (Fits()) return true;

        _compact |= Compact.View;
        _setViewCompact(true);
        _presentation.Measure(Size.Infinity);
        if (Fits()) return true;

        _compact |= Compact.Stamp;
        _setStampCompact(true);
        _stamp.Measure(Size.Infinity);
        if (Fits()) return true;

        // Preserve the one-row layout in locales with longer resume labels.
        if (!ReferenceEquals(groups[0], _walk)) return false;
        TrimWalk(LineWidth(groups) - width);
        return LineWidth(groups) <= width;

        bool Fits()
        {
            double reserve = (previous & ~_compact) != Compact.None ? TaggingHeaderSettings.RestoreReserve : 0;
            // Re-measure from natural width at every stage so compacting a
            // label gives the dropdowns back any space they can use.
            _filters.Measure(Size.Infinity);
            double excess = LineWidth(groups) + reserve - width;
            if (excess <= 0) return true;
            double budget = Math.Max(_filters.MinWidth, _filters.DesiredSize.Width - excess);
            _filters.Measure(new Size(budget, double.PositiveInfinity));
            return LineWidth(groups) + reserve <= width;
        }
    }

    private void MeasureFullWalk()
    {
        _trimStartLabel(0);
        _walk.Measure(Size.Infinity);
    }

    private void TrimWalk(double excess)
    {
        if (excess <= 0) return;
        _trimStartLabel(excess);
        _walk.Measure(Size.Infinity);
    }

    private static Control[] Visible(params Control[] groups) => Array.FindAll(groups, group => group.IsVisible);

    private static double LineWidth(ReadOnlySpan<Control> groups)
    {
        double width = GroupSpacing * Math.Max(0, groups.Length - 1);
        foreach (Control group in groups) width += group.DesiredSize.Width;
        return width;
    }

    private void Wrap(ReadOnlySpan<Control> groups, double width)
    {
        while (!groups.IsEmpty)
        {
            int count = 1;
            while (count < groups.Length && LineWidth(groups[..(count + 1)]) <= width) count++;
            AddLine(groups[..count], width);
            groups = groups[count..];
        }
    }

    private void AddLine(ReadOnlySpan<Control> groups, double width)
    {
        double minimum = LineWidth(groups);
        double walkWidth = 0;
        if (ReferenceEquals(groups[0], _walk))
        {
            walkWidth = _walk.DesiredSize.Width;
            double extra = double.IsFinite(width) ? Math.Max(0, width - minimum) : 0;
            _walk.Measure(new Size(walkWidth + extra, double.PositiveInfinity));
        }
        double height = 0;
        foreach (Control group in groups) height = Math.Max(height, group.DesiredSize.Height);
        _lines.Add(new Line(groups.ToArray(), minimum, height, walkWidth));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (Line line in _lines)
        {
            double x = 0;
            double extra = Math.Max(0, finalSize.Width - line.Width);
            foreach (Control group in line.Groups)
            {
                Size size = group.DesiredSize;
                double width = size.Width;
                if (ReferenceEquals(group, _walk))
                {
                    width = line.WalkWidth + extra;
                    extra = 0;
                }
                if (ReferenceEquals(group, _presentation)) x += extra;
                group.Arrange(new Rect(x, y + (line.Height - size.Height) / 2, width, size.Height));
                x += width + GroupSpacing;
            }
            y += line.Height + LineSpacing;
        }
        return finalSize;
    }
}
