using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

internal sealed class PanelActionStack : MinLineWrapPanel
{
    internal PanelActionStack(string name)
    {
        Name = name;
        ItemSpacing = LineSpacing = 8;
    }

    internal static void SizeButton(Button button, bool compactSpacing = false)
    {
        button.Classes.Set("panel-action", true);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(compactSpacing ? 8 : 12, 0);
        button.MinWidth = 96;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Height = WorkspaceMetrics.InputHeight;
    }

    internal static void Present(Button button, string icon, bool compactLabel = false, bool compactSpacing = false,
        bool allowIconOnly = false)
    {
        SizeButton(button, compactSpacing);
        double labelPadding = button.Padding.Left + button.Padding.Right;
        double gap = compactSpacing ? 4 : 8;
        if (allowIconOnly)
        {
            button.MinWidth = WorkspaceMetrics.InputHeight;
            button.Padding = new Thickness(0);
        }
        button.ContentTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((content, _) =>
        {
            TextBlock text = new()
            {
                Text = content?.ToString(), Classes = { "panel-action-label" }, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1, VerticalAlignment = VerticalAlignment.Center
            };
            text.Classes.Set("compact-action-label", compactLabel);
            Grid.SetColumn(text, 1);
            Border mark = (Border)CommandBindings.IconFromKey(icon, 16);
            mark.Bind(Border.BackgroundProperty, text.GetObservable(TextBlock.ForegroundProperty));
            if (allowIconOnly) return new FittingAction(mark, text, gap, labelPadding);
            return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = gap, Children = { mark, text } };
        });
        ToolTip.SetTip(button, ToolTip.GetTip(button) ?? button.Content);
    }

    private sealed class FittingAction : Border
    {
        private readonly Control _icon;
        private readonly TextBlock _label;
        private readonly Border _caption;
        private readonly double _labelPadding;

        internal FittingAction(Control icon, TextBlock label, double gap, double labelPadding)
        {
            _icon = icon;
            _label = label;
            _caption = new Border { Child = label, Margin = new Thickness(gap, 0, 0, 0) };
            _labelPadding = labelPadding;
            Child = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                Children = { icon, _caption } };
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            // The label stays measurable inside its collapsed caption, including after resizing or changing language.
            _icon.Measure(Size.Infinity);
            _label.Measure(Size.Infinity);
            double fullWidth = Math.Max(96 - 2, _icon.DesiredSize.Width + _caption.Margin.Left + _label.DesiredSize.Width + _labelPadding);
            _caption.IsVisible = fullWidth <= availableSize.Width;
            Size content = base.MeasureOverride(availableSize);
            return content.WithWidth(_caption.IsVisible ? fullWidth : content.Width);
        }
    }
}
