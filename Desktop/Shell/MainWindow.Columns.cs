using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Primitives.PopupPositioning;
using Avalonia.Input;
using Avalonia.VisualTree;
using Avalonia.Interactivity;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using Serilog;

namespace MeowChan;

public partial class MainWindow
{
    private void LocalizeDownloadsColumnsButton()
    {
        if (!_workspaceInitialized) return;
        CommandBindings.Apply(DownloadsColumnsButton, "table.choose-columns", iconWithLabel: true);
    }

    private void ToggleDownloadColumns() => ToggleDownloadColumns(anchor: null);

    private void ToggleDownloadColumns(Control? anchor)
    {
        var button = DownloadsColumnsButton;
        if (PopupToggle.ConsumeActivation(button))
        {
            button.IsChecked = false;
            return;
        }
        bool boards = DownloadsTabs.SelectedIndex == 1;
        ContextMenu menu = BuildDownloadColumnsMenu(boards ? "BoardsDataGrid" : "ThreadsDataGrid");
        // Barebones and Simple keep "Columns" under "More": with that list closed the button is not on screen, and the shortcut
        // opens the menu at the visible "More" of the same bar (or at the table when the bar shows no "More").
        anchor ??= DownloadColumnsAnchor(button, boards);
        if (!ReferenceEquals(anchor, button))
        {
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.AnchorAndGravity;
            bool table = anchor is DataGrid;
            menu.PlacementAnchor = table ? PopupAnchor.TopRight : PopupAnchor.BottomRight;
            menu.PlacementGravity = PopupGravity.BottomLeft;
        }
        button.ContextMenu = menu;
        menu.Closed += (_, _) =>
        {
            if (!ReferenceEquals(button.ContextMenu, menu)) return;
            button.ContextMenu = null;
            button.IsChecked = false;
            ReturnFocusFromDownloadColumns(anchor);
        };
        menu.AddHandler(InputElement.KeyDownEvent, (_, key) =>
        {
            if (ShortcutService.IsCapturing) return;
            using var context = EffectiveShortcutBindings.EnterKeyContext("downloads");
            if (!ShortcutService.Current.Matches(key, "table.choose-columns")) return;
            key.Handled = true;
            menu.Close();
        }, RoutingStrategies.Tunnel);
        PopupToggle.Track(button, menu);
        menu.Open(anchor);
        button.IsChecked = menu.IsOpen;
    }

    private Control DownloadColumnsAnchor(Button button, bool boards)
    {
        static bool OnScreen(Control control) => control.IsAttachedToVisualTree() && control.IsEffectivelyVisible;
        if (OnScreen(button)) return button;
        return WorkspaceListActions.More is { } more && OnScreen(more) ? more : DownloadList(boards);
    }

