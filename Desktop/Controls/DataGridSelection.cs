using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Selection writes that respect the selection mode of a <see cref="DataGrid"/>. Avalonia's <c>DataGrid.SelectedItems</c> throws on
/// every write (Clear, Add, Remove) while the grid is in Single mode ("Can only change SelectedItems collection in Extended selection
/// mode"), even when there is nothing to remove. Code that clears or sets the selection of a grid whose mode it does not own goes
/// through these helpers; reading <c>SelectedItems</c> is always safe.
/// </summary>
internal static class DataGridSelection
{
    /// <summary>Drops every selected row, in any selection mode.</summary>
    internal static void Clear(DataGrid grid)
    {
        if (grid.SelectionMode != DataGridSelectionMode.Single && grid.SelectedItems.Count > 0) grid.SelectedItems.Clear();
        // The lead (SelectedItem) is dropped in every mode: in Single mode it is the whole selection.
        if (grid.SelectedItem != null) grid.SelectedItem = null;
    }

    /// <summary>
    /// Selects <paramref name="items"/>: all of them in Extended mode, only the first one in Single mode (the lead, if it is among
    /// them). Items the grid already shows selected are left alone.
    /// </summary>
    internal static void Select(DataGrid grid, IReadOnlyList<object> items, object? lead = null)
    {
        if (items.Count == 0) { Clear(grid); return; }
        object first = lead != null && items.Contains(lead) ? lead : items[0];
        if (!ReferenceEquals(grid.SelectedItem, first)) grid.SelectedItem = first;
        if (grid.SelectionMode == DataGridSelectionMode.Single) return;
        foreach (object item in items)
            if (!grid.SelectedItems.Contains(item)) grid.SelectedItems.Add(item);
    }
}
