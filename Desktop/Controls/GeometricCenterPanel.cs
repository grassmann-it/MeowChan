using System;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// A one-line three-region layout whose middle region is centred on the whole available width,
/// rather than in the space left over by unequal edge regions. If the requested width cannot
/// fit, the middle region yields to the two edge regions and is constrained to their real gap.
/// </summary>
internal class GeometricCenterPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double height = 0;
        double width = 0;
        foreach (Control child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            height = Math.Max(height, child.DesiredSize.Height);
            width += child.DesiredSize.Width;
        }
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        Control? left = Children.Count > 0 ? Children[0] : null;
        Control? center = Children.Count > 1 ? Children[1] : null;
        Control? right = Children.Count > 2 ? Children[2] : null;
        double leftWidth = Math.Min(left?.DesiredSize.Width ?? 0, finalSize.Width);
        double rightWidth = Math.Min(right?.DesiredSize.Width ?? 0, Math.Max(0, finalSize.Width - leftWidth));
        double gapStart = leftWidth;
        double gapEnd = Math.Max(gapStart, finalSize.Width - rightWidth);
        double centerWidth = Math.Min(center?.DesiredSize.Width ?? 0, gapEnd - gapStart);
        double centeredX = (finalSize.Width - centerWidth) / 2;
        double centerX = Math.Clamp(centeredX, gapStart, gapEnd - centerWidth);

        left?.Arrange(new Rect(0, 0, leftWidth, finalSize.Height));
        center?.Arrange(new Rect(centerX, 0, centerWidth, finalSize.Height));
        right?.Arrange(new Rect(finalSize.Width - rightWidth, 0, rightWidth, finalSize.Height));
        for (int index = 3; index < Children.Count; index++) Children[index].Arrange(default);
        return finalSize;
    }
}
