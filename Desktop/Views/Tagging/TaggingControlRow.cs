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
    public Control? TrailingChild { get; set; }
    internal Action<bool>? SetTrailingCompact { get; set; }
    internal IReadOnlyList<(Control Group, Action<bool> SetCompact)> CompactGroups { get; set; } = [];
    private bool _trailingCompact;

    protected override Size MeasureOverride(Size availableSize)
    {
        Size constraint = new(availableSize.Width, double.PositiveInfinity);
        foreach (var (_, setCompact) in CompactGroups) setCompact(false);
        double leadingWidth = 0;
        foreach (Control child in Children)
        {
            if (ReferenceEquals(child, TrailingChild) && SetTrailingCompact != null) continue;
            child.Measure(constraint);
            if (child.IsVisible) leadingWidth += child.DesiredSize.Width + GroupSpacing;
        }
        if (TrailingChild is { IsVisible: true } trailing && SetTrailingCompact is { } compact)
        {
            // Measure the localized labels even when the last arrangement used icons. Try the compact
            // presentation before wrapping whole groups. Use the same spare gap as the group wrap.
            compact(false);
            trailing.Measure(Size.Infinity);
            double fullWidth = leadingWidth + trailing.DesiredSize.Width;
            _trailingCompact = fullWidth + (_trailingCompact ? GroupSpacing : 0) > availableSize.Width;
            compact(_trailingCompact);
            trailing.Measure(constraint);
        }
        double rowWidth = 0;
        int visibleCount = 0;
        foreach (Control child in Children)
        {
            if (!child.IsVisible) continue;
            rowWidth += child.DesiredSize.Width;
            visibleCount++;
        }
        rowWidth += Math.Max(0, visibleCount - 1) * GroupSpacing;
        foreach (var (group, setCompact) in CompactGroups)
        {
            if (rowWidth <= availableSize.Width) break;
            if (!group.IsVisible) continue;
            double fullWidth = group.DesiredSize.Width;
            setCompact(true);
            group.Measure(constraint);
            rowWidth -= fullWidth - group.DesiredSize.Width;
        }
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
