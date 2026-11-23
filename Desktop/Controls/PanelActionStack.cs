using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MeowChan.Desktop.Commands;

namespace MeowChan.Desktop.Controls;

internal sealed class PanelActionStack : StackPanel
{
    protected override Type StyleKeyOverride => typeof(StackPanel);

    internal PanelActionStack(string name)
    {
        Name = name;
        Spacing = 8;
    }

    internal static void Stretch(Button button)
    {
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Height = WorkspaceMetrics.InputHeight;
    }

    internal static void Present(Button button, string icon)
    {
        Stretch(button);
        if (button.Content is not string label) return;
        TextBlock text = new()
        {
            Text = label, Classes = { "panel-action-label" }, TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1, VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(text, 1);
        Border mark = (Border)CommandBindings.IconFromKey(icon, 16);
        mark.Bind(Border.BackgroundProperty, text.GetObservable(TextBlock.ForegroundProperty));
        button.Content = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Children = { mark, text } };
        ToolTip.SetTip(button, ToolTip.GetTip(button) ?? label);
    }
}
