using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// A wrapping row whose lines are at least <see cref="MinLineHeight"/> high and never lower than their tallest item (narrow-window sweep,
/// 16.11.2026). A <see cref="WrapPanel"/> with <c>ItemHeight</c> gave every line exactly that height: a status text that needs two lines in a
/// narrow window or a long language was measured into one line's height and its second line vanished. Here each item is measured with the
/// row's width and unbounded height, so a wrapping text gets all its lines; the line it stands in is as high as it needs, at least the
/// minimum. Every item is arranged over its line's full height, so <c>VerticalAlignment.Center</c> centres a one-line text in a 32 dip line
/// exactly as <c>ItemHeight</c> did. Hidden items take no room and no gap.
/// </summary>
internal class MinLineWrapPanel : Panel
{
    public static readonly StyledProperty<double> ItemSpacingProperty =
        AvaloniaProperty.Register<MinLineWrapPanel, double>(nameof(ItemSpacing));
    public static readonly StyledProperty<double> LineSpacingProperty =
        AvaloniaProperty.Register<MinLineWrapPanel, double>(nameof(LineSpacing));
    public static readonly StyledProperty<double> MinLineHeightProperty =
        AvaloniaProperty.Register<MinLineWrapPanel, double>(nameof(MinLineHeight));

    static MinLineWrapPanel() => AffectsMeasure<MinLineWrapPanel>(ItemSpacingProperty, LineSpacingProperty, MinLineHeightProperty);

    /// <summary>The gap between two items of one line, in dip.</summary>
    public double ItemSpacing { get => GetValue(ItemSpacingProperty); set => SetValue(ItemSpacingProperty, value); }
    /// <summary>The gap between two lines, in dip.</summary>
    public double LineSpacing { get => GetValue(LineSpacingProperty); set => SetValue(LineSpacingProperty, value); }
    /// <summary>The least height of a line, in dip (a line with a taller item is as high as that item).</summary>
    public double MinLineHeight { get => GetValue(MinLineHeightProperty); set => SetValue(MinLineHeightProperty, value); }

    /// <summary>One line of the row: the first and the last item (indexes into the visible items), its width and its height.</summary>
    internal readonly record struct Line(int First, int Last, double Width, double Height);

    /// <summary>
    /// The pure line breaking (tests): items of the given widths and heights go left to right with <paramref name="itemSpacing"/> between them;
    /// an item that does not fit after another starts a new line (an item wider than the row stands alone on its line). A line is as high as
    /// its tallest item, at least <paramref name="minLineHeight"/>.
    /// </summary>
    internal static List<Line> Lines(IReadOnlyList<Size> items, double width, double itemSpacing, double minLineHeight)
    {
        List<Line> lines = new();
        double limit = double.IsNaN(width) ? double.PositiveInfinity : width;
        int first = 0;
        double lineWidth = 0, lineHeight = 0;
        for (int i = 0; i < items.Count; i++)
        {
            Size item = items[i];
            if (i > first && lineWidth + itemSpacing + item.Width > limit)
            {
                lines.Add(new Line(first, i - 1, lineWidth, Math.Max(minLineHeight, lineHeight)));
                first = i;
                lineWidth = 0;
                lineHeight = 0;
            }
            lineWidth += (i > first ? itemSpacing : 0) + item.Width;
            lineHeight = Math.Max(lineHeight, item.Height);
        }
        if (items.Count > 0) lines.Add(new Line(first, items.Count - 1, lineWidth, Math.Max(minLineHeight, lineHeight)));
        return lines;
    }

    private readonly List<Control> _shown = new();
    private readonly List<Size> _sizes = new();

    private void CollectShown()
    {
        _shown.Clear();
        _sizes.Clear();
        foreach (Control child in Children)
        {
            if (!child.IsVisible) continue;
            _shown.Add(child);
            _sizes.Add(child.DesiredSize);
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size item = new(availableSize.Width, double.PositiveInfinity);
        foreach (Control child in Children) child.Measure(item);
        CollectShown();
        List<Line> lines = Lines(_sizes, availableSize.Width, ItemSpacing, MinLineHeight);
        double width = 0, height = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            width = Math.Max(width, lines[i].Width);
            height += lines[i].Height + (i > 0 ? LineSpacing : 0);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        CollectShown();
        double y = 0;
        foreach (Line line in Lines(_sizes, finalSize.Width, ItemSpacing, MinLineHeight))
        {
            double x = 0;
            for (int i = line.First; i <= line.Last; i++)
            {
                double itemWidth = Math.Min(_sizes[i].Width, Math.Max(0, finalSize.Width - x));
                _shown[i].Arrange(new Rect(x, y, itemWidth, line.Height));
                x += _sizes[i].Width + ItemSpacing;
            }
            y += line.Height + LineSpacing;
        }
        return finalSize;
    }
}
