using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;

namespace MeowChan;

public partial class ArchivePage
{
    private Dictionary<string, double> _archiveColumnWidths = new(StringComparer.Ordinal);
    internal Func<IReadOnlyDictionary<string, double>, Task>? SaveArchiveColumnWidthsAsync { get; set; }

    internal IReadOnlyDictionary<string, double> ArchiveColumnWidthsPreference
    {
        get => _archiveColumnWidths;
        set
        {
            _archiveColumnWidths = value == null
                ? new(StringComparer.Ordinal) : value.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            ApplyArchiveColumnWidths(Control<DataGrid>("ArchiveThreadTable"));
            ApplyArchiveColumnWidths(Control<DataGrid>("ArchiveMediaGrid"));
        }
    }

    private void ApplyArchiveColumnWidths(DataGrid grid)
    {
        for (int index = 0; index < grid.Columns.Count; index++)
            if (_archiveColumnWidths.TryGetValue(grid.Name + "." + index, out double width)
                && double.IsFinite(width) && width is >= 24 and <= 2000)
                grid.Columns[index].Width = new DataGridLength(width);
    }

    private async void OnArchiveColumnsReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not DataGrid grid || SaveArchiveColumnWidthsAsync == null) return;
        Dictionary<string, double> next = new(_archiveColumnWidths, StringComparer.Ordinal);
        bool changed = false;
        for (int index = 0; index < grid.Columns.Count; index++)
        {
            double width = Math.Round(grid.Columns[index].ActualWidth, 1);
            if (!double.IsFinite(width) || width is < 24 or > 2000) continue;
            string key = grid.Name + "." + index;
            if (next.TryGetValue(key, out double prior) && Math.Abs(prior - width) < 1) continue;
            next[key] = width;
            changed = true;
        }
        if (!changed) return;
        try
        {
            await SaveArchiveColumnWidthsAsync(next);
            _archiveColumnWidths = next;
        }
        catch (Exception exception) { if (!_ended) Message(exception.Message, true); }
    }
}
