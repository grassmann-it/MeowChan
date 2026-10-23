using System;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop;

// Decide the arrangement before measuring either child. Mutating Grid cells from
// SizeChanged made virtualized item heights depend on an earlier layout pass.
public sealed class ReaderPostLayout : Panel
{
    public static readonly AttachedProperty<bool> LargeReaderProperty =
        AvaloniaProperty.RegisterAttached<ReaderPostLayout, Control, bool>("LargeReader", inherits: true);

    static ReaderPostLayout() => AffectsMeasure<ReaderPostLayout>(LargeReaderProperty);

    private bool _beside;
    private bool Beside(double width) => width >= (GetValue(LargeReaderProperty) ? 760 : 600);
    private double MediaWidth => GetValue(LargeReaderProperty) ? 376 : 268;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Children.Count != 2) return default;
        Control media = Children[0], body = Children[1];
        double width = availableSize.Width;
        media.Measure(new Size(Math.Min(width, MediaWidth), availableSize.Height));
        bool beside = _beside = Beside(width);
        double gap = media.IsVisible ? (beside ? 12 : 8) : 0;
        body.Measure(new Size(beside ? Math.Max(0, width - media.DesiredSize.Width - gap) : width,
            availableSize.Height));
        return beside
            ? new Size(media.DesiredSize.Width + gap + body.DesiredSize.Width,
                Math.Max(media.DesiredSize.Height, body.DesiredSize.Height))
            : new Size(Math.Max(media.DesiredSize.Width, body.DesiredSize.Width),
                media.DesiredSize.Height + gap + body.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count != 2) return finalSize;
        Control media = Children[0], body = Children[1];
        bool beside = _beside;
        double gap = media.IsVisible ? (beside ? 12 : 8) : 0;
        double mediaWidth = Math.Min(finalSize.Width, media.DesiredSize.Width);
        media.Arrange(new Rect(0, 0, mediaWidth, media.DesiredSize.Height));
        body.Arrange(beside
            ? new Rect(mediaWidth + gap, 0, Math.Max(0, finalSize.Width - mediaWidth - gap), finalSize.Height)
            : new Rect(0, media.DesiredSize.Height + gap, finalSize.Width,
                Math.Max(0, finalSize.Height - media.DesiredSize.Height - gap)));
        return finalSize;
    }
}
