using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan;

// The context menu of the source table: right-click on a row, the Menu key or Shift+F10 on the selected row of the table (wide
// layout) or on an entry of the compact list (narrow layout). The row is the one under the pointer, else the selected or focused one.
public partial class StatisticsPage
{
    private ContextMenuHost? _gridMenu, _compactMenu;

    private void InitializeRowMenus()
    {
        ContextMenuTarget gridTarget = new(StatisticsGrid);
        _gridMenu = new ContextMenuHost(StatisticsGrid, () => ComposeRowMenu(StatisticsGrid, gridTarget.IsPointer
            ? gridTarget.Find<DataGridRow>()?.DataContext as StatisticsRow : StatisticsGrid.SelectedItem as StatisticsRow), () => StatisticsGrid.Focus());
        ContextMenuTarget compactTarget = new(StatisticsCompactList);
        _compactMenu = new ContextMenuHost(StatisticsCompactList, () => ComposeRowMenu(StatisticsCompactList,
            compactTarget.Find<Expander>()?.DataContext as StatisticsRow), () => StatisticsCompactList.Focus());
        // Interaction audit 1.1.0: Ctrl/Cmd+C on the selected row is the menu's "Copy row" (tunnel: before the table copies its cell texts).
        StatisticsGrid.AddHandler(KeyDownEvent, OnStatisticsCopyKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
    }

    private void OnStatisticsCopyKey(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (e.Handled || _ended || MeowChan.Desktop.Commands.ShortcutService.IsCapturing
            || !MainWindow.IsListCommandSource(e.Source, StatisticsGrid)
            || !MeowChan.Desktop.Commands.CommandBindings.Matches(e, StatisticsRowMenu.CopyRow)
            || StatisticsGrid.SelectedItem is not StatisticsRow row) return;
        e.Handled = true;
        int retention = _last?.RetentionDays ?? Config.StatsRetentionDaysDefault;
        _ = CopyMenuTextAsync(StatisticsGrid, StatisticsRowMenu.RowText(row, retention));
    }

    /// <summary>The menu hosts of the table and of the compact list; read by the smokes.</summary>
    internal ContextMenuHost? TableMenuHost => _gridMenu;
    internal ContextMenuHost? CompactMenuHost => _compactMenu;

    /// <summary>The menu of a row as the page would open it now (null when there is no row or the page ended); also the test seam.</summary>
    internal ContextMenuBuilder? ComposeRowMenu(Control owner, StatisticsRow? row)
    {
        if (_ended || row == null) return null;
        if (ReferenceEquals(owner, StatisticsGrid) && !ReferenceEquals(StatisticsGrid.SelectedItem, row)) StatisticsGrid.SelectedItem = row;
        int retention = _last?.RetentionDays ?? Config.StatsRetentionDaysDefault;
        return StatisticsRowMenu.Compose(row.Name, row.IsTotal, _hub != null && !_resetting,
            copyRow: () => _ = CopyMenuTextAsync(owner, StatisticsRowMenu.RowText(row, retention)),
            copySource: () => _ = CopyMenuTextAsync(owner, row.Name),
            reset: OpenConfirmation);
    }

    private async System.Threading.Tasks.Task CopyMenuTextAsync(Control owner, string text)
    {
        if (await MenuClipboard.CopyAsync(owner, text)) return;
        if (_ended) return;
        // The reset result's line is the page's one transient message.
        StatisticsStatus.Text = Texts.T("ctxmenu.copyFailed");
        ResetStatusPanel.IsVisible = true;
        _statusHide?.Dispose();
        _statusHide = Avalonia.Threading.DispatcherTimer.RunOnce(() => ResetStatusPanel.IsVisible = false, TimeSpan.FromSeconds(5));
    }

    private void DisposeRowMenus()
    {
        _gridMenu?.Dispose();
        _compactMenu?.Dispose();
    }
}
