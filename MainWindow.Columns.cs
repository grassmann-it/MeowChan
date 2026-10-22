using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using Serilog;

namespace MeowChan;

public partial class MainWindow
{
    private void LocalizeDownloadsColumnsButton()
    {
        if (!_workspaceInitialized) return;
        CommandBindings.Apply(WorkspaceControl<Button>("DownloadsColumnsButton"), "table.choose-columns", iconWithLabel: true);
    }

    private void OnDownloadsColumnsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        string gridName = WorkspaceControl<TabControl>("DownloadsTabs").SelectedIndex == 1
            ? "BoardsDataGrid" : "ThreadsDataGrid";
        DataGrid grid = WorkspaceControl<DataGrid>(gridName);
        string mode = _config.WorkspaceMode;
        ContextMenu menu = new();
        foreach (DataGridColumn column in grid.Columns)
        {
            if (IsAdvancedWorkColumn(column) && mode != "Advanced") continue;
            string key = gridName + "." + column.SortMemberPath;
            MenuItem item = new()
            {
                Header = column.Header,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
                IsEnabled = !column.IsVisible || CanHideDownloadColumn(gridName, column.SortMemberPath,
                    grid.Columns.Where(candidate => candidate.IsVisible).Select(candidate => candidate.SortMemberPath).ToArray())
            };
            if (IsAdvancedWorkColumn(column))
            {
                string help = Texts.T(column.SortMemberPath == nameof(ThreadRow.MissingMedia) ? "column.missingMediaHint" : "column.earliestRetryHint");
                ToolTip.SetTip(item, help);
                Avalonia.Automation.AutomationProperties.SetHelpText(item, help);
            }
            bool visible = column.IsVisible;
            item.Click += async (_, _) => await SetDownloadColumnVisibleAsync(gridName, key, !visible, mode);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        MenuItem reset = new() { Header = Texts.T("archive.columnsReset") };
        reset.Click += async (_, _) => await ResetDownloadColumnsAsync(gridName, mode);
        menu.Items.Add(reset);
        button.ContextMenu = menu;
        menu.Open(button);
    }

    private async Task SetDownloadColumnVisibleAsync(string gridName, string key, bool visible, string mode)
    {
        DataGrid grid = WorkspaceControl<DataGrid>(gridName);
        if (!visible && _config.WorkspaceMode == mode &&
            !CanHideDownloadColumn(gridName, key[(gridName.Length + 1)..],
                grid.Columns.Where(column => column.IsVisible).Select(column => column.SortMemberPath).ToArray())) return;
        Dictionary<string, bool> previous = new(_config.GetLayoutProfile(mode).ColumnVisibility, StringComparer.Ordinal);
        _config.GetLayoutProfile(mode).ColumnVisibility[key] = visible;
        if (_config.WorkspaceMode == mode) ApplyWorkspacePreferences();
        try
        {
            await _session.SaveViewPreferencesAsync(current => current.GetLayoutProfile(mode).ColumnVisibility[key] = visible);
        }
        catch (Exception error)
        {
            _config.GetLayoutProfile(mode).ColumnVisibility = previous;
            if (_config.WorkspaceMode == mode) ApplyWorkspacePreferences();
            Log.Warning(error, "Could not save download column visibility.");
        }
    }

    internal static bool CanHideDownloadColumn(string gridName, string columnKey, IReadOnlyCollection<string> visibleKeys)
    {
        if (visibleKeys.Count <= 1) return false;
        // A source alone identifies neither a thread nor a board within that source.
        string[] identityKeys = gridName == "BoardsDataGrid"
            ? [nameof(BoardRow.NiceName)]
            : [nameof(ThreadRow.ThreadNumber), nameof(ThreadRow.Title)];
        return !identityKeys.Contains(columnKey) || identityKeys.Count(visibleKeys.Contains) > 1;
    }

    internal static string? RequiredDownloadIdentityToRestore(string gridName, IReadOnlyCollection<string> visibleKeys)
    {
        if (gridName == "BoardsDataGrid")
            return visibleKeys.Contains(nameof(BoardRow.NiceName)) ? null : nameof(BoardRow.NiceName);
        return visibleKeys.Contains(nameof(ThreadRow.Title)) || visibleKeys.Contains(nameof(ThreadRow.ThreadNumber))
            ? null : nameof(ThreadRow.Title);
    }

    private async Task ResetDownloadColumnsAsync(string gridName, string mode)
    {
        await FlushWindowPreferencesAsync();
        PresentationProfile profile = _config.GetLayoutProfile(mode);
        Dictionary<string, bool> previous = new(_config.GetLayoutProfile(mode).ColumnVisibility, StringComparer.Ordinal);
        Dictionary<string, double> oldWidths = new(profile.ColumnWidths, StringComparer.Ordinal);
        Dictionary<string, int> oldOrder = new(profile.ColumnOrder, StringComparer.Ordinal);
        void RemoveGridPreferences(PresentationProfile target)
        {
            foreach (string key in target.ColumnVisibility.Keys.Where(key => key.StartsWith(gridName + ".", StringComparison.Ordinal)).ToArray())
                target.ColumnVisibility.Remove(key);
            foreach (string key in target.ColumnWidths.Keys.Where(key => key.StartsWith(gridName + ".", StringComparison.Ordinal)).ToArray())
                target.ColumnWidths.Remove(key);
            foreach (string key in target.ColumnOrder.Keys.Where(key => key.StartsWith(gridName + ".", StringComparison.Ordinal)).ToArray())
                target.ColumnOrder.Remove(key);
        }
        RemoveGridPreferences(profile);
        if (_config.WorkspaceMode == mode)
        {
            ApplyWorkspacePreferences();
            RestoreDownloadColumnGeometry(gridName);
        }
        try
        {
            await _session.SaveViewPreferencesAsync(current => RemoveGridPreferences(current.GetLayoutProfile(mode)));
        }
        catch (Exception error)
        {
            profile.ColumnVisibility = previous;
            profile.ColumnWidths = oldWidths;
            profile.ColumnOrder = oldOrder;
            if (_config.WorkspaceMode == mode)
            {
                ApplyWorkspacePreferences();
                RestoreDownloadColumnGeometry(gridName);
            }
            Log.Warning(error, "Could not reset download columns.");
        }
    }

    private void RestoreDownloadColumnGeometry(string gridName)
    {
        _applyingColumnProfile = true;
        try
        {
            DataGrid grid = WorkspaceControl<DataGrid>(gridName);
            foreach (DataGridColumn column in grid.Columns)
            {
                string key = gridName + "." + column.SortMemberPath;
                if (_config.ColumnWidths.TryGetValue(key, out double width))
                    column.Width = new DataGridLength(width);
                else if (gridName == "ThreadsDataGrid" && column.SortMemberPath == nameof(ThreadRow.Completion))
                    column.Width = new DataGridLength(_config.WorkspaceMode == "Advanced" ? 140 : 300);
                else if (_defaultColumnWidths.TryGetValue(key, out DataGridLength defaultWidth))
                    column.Width = defaultWidth;
            }
            DataGridColumn[] order = grid.Columns.OrderBy(column =>
                _config.ColumnOrder.TryGetValue(gridName + "." + column.SortMemberPath, out int index)
                    ? index : grid.Columns.IndexOf(column)).ToArray();
            for (int index = 0; index < order.Length; index++) order[index].DisplayIndex = index;
        }
        finally { _applyingColumnProfile = false; }
    }
}