    // The buttons of the bar take no focus: a closed column menu leaves the keyboard in the table it arranges, unless the
    // focus already went to another control on purpose (a click into a field, a chosen entry's own target).
    private void ReturnFocusFromDownloadColumns(Control anchor) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_isClosing || DownloadsColumnsButton.ContextMenu != null || _selectedPage != WorkspacePage.Downloads) return;
        DataGrid list = DownloadList(DownloadsTabs.SelectedIndex == 1);
        if (list.IsKeyboardFocusWithin || !list.IsEffectivelyVisible) return;
        if (FocusManager?.GetFocusedElement() is Avalonia.Visual focused && focused.IsAttachedToVisualTree()
            && !ReferenceEquals(focused, anchor) && !focused.IsVisualAncestorOf(anchor)
            && !WorkspaceListActions.IsVisualAncestorOf(focused)) return;
        list.Focus();
    }, Avalonia.Threading.DispatcherPriority.Input);

    // A right-click on a column header of either downloads table opens the same column menu as the "Columns" button; the keyboard
    // can run the column command separately. Rows keep their own menu (MainWindow.Selection.cs).
    private void InitializeDownloadHeaderMenus()
    {
        WorkspaceListActions.OverflowKeyDown += OnDownloadOverflowKeyDown;
        foreach (string gridName in new[] { "ThreadsDataGrid", "BoardsDataGrid" })
        {
            DataGrid grid = WorkspaceControl<DataGrid>(gridName);
            grid.AddHandler(Avalonia.Input.InputElement.PointerPressedEvent, (_, e) =>
            {
                // The button is the offer of the work view: where it is hidden, the header does not bring the choice back.
                if (!ContextClick.IsPress(e, grid) || _isClosing || !_restoreCompleted || !DownloadsColumnsButton.IsVisible) return;
                if ((e.Source as Avalonia.Visual)?.GetSelfAndVisualAncestors().OfType<DataGridColumnHeader>().FirstOrDefault() is not { } header) return;
                e.Handled = true;
                ContextMenu menu = BuildDownloadColumnsMenu(gridName);
                header.ContextMenu = menu;
                menu.Closed += (_, _) => { if (ReferenceEquals(header.ContextMenu, menu)) header.ContextMenu = null; };
                menu.Open(header);
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        }
    }

    private void OnDownloadOverflowKeyDown(object? sender, KeyEventArgs e)
    {
        if (ShortcutService.IsCapturing || !CanFocusDownloadSearch || !DownloadsColumnsButton.IsVisible
            || !CanExecuteShellPowerCommand(e.Source, e.Key) || !DownloadsMatches(e, "table.choose-columns")) return;
        e.Handled = true;
        ToggleDownloadColumnsFromShortcut();
    }

    private void ToggleDownloadColumnsFromShortcut()
    {
        if (WorkspaceListActions.More is { IsChecked: true } more
            && WorkspaceListActions.OverflowHost?.Children.Contains(DownloadsColumnsButton) == true)
        {
            // An independent popup rooted in More's popup cannot take keyboard focus on every platform.
            more.IsChecked = false;
            ToggleDownloadColumns(more);
            return;
        }
        ToggleDownloadColumns();
    }

    /// <summary>The column menu of a downloads table: one switch per offered column (the last visible one cannot be hidden), then the reset.</summary>
    internal ContextMenu BuildDownloadColumnsMenu(string gridName)
    {
        DataGrid grid = WorkspaceControl<DataGrid>(gridName);
        string mode = _config.WorkspaceMode;
        ContextMenuBuilder builder = new ContextMenuBuilder();
        List<(string Key, string? Help)> help = new();
        foreach (DataGridColumn column in grid.Columns)
        {
            if (!WorkViewPolicy.ColumnOffered(WorkViewPolicy.DownloadColumnId(gridName, column.SortMemberPath), ConfiguredWorkView)) continue;
            string key = gridName + "." + column.SortMemberPath;
            bool visible = column.IsVisible;
            bool available = !visible || CanHideDownloadColumn(gridName, column.SortMemberPath,
                grid.Columns.Where(candidate => candidate.IsVisible).Select(candidate => candidate.SortMemberPath).ToArray());
            builder.Check(key, column.Header?.ToString() ?? key, visible,
                async () => await SetDownloadColumnVisibleAsync(gridName, key, !visible, mode),
                available: available, reason: available ? null : Texts.T("archive.columns.required"));
            if (available && IsAdvancedWorkColumn(column))
                help.Add((key, Texts.T(column.SortMemberPath == nameof(ThreadRow.MissingMedia) ? "column.missingMediaHint" : "column.earliestRetryHint")));
        }
        builder.Section().Item("archive.columnsReset", Texts.T("archive.columnsReset"),
            () => _ = ResetDownloadColumnsAsync(gridName, mode));
        help.Add(("archive.columnsReset", Texts.T("archive.columnsReset.hint")));
        ContextMenu menu = builder.Build()!;
        foreach ((string key, string? text) in help)
        {
            if (menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, key)) is not { } item || text == null) continue;
            ToolTip.SetTip(item, text);
            Avalonia.Automation.AutomationProperties.SetHelpText(item, text);
        }
        return menu;
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
                    column.Width = new DataGridLength(WorkViewDensity.DownloadCompletionWidth(ConfiguredWorkView));
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
