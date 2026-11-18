using System;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop;

// Archive result footer: the pager and its range label share one centered line
// when both fit; otherwise the label stays below the pager as before. The choice
// is made in measure, never from SizeChanged. Once a result set needed two lines
// it keeps them while paging through that set, so a wider pager or range label on
// a later page cannot move the pager under the pointer. A new result total or a
// different available width starts a fresh decision.
public sealed class ArchiveFooterLayout : Panel
{
    internal const double InlineGap = 8;
    internal const double StackGap = 4;

    public static readonly StyledProperty<int> LayoutKeyProperty =
        AvaloniaProperty.Register<ArchiveFooterLayout, int>(nameof(LayoutKey));

    static ArchiveFooterLayout() => AffectsMeasure<ArchiveFooterLayout>(LayoutKeyProperty);

    public int LayoutKey
    {
        get => GetValue(LayoutKeyProperty);
        set => SetValue(LayoutKeyProperty, value);
    }

    private bool _inline;
    private bool _stackedLatch;
    private int _latchKey;
    private double _latchWidth = double.NaN;

    internal bool IsInline => _inline;

    internal static (bool Inline, bool StackedLatch) Resolve(bool bothVisible, double pagerWidth, double labelWidth,
        double availableWidth, bool stackedLatch)
    {
        if (!bothVisible) return (false, stackedLatch);
        bool fits = pagerWidth + InlineGap + labelWidth <= availableWidth;
        bool latch = stackedLatch || !fits;
        return (!latch, latch);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return default;
        Control pager = Children[0], label = Children[1];
        pager.Measure(availableSize);
        label.Measure(availableSize);
        if (LayoutKey != _latchKey || !availableSize.Width.Equals(_latchWidth))
        {
            _latchKey = LayoutKey;
            _latchWidth = availableSize.Width;
            _stackedLatch = false;
        }
        bool both = pager.IsVisible && label.IsVisible;
        (_inline, _stackedLatch) = Resolve(both, pager.DesiredSize.Width, label.DesiredSize.Width,
            availableSize.Width, _stackedLatch);
        Size p = pager.DesiredSize, l = label.DesiredSize;
        return _inline
            ? new Size(p.Width + InlineGap + l.Width, Math.Max(p.Height, l.Height))
            : new Size(Math.Max(p.Width, l.Width), p.Height + (both ? StackGap : 0) + l.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return finalSize;
        Control pager = Children[0], label = Children[1];
        Size p = pager.DesiredSize, l = label.DesiredSize;
        if (_inline)
        {
            double height = Math.Max(p.Height, l.Height);
            double left = Math.Max(0, (finalSize.Width - p.Width - InlineGap - l.Width) / 2);
            pager.Arrange(new Rect(left, (height - p.Height) / 2, p.Width, p.Height));
            double labelLeft = left + p.Width + InlineGap;
            label.Arrange(new Rect(labelLeft, (height - l.Height) / 2,
                Math.Max(0, Math.Min(l.Width, finalSize.Width - labelLeft)), l.Height));
            return finalSize;
        }
        double pagerWidth = Math.Min(p.Width, finalSize.Width), labelWidth = Math.Min(l.Width, finalSize.Width);
        pager.Arrange(new Rect((finalSize.Width - pagerWidth) / 2, 0, pagerWidth, p.Height));
        double top = pager.IsVisible && label.IsVisible ? p.Height + StackGap : p.Height;
        label.Arrange(new Rect((finalSize.Width - labelWidth) / 2, top, labelWidth, l.Height));
        return finalSize;
    }
}
