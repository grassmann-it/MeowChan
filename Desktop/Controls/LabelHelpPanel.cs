using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// SP1: a label and its help button on one line, the button right after the last character of the label text (not at the far end of the
/// label column). The first child is the label (it wraps in the room the button leaves), the second one is the button. A wrapped label puts
/// the button after its last line. Pure layout: no text, no automation, no focus handling of its own.
/// </summary>
public sealed class LabelHelpPanel : Panel
{
    public static readonly StyledProperty<double> SpacingProperty = AvaloniaProperty.Register<LabelHelpPanel, double>(nameof(Spacing), 8);

    static LabelHelpPanel()
    {
        AffectsMeasure<LabelHelpPanel>(SpacingProperty);
    }

    /// <summary>The gap between the end of the label text and the button.</summary>
    public double Spacing { get => GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>Where the button starts and how tall the row is for a label of the given text width (the arithmetic of the arrangement; tests).</summary>
    internal static (double ButtonX, double LabelWidth) Place(double availableWidth, double labelDesiredWidth, double buttonWidth, double spacing)
    {
        double room = double.IsInfinity(availableWidth) ? labelDesiredWidth : Math.Max(0, availableWidth - buttonWidth - spacing);
        double label = Math.Min(labelDesiredWidth, room);
        return (label + spacing, label);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        (Control? label, Control? help) = Parts();
        if (label == null) return default;
        double buttonWidth = 0, buttonHeight = 0, gap = 0;
        if (help != null && help.IsVisible)
        {
            help.Measure(Size.Infinity);
            buttonWidth = help.DesiredSize.Width;
            buttonHeight = help.DesiredSize.Height;
            gap = Spacing;
        }
        double room = double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - buttonWidth - gap);
        label.Measure(new Size(room, availableSize.Height));
        double width = label.DesiredSize.Width + (help != null && help.IsVisible ? gap + buttonWidth : 0);
        return new Size(width, Math.Max(label.DesiredSize.Height, buttonHeight));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        (Control? label, Control? help) = Parts();
        if (label == null) return finalSize;
        double buttonWidth = help != null && help.IsVisible ? help.DesiredSize.Width : 0;
        double gap = buttonWidth > 0 ? Spacing : 0;
        (double buttonX, double labelWidth) = Place(finalSize.Width, label.DesiredSize.Width, buttonWidth, gap);
        double rowHeight = Math.Max(label.DesiredSize.Height, help?.DesiredSize.Height ?? 0);
        label.Arrange(new Rect(0, (rowHeight - label.DesiredSize.Height) / 2, labelWidth, label.DesiredSize.Height));
        if (help != null && help.IsVisible)
            help.Arrange(new Rect(buttonX, (rowHeight - help.DesiredSize.Height) / 2, buttonWidth, help.DesiredSize.Height));
        return new Size(finalSize.Width, rowHeight);
    }

    private (Control? Label, Control? Help) Parts() =>
        (Children.Count > 0 ? Children[0] : null, Children.Count > 1 ? Children[1] : null);
}
