using System;
using System.Collections.Generic;
using Avalonia.Controls;

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
    }

    private static void SetRowFocus(DataGridRow row, object? current) =>
        row.Classes.Set(CurrentRowClass, current != null && ReferenceEquals(row.DataContext, current));
}
