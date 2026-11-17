using System;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// A text that is never narrower than a reserved sample text in its own font (<see cref="ReserveText"/>), so a counter in front of buttons
/// ("1 selected" → "1,234 selected") does not push the buttons along while its number grows within the reserved digits. Without a sample it is
/// an ordinary <see cref="TextBlock"/> (same styles).
/// </summary>
public sealed class ReservedTextBlock : TextBlock
{
    /// <summary>The widest text the block keeps room for, e.g. the counter with four digits.</summary>
    public static readonly StyledProperty<string?> ReserveTextProperty =
        AvaloniaProperty.Register<ReservedTextBlock, string?>(nameof(ReserveText));

    static ReservedTextBlock() => AffectsMeasure<ReservedTextBlock>(ReserveTextProperty);

    protected override Type StyleKeyOverride => typeof(TextBlock);

    /// <inheritdoc cref="ReserveTextProperty"/>
    public string? ReserveText
    {
        get => GetValue(ReserveTextProperty);
        set => SetValue(ReserveTextProperty, value);
    }

    // A detached text in the same font measures the sample (the block's own text layout always lays out its own text).
    private readonly TextBlock _sample = new();

    protected override Size MeasureOverride(Size availableSize)
    {
        Size size = base.MeasureOverride(availableSize);
        if (string.IsNullOrEmpty(ReserveText)) return size;
        _sample.Text = ReserveText;
        _sample.FontFamily = FontFamily; _sample.FontSize = FontSize; _sample.FontWeight = FontWeight; _sample.FontStyle = FontStyle;
        _sample.FontStretch = FontStretch; _sample.FontFeatures = FontFeatures; _sample.LetterSpacing = LetterSpacing;
        _sample.Measure(Size.Infinity);
        double reserved = Math.Ceiling(_sample.DesiredSize.Width) + Padding.Left + Padding.Right;
        double width = Math.Max(size.Width, reserved);
        return new Size(double.IsFinite(availableSize.Width) ? Math.Min(width, availableSize.Width) : width, size.Height);
    }
}
