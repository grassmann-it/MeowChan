using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
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

    internal static void SizeButton(Button button)
    {
        button.Classes.Set("panel-action", true);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Padding = new Thickness(12, 0);
        button.MinWidth = 96;
        button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        button.Height = WorkspaceMetrics.InputHeight;
    }

    internal static void Present(Button button, string icon)
    {
        SizeButton(button);
        button.ContentTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((content, _) =>
        {
            TextBlock text = new()
            {
                Text = content?.ToString(), Classes = { "panel-action-label" }, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1, VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(text, 1);
            Border mark = (Border)CommandBindings.IconFromKey(icon, 16);
            mark.Bind(Border.BackgroundProperty, text.GetObservable(TextBlock.ForegroundProperty));
            return new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8, Children = { mark, text } };
        });
        ToolTip.SetTip(button, ToolTip.GetTip(button) ?? button.Content);
    }
}
