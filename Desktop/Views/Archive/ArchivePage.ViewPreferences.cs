using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class ArchivePage
{
    private static readonly string[] ThreadColumnKeys = ["identity", "title", "posts", "media", "source", "work", "lastCapture"];
    private static readonly string[] MediaColumnKeys = ["name", "size", "version", "rating"];
    private static readonly double[] ThreadDefaultWidths = [150, 0, 65, 80, 90, 110, 145];
    private static readonly double[] MediaDefaultWidths = [0, 85, 125, 130];
    private Dictionary<string, double> _archiveColumnWidths = new(StringComparer.Ordinal);
    private Dictionary<string, bool> _archiveColumnVisibility = new(StringComparer.Ordinal);
    private Dictionary<string, int> _archiveColumnOrder = new(StringComparer.Ordinal);
    private string _archiveColumnMode = "Simple";
    internal Func<IReadOnlyDictionary<string, double>, Task>? SaveArchiveColumnWidthsAsync { get; set; }
    internal Func<IReadOnlyDictionary<string, bool>, Task>? SaveArchiveColumnVisibilityAsync { get; set; }
    internal Func<IReadOnlyDictionary<string, int>, Task>? SaveArchiveColumnOrderAsync { get; set; }

    internal IReadOnlyDictionary<string, int> ArchiveColumnOrderPreference
    {
        get => _archiveColumnOrder;
        set
        {
            _archiveColumnOrder = value == null ? new(StringComparer.Ordinal)
                : value.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            ApplyArchiveColumns();
        }
    }

    internal string ArchiveColumnModePreference
    {
        get => _archiveColumnMode;
        set { _archiveColumnMode = value is "Barebones" or "Advanced" ? value : "Simple"; ApplyArchiveColumns(); UpdateLayoutMode(); }
    }

    internal IReadOnlyDictionary<string, bool> ArchiveColumnVisibilityPreference
    {
        get => _archiveColumnVisibility;
        set
        {
            _archiveColumnVisibility = value == null ? new(StringComparer.Ordinal)
                : value.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            ApplyArchiveColumns();
        }
    }

    internal IReadOnlyDictionary<string, double> ArchiveColumnWidthsPreference
    {
        get => _archiveColumnWidths;
        set
        {
            _archiveColumnWidths = value == null ? new(StringComparer.Ordinal)
                : value.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            ApplyArchiveColumns();
        }
    }

    private static string[] ColumnKeys(DataGrid grid) => grid.Name == "ArchiveMediaGrid" ? MediaColumnKeys : ThreadColumnKeys;
    private static string ColumnPreferenceKey(DataGrid grid, string column) => grid.Name + "." + column;
    private bool DefaultVisible(DataGrid grid, string key) => grid.Name == "ArchiveMediaGrid"
        ? _archiveColumnMode != "Barebones" || key is "name" or "size"
        : _archiveColumnMode switch
        {
            "Barebones" => key is "identity" or "title" or "posts" or "media",
            "Simple" => key is not "work" and not "lastCapture",
            _ => true
        };

    private bool ColumnVisible(DataGrid grid, string key)
    {
        if (key == "name") return true;
        bool visible = _archiveColumnVisibility.TryGetValue(ColumnPreferenceKey(grid, key), out bool saved)
            ? saved : DefaultVisible(grid, key);
        if (key == "identity" && !visible &&
            _archiveColumnVisibility.TryGetValue(ColumnPreferenceKey(grid, "title"), out bool title) && !title)
            return true;
        return visible;
    }

    private void ApplyArchiveColumns()
    {
        foreach (DataGrid grid in new[] { Control<DataGrid>("ArchiveThreadTable"), Control<DataGrid>("ArchiveMediaGrid") })
        {
            string[] keys = ColumnKeys(grid);
            for (int index = 0; index < keys.Length; index++)
            {
                var column = grid.Columns[index];
                column.IsVisible = ColumnVisible(grid, keys[index]);
                string semantic = ColumnPreferenceKey(grid, keys[index]);
                // Read pre-0.9 numeric widths; all new writes use stable semantic names.
                if ((_archiveColumnWidths.TryGetValue(semantic, out double width) ||
                     _archiveColumnWidths.TryGetValue(grid.Name + "." + index, out width))
                    && double.IsFinite(width) && width is >= 55 and <= 2000)
                    column.Width = new DataGridLength(Math.Max(column.MinWidth, width));
                else
                {
                    double defaultWidth = (grid.Name == "ArchiveMediaGrid" ? MediaDefaultWidths : ThreadDefaultWidths)[index];
                    column.Width = defaultWidth == 0
                        ? new DataGridLength(1, DataGridLengthUnitType.Star)
                        : new DataGridLength(defaultWidth);
                }
            }
            if (keys.Any(key => _archiveColumnOrder.ContainsKey(ColumnPreferenceKey(grid, key))))
            {
                int[] order = Enumerable.Range(0, keys.Length)
                    .OrderBy(index => _archiveColumnOrder.TryGetValue(ColumnPreferenceKey(grid, keys[index]), out int position)
                        && position >= 0 && position < keys.Length ? position : index)
                    .ThenBy(index => index).ToArray();
                for (int position = 0; position < order.Length; position++)
                    grid.Columns[order[position]].DisplayIndex = position;
            }
            else for (int index = 0; index < keys.Length; index++)
                grid.Columns[index].DisplayIndex = index;
        }
    }

    private void LocalizeArchiveColumns()
    {
        CommandBindings.Apply(Control<Button>("ArchiveThreadColumnsButton"), "table.choose-columns", iconWithLabel: true);
        CommandBindings.Apply(Control<Button>("ArchiveMediaColumnsButton"), "table.choose-columns", iconWithLabel: true);
        DataGrid threads = Control<DataGrid>("ArchiveThreadTable");
        for (int i = 0; i < ThreadColumnKeys.Length; i++)
            threads.Columns[i].Header = Texts.T("archive.columns." + ThreadColumnKeys[i]);
        DataGrid media = Control<DataGrid>("ArchiveMediaGrid");
        for (int i = 0; i < MediaColumnKeys.Length; i++)
            media.Columns[i].Header = MediaColumnKeys[i] == "rating" ? MeowChan.Desktop.MediaRatingText.Label : Texts.T("archive.columns." + MediaColumnKeys[i]);
    }

    private void OnArchiveColumnsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        DataGrid grid = button.Name == "ArchiveMediaColumnsButton"
            ? Control<DataGrid>("ArchiveMediaGrid") : Control<DataGrid>("ArchiveThreadTable");
        ContextMenu menu = new();
        string[] keys = ColumnKeys(grid);
        foreach (string key in keys)
        {
            MenuItem item = new()
            {
                Header = Texts.T("archive.columns." + key),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = ColumnVisible(grid, key),
                IsEnabled = !(key == "name" ||
                    (key is "identity" or "title" &&
                     !ColumnVisible(grid, key == "identity" ? "title" : "identity")))
            };
            item.Click += async (_, _) => await ToggleArchiveColumnAsync(grid, key, !ColumnVisible(grid, key));
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        MenuItem reset = new() { Header = Texts.T("archive.columnsReset") };
        reset.Click += async (_, _) => await ResetArchiveColumnsAsync(grid);
        menu.Items.Add(reset);
        button.ContextMenu = menu;
        menu.Open(button);
    }

    private async Task ToggleArchiveColumnAsync(DataGrid grid, string key, bool visible)
    {
        if (!visible && (key == "name" || key is "identity" or "title" &&
            !ColumnVisible(grid, key == "identity" ? "title" : "identity"))) return;
        var next = new Dictionary<string, bool>(_archiveColumnVisibility, StringComparer.Ordinal)
        { [ColumnPreferenceKey(grid, key)] = visible };
        var saveVisibility = SaveArchiveColumnVisibilityAsync;
        _archiveColumnVisibility = next;
        ApplyArchiveColumns();
        if (saveVisibility != null)
            try { await saveVisibility(next); }
            catch (Exception exception) { if (!_ended) Message(exception.Message, true); }
    }

    private async Task ResetArchiveColumnsAsync(DataGrid grid)
    {
        var visibility = new Dictionary<string, bool>(_archiveColumnVisibility, StringComparer.Ordinal);
        var widths = new Dictionary<string, double>(_archiveColumnWidths, StringComparer.Ordinal);
        var order = new Dictionary<string, int>(_archiveColumnOrder, StringComparer.Ordinal);
        var saveVisibility = SaveArchiveColumnVisibilityAsync;
        var saveWidths = SaveArchiveColumnWidthsAsync;
        var saveOrder = SaveArchiveColumnOrderAsync;
        for (int i = 0; i < ColumnKeys(grid).Length; i++)
        {
            visibility.Remove(ColumnPreferenceKey(grid, ColumnKeys(grid)[i]));
            widths.Remove(ColumnPreferenceKey(grid, ColumnKeys(grid)[i]));
            widths.Remove(grid.Name + "." + i);
            order.Remove(ColumnPreferenceKey(grid, ColumnKeys(grid)[i]));
        }
        _archiveColumnVisibility = visibility;
        _archiveColumnWidths = widths;
        _archiveColumnOrder = order;
        ApplyArchiveColumns();
        try
        {
            // Invoke each callback before yielding so each captures the originating mode.
            Task visibilitySave = saveVisibility?.Invoke(visibility) ?? Task.CompletedTask;
            Task widthSave = saveWidths?.Invoke(widths) ?? Task.CompletedTask;
            Task orderSave = saveOrder?.Invoke(order) ?? Task.CompletedTask;
            await Task.WhenAll(visibilitySave, widthSave, orderSave);
        }
        catch (Exception exception) { if (!_ended) Message(exception.Message, true); }
    }

    private async void OnArchiveColumnsReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not DataGrid grid ||
            e.Source is not Avalonia.Visual visual ||
            !visual.GetSelfAndVisualAncestors().OfType<DataGridColumnHeader>().Any()) return;
        string mode = _archiveColumnMode;
        var saveWidths = SaveArchiveColumnWidthsAsync;
        var saveOrder = SaveArchiveColumnOrderAsync;
        var startingWidths = _archiveColumnWidths;
        var startingOrder = _archiveColumnOrder;
        Dictionary<string, double> next = new(_archiveColumnWidths, StringComparer.Ordinal);
        Dictionary<string, int> nextOrder = new(_archiveColumnOrder, StringComparer.Ordinal);
        bool changed = false, orderChanged = false;
        string[] keys = ColumnKeys(grid);
        for (int index = 0; index < keys.Length; index++)
        {
            string orderKey = ColumnPreferenceKey(grid, keys[index]);
            int position = grid.Columns[index].DisplayIndex;
            if (!(nextOrder.TryGetValue(orderKey, out int oldPosition) && oldPosition == position)
                && (nextOrder.ContainsKey(orderKey) || position != index))
            {
                nextOrder[orderKey] = position;
                orderChanged = true;
            }
            var column = grid.Columns[index];
            if (!column.IsVisible || column.Width.UnitType == DataGridLengthUnitType.Star) continue;
            double width = Math.Round(column.ActualWidth, 1);
            if (!double.IsFinite(width) || width is < 55 or > 2000) continue;
            string key = ColumnPreferenceKey(grid, keys[index]);
            if (next.TryGetValue(key, out double prior) && Math.Abs(prior - width) < 1) continue;
            next[key] = width;
            changed = true;
        }
        if (!changed && !orderChanged) return;
        try
        {
            Task orderSave = orderChanged && saveOrder != null ? saveOrder(nextOrder) : Task.CompletedTask;
            Task widthSave = changed && saveWidths != null ? saveWidths(next) : Task.CompletedTask;
            await Task.WhenAll(orderSave, widthSave);
            if (_archiveColumnMode == mode)
            {
                if (orderChanged && ReferenceEquals(SaveArchiveColumnOrderAsync, saveOrder)
                    && ReferenceEquals(_archiveColumnOrder, startingOrder)) _archiveColumnOrder = nextOrder;
                if (changed && ReferenceEquals(SaveArchiveColumnWidthsAsync, saveWidths)
                    && ReferenceEquals(_archiveColumnWidths, startingWidths)) _archiveColumnWidths = next;
            }
        }
        catch (Exception exception) { if (!_ended) Message(exception.Message, true); }
    }
}
