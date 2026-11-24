using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace MeowChan.Desktop.Controls;

// Preserve the 12-DIP text height and both rating axes when a translation exceeds the right column.
// At ordinary widths this is the standard TextBlock layout. Only the over-wide runs need horizontal fitting.
internal sealed class RatingCaption : TextBlock
{
    protected override Type StyleKeyOverride => typeof(TextBlock);

    internal RatingCaption()
    {
        // LCD text assumes an opaque, matching background. This caption is drawn over the panel's composited surface.
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        TextAlignment = TextAlignment.Left;
        RenderTransformOrigin = RelativePoint.TopLeft;
        RenderTransform = _scale;
        ClipToBounds = false;
    }

    private readonly ScaleTransform _scale = new(1, 1);
    private int _lineLimit = 2;
    private double _layoutWidth;
    internal double HorizontalScale => _scale.ScaleX;

    internal void SetLineLimit(int lines)
    {
        if (_lineLimit == lines) return;
        _lineLimit = lines;
        InvalidateMeasure();
    }

    private int LineCount(double width)
    {
        using TextLayout layout = new(Text, new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize,
            Foreground, textWrapping: TextWrapping.Wrap, maxWidth: width, fontFeatures: FontFeatures);
        return layout.TextLines.Count;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        _layoutWidth = 0;
        double width = availableSize.Width;
        Size natural = base.MeasureOverride(availableSize);
        if (string.IsNullOrEmpty(Text) || !double.IsFinite(width) || width <= 0) return natural;
        var lines = TextLayout.TextLines;
        if (lines.Count <= _lineLimit && lines.Sum(line => line.TextRuns.Sum(run => run.Text.Length)) == Text.Length)
            return natural;

        // Find the narrowest unscaled layout that contains every character in the allowed lines.
        double low = Math.Floor(width), high = Math.Ceiling(base.MeasureOverride(Size.Infinity).Width) + 1;
        while (high - low > 1)
        {
            double mid = Math.Floor((low + high) / 2);
            if (LineCount(mid) <= _lineLimit) high = mid;
            else low = mid;
        }
        _layoutWidth = high;
        Size fitted = base.MeasureOverride(availableSize.WithWidth(high));
        return new Size(width, fitted.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double width = Math.Max(_layoutWidth, finalSize.Width);
        _scale.ScaleX = width > 0 ? finalSize.Width / width : 1;
        base.ArrangeOverride(finalSize.WithWidth(width));
        return finalSize;
    }
}
