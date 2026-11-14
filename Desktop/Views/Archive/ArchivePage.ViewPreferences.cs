using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

public partial class ArchivePage
{
    private static readonly string[] ThreadColumnKeys = ["identity", "title", "posts", "media", "source", "work", "lastCapture", "tags"];
    private static readonly string[] MediaColumnKeys = ["name", "size", "version", "rating"];
    private static readonly double[] ThreadDefaultWidths = [150, 0, 65, 80, 90, 110, 145, 170];
    private static readonly double[] MediaDefaultWidths = [0, 85, 125, 130];
    private Dictionary<string, double> _archiveColumnWidths = new(StringComparer.Ordinal);
    private Dictionary<string, bool> _archiveColumnVisibility = new(StringComparer.Ordinal);
    private Dictionary<string, int> _archiveColumnOrder = new(StringComparer.Ordinal);
    private string _archiveColumnMode = WorkViewSetting.SimpleName;
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
        set
        {
            _archiveColumnMode = WorkViewSetting.Parse(value, WorkView.Simple, exact: true).ToSetting();
            bool viewChanged = NoteArchiveWorkView();
            ApplyArchiveColumns();
            ApplyArchiveWorkView(viewChanged);
            UpdateLayoutMode();
            ApplyReaderWorkView();
        }
    }

    /// <summary>The page's work view; an unknown setting is Simple, as it always was here.</summary>
    private WorkView ArchiveWorkView => WorkViewSetting.Parse(_archiveColumnMode, WorkView.Simple, exact: true);

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
    // Tags is appended as the last key: saved numeric widths and column orders of older versions stay valid.
    // The defaults per view are WorkViewPolicy.Archive; an unknown mode text shows every column, as it always did.
    internal static bool DefaultColumnVisible(string gridName, string mode, string key) =>
        WorkViewPolicy.DefaultColumnVisible(WorkViewPolicy.ArchiveColumnId(gridName, key), WorkViewSetting.Parse(mode, WorkView.Advanced, exact: true));

    private bool ColumnVisible(DataGrid grid, string key) => ResolveColumnVisible(grid.Name!, _archiveColumnMode, _archiveColumnVisibility, key);

    // A saved choice wins; a column the saved preferences never heard of follows the mode default.
    internal static bool ResolveColumnVisible(string gridName, string mode, IReadOnlyDictionary<string, bool> saved, string key)
    {
        if (key == "name") return true;
        bool visible = saved.TryGetValue(gridName + "." + key, out bool chosen) ? chosen : DefaultColumnVisible(gridName, mode, key);
        if (key == "identity" && !visible &&
            saved.TryGetValue(gridName + ".title", out bool title) && !title)
            return true;
        return visible;
    }

    // Saved positions win; a column without one (older preferences) keeps its natural place after them.
    internal static int[] ResolveColumnOrder(string gridName, string[] keys, IReadOnlyDictionary<string, int> saved)
    {
        if (!keys.Any(key => saved.ContainsKey(gridName + "." + key))) return Enumerable.Range(0, keys.Length).ToArray();
        return Enumerable.Range(0, keys.Length)
            .OrderBy(index => saved.TryGetValue(gridName + "." + keys[index], out int position)
                && position >= 0 && position < keys.Length ? position : index)
            .ThenBy(index => index).ToArray();
    }

    private void ApplyArchiveColumns()
    {
        foreach (DataGrid grid in new[] { ArchiveThreadTable, ArchiveMediaGrid })
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
            int[] order = ResolveColumnOrder(grid.Name!, keys, _archiveColumnOrder);
            for (int position = 0; position < order.Length; position++)
                grid.Columns[order[position]].DisplayIndex = position;
        }
    }

    private void LocalizeArchiveColumns()
    {
        CommandBindings.Apply(ArchiveThreadColumnsButton, "table.choose-columns", iconWithLabel: true);
        CommandBindings.Apply(ArchiveMediaColumnsButton, "table.choose-columns", iconWithLabel: true);
        DataGrid threads = ArchiveThreadTable;
        for (int i = 0; i < ThreadColumnKeys.Length; i++)
            threads.Columns[i].Header = Texts.T("archive.columns." + ThreadColumnKeys[i]);
        DataGrid media = ArchiveMediaGrid;
        for (int i = 0; i < MediaColumnKeys.Length; i++)
            media.Columns[i].Header = MediaColumnKeys[i] == "rating" ? MeowChan.Desktop.MediaRatingText.Label : Texts.T("archive.columns." + MediaColumnKeys[i]);
    }

    private void OnArchiveColumnsClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        DataGrid grid = button.Name == "ArchiveMediaColumnsButton"
            ? ArchiveMediaGrid : ArchiveThreadTable;
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
