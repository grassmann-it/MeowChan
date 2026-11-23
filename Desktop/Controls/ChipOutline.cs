using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// The frame of a chip whose outline can be dashed. Avalonia's <see cref="Border"/> draws only solid lines, so a source tag
/// (Danbooru) gets this decorator: a dashed outline tells it apart from a filled own-tag chip without relying on colour.
/// Purely visual; it takes no input and adds no tab stop. Colours, radius and dash come from the <c>chip-*</c> styles.
/// </summary>
public sealed class ChipOutline : Decorator
{
    /// <summary>Dash and gap, in multiples of the stroke thickness: short enough to read as dashed on an 18-px chip.</summary>
    private static readonly ImmutableDashStyle Dash = new(new double[] { 2, 2 }, 0);

    public static readonly StyledProperty<IBrush?> BackgroundProperty = Border.BackgroundProperty.AddOwner<ChipOutline>();
    public static readonly StyledProperty<IBrush?> StrokeProperty = AvaloniaProperty.Register<ChipOutline, IBrush?>(nameof(Stroke));
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<ChipOutline, double>(nameof(StrokeThickness), 1);
    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty = Border.CornerRadiusProperty.AddOwner<ChipOutline>();
    public static readonly StyledProperty<bool> IsDashedProperty = AvaloniaProperty.Register<ChipOutline, bool>(nameof(IsDashed));

    static ChipOutline() => AffectsRender<ChipOutline>(BackgroundProperty, StrokeProperty, StrokeThicknessProperty, CornerRadiusProperty, IsDashedProperty);

    public IBrush? Background { get => GetValue(BackgroundProperty); set => SetValue(BackgroundProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public CornerRadius CornerRadius { get => GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    /// <summary>True draws the outline dashed (a source tag that is not marked); false draws it solid.</summary>
    public bool IsDashed { get => GetValue(IsDashedProperty); set => SetValue(IsDashedProperty, value); }

    public override void Render(DrawingContext context)
    {
        double thickness = Math.Max(0, StrokeThickness);
        Rect bounds = new Rect(Bounds.Size).Deflate(thickness / 2);
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        double radius = Math.Min(CornerRadius.TopLeft, Math.Min(bounds.Width, bounds.Height) / 2);
        Pen? pen = Stroke is { } stroke && thickness > 0 ? new Pen(stroke, thickness, IsDashed ? Dash : null) : null;
        context.DrawRectangle(Background, pen, new RoundedRect(bounds, radius));
    }
}
