using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Layout;

namespace MeowChan.Desktop;

/// <summary>
/// SP1: the one settings row for the rows that are built in code (the markup of the settings page writes the same structure by hand, and a
/// contract test holds both to it): <c>Border.settings-divider &gt; Grid.settings-row[*,280 / Auto,Auto] &gt; (StackPanel.settings-label, control in column 1)</c>.
/// The label cell holds the label and, under it, the description; the control is in the fixed 280 dip column.
/// </summary>
internal static class SettingsRowFactory
{
    internal const string DividerClass = "settings-divider";
    internal const string RowClass = "settings-row";
    internal const string LabelClass = "settings-label";
    internal const string ControlColumnWidth = "280";

    internal static Border Create(IEnumerable<Control> labelCell, Control editor)
    {
        var cell = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        cell.Classes.Add(LabelClass);
        foreach (Control child in labelCell) cell.Children.Add(child);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*," + ControlColumnWidth),
            RowDefinitions = new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 24
        };
        grid.Classes.Add(RowClass);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(cell);
        grid.Children.Add(editor);
        var border = new Border { Child = grid };
        border.Classes.Add(DividerClass);
        return border;
    }
}
