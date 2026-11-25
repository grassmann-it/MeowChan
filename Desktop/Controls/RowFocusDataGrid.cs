using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MeowChan.Desktop.Controls;

internal sealed class RowFocusDataGrid : DataGrid
{
    private const string CurrentRowClass = "current-cell-row";
    private readonly HashSet<DataGridRow> _realizedRows = new();

    protected override Type StyleKeyOverride => typeof(DataGrid);

    public RowFocusDataGrid()
    {
        LoadingRow += (_, e) =>
        {
            _realizedRows.Add(e.Row);
            SetRowFocus(e.Row, CurrentItem);
        };
        UnloadingRow += (_, e) =>
        {
            _realizedRows.Remove(e.Row);
            e.Row.Classes.Remove(CurrentRowClass);
        };
        CurrentCellChanged += (_, _) =>
        {
            object? current = CurrentItem;
            foreach (DataGridRow row in _realizedRows)
                SetRowFocus(row, current);
        };
        AddHandler(KeyDownEvent, OnRowActionTab, RoutingStrategies.Tunnel);
    }

    private void OnRowActionTab(object? sender, KeyEventArgs e)
    {
        // Arrow keys still navigate the grid. Tab can enter the action in the current cell.
        if (e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None
            || (!ReferenceEquals(e.Source, this) && e.Source is not DataGridCell)) return;
        DataGridRow? row = _realizedRows.FirstOrDefault(row => row.Classes.Contains(CurrentRowClass));
        DataGridCell? cell = row?.GetVisualDescendants().OfType<DataGridCell>()
            .FirstOrDefault(cell => cell.Classes.Contains(":current"));
        Button? action = cell?.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => button.Classes.Contains("row-action") && button.IsEffectivelyVisible && button.IsEffectivelyEnabled);
        if (action?.Focus(NavigationMethod.Tab) == true) e.Handled = true;
    }

    private static void SetRowFocus(DataGridRow row, object? current) =>
        row.Classes.Set(CurrentRowClass, current != null && ReferenceEquals(row.DataContext, current));
}
