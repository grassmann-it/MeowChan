using System;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.WorkViews;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// C16: the queue table keeps its preview useful but optional. U05: the toolbar carries it as a direct "Thumbnails" toggle
/// (the table's only optional column); the header's context menu stays as the same keyboard and pointer path as the other shared menus.
/// Hiding the preview collapses its column rather than changing the row's semantic order.
/// </summary>
public sealed partial class TaggingPage
{
    private readonly ToggleButton _columnsButton = new() { Name = "TaggingColumnsButton" };
    private bool _showPreviewColumn = true;
    private ContextMenuHost? _columnMenuHost;

    internal bool ShowPreviewColumn => _showPreviewColumn;
    internal Grid ColumnHeader => _columnHeader;
    internal TaggingControlRow ListControls => _listControls;
    internal ToggleButton ColumnsButton => _columnsButton;
    internal Action<bool>? SavePreviewColumn { get; set; }

    private void InitializeColumns()
    {
        WorkViewElement.SetId(_columnsButton, "tagging.columns");
        _columnsButton.IsChecked = _showPreviewColumn;
        _columnsButton.Click += (_, _) => SetPreviewColumnVisible(_columnsButton.IsChecked == true);
        _columnMenuHost = new ContextMenuHost(_columnHeader, ComposeColumnMenu, fallbackFocus: () => FocusContent());
    }

    internal void RestorePreviewColumn(bool visible) => SetPreviewColumnVisible(visible, save: false);

    internal ContextMenuModel? ColumnMenuModel() => ComposeColumnMenu()?.ToModel();

    private ContextMenuBuilder? ComposeColumnMenu()
    {
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("command.table.choose-columns"));
        menu.Check("tagging.columns.preview", CommandCatalog.Label("tagging.preview-column"), _showPreviewColumn,
            () => SetPreviewColumnVisible(!_showPreviewColumn));
        return menu;
    }

    private void SetPreviewColumnVisible(bool visible, bool save = true)
    {
        _columnsButton.IsChecked = visible; // the toggle and the header menu's check always show the same state
        if (_showPreviewColumn == visible) return;
        _showPreviewColumn = visible;
        LocalizeColumns();
        _list.ItemTemplate = RowTemplate();
        Render();
        if (save) SavePreviewColumn?.Invoke(visible);
    }

    private void EndColumns()
    {
        _columnMenuHost?.Dispose();
        _columnMenuHost = null;
    }
}
