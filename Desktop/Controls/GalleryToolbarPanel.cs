using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

internal sealed class GalleryToolbarPanel : GeometricCenterPanel
{
    private const double RegionGap = 8;

    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = base.MeasureOverride(availableSize);
        if (Children.Count < 3) return size;

        Control right = Children[2];
        var labels = right.GetLogicalDescendants().OfType<Button>()
            .Where(button => button.IsEffectivelyVisible).Select(button => button.Content)
            .OfType<GalleryToolbarActionContent>().ToArray();
        // Recover the icon-only width from either presentation. Keep pagination centred with an 8 DIP gap on each
        // side, then use the same budget on every measure so the result is independent of previous window sizes.
        double sideRoom = (availableSize.Width - Children[1].DesiredSize.Width) / 2 - RegionGap;
        double iconWidth = right.DesiredSize.Width - labels.Where(label => label.ShowLabel).Sum(label => label.LabelWidth);
        double labelRoom = sideRoom >= Children[0].DesiredSize.Width ? sideRoom - iconWidth : 0;

        bool changed = false;
        foreach (var label in labels)
        {
            bool show = labelRoom >= label.LabelWidth;
            if (show) labelRoom -= label.LabelWidth;
            if (label.ShowLabel == show) continue;
            label.ShowLabel = show;
            // Remeasure only when the presentation changes. Both modes use the same controls, including the tooltip anchor.
            for (Visual? current = label; current != null && current != this; current = current.GetVisualParent())
                if (current is Layoutable element) element.InvalidateMeasure();
            changed = true;
        }
        return changed ? base.MeasureOverride(availableSize) : size;
    }
}

internal sealed class GalleryToolbarActionContent : Panel
{
    private const double LabelGap = 4;
    private readonly TextBlock _label = new() { IsHitTestVisible = false, Opacity = 0 };

    internal GalleryToolbarActionContent(Control icon, string text)
    {
        IsHitTestVisible = false;
        _label.Text = text;
        AutomationProperties.SetAccessibilityView(_label, AccessibilityView.Raw);
        Children.Add(icon);
        Children.Add(_label);
    }

    internal string Text { set => _label.Text = value; }
    internal double LabelWidth { get; private set; }
    internal bool ShowLabel
    {
        get => _label.Opacity == 1;
        set => _label.Opacity = value ? 1 : 0;
    }
    internal Control Icon { get => Children[0]; set => Children[0] = value; }

    protected override Size MeasureOverride(Size availableSize)
    {
        Control icon = Icon;
        icon.Measure(Size.Infinity);
        _label.Measure(Size.Infinity);
        double width = _label.DesiredSize.Width + LabelGap;
        if (LabelWidth != width)
        {
            LabelWidth = width;
            this.FindAncestorOfType<GalleryToolbarPanel>()?.InvalidateMeasure();
        }
        return new Size(icon.DesiredSize.Width + (ShowLabel ? LabelWidth : 0),
            Math.Max(icon.DesiredSize.Height, _label.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Control icon = Icon;
        Size size = icon.DesiredSize;
        icon.Arrange(new Rect(new Point(0, (finalSize.Height - size.Height) / 2), size));
        Size text = _label.DesiredSize;
        _label.Arrange(ShowLabel ? new Rect(new Point(size.Width + LabelGap, (finalSize.Height - text.Height) / 2), text) : default);
        return finalSize;
    }
}
