using System;
using Avalonia.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.WorkViews;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// C16: the queue table keeps its preview useful but optional. The header's context menu is the same keyboard and pointer path as
/// the other shared menus; hiding the preview collapses its column rather than changing the row's semantic order.
/// </summary>
public sealed partial class TaggingPage
{
    private const double NarrowToolbarWidth = 1100;
    private readonly Button _columnsButton = new() { Name = "TaggingColumnsButton" };
    private bool _showPreviewColumn = true;
    private bool _narrowToolbar;
    private ContextMenuHost? _columnMenuHost;

    internal bool ShowPreviewColumn => _showPreviewColumn;
    internal Grid ColumnHeader => _columnHeader;
    internal WrapPanel SecondaryToolbar => _secondaryToolbar;
    internal bool IsNarrowToolbar => _narrowToolbar;
    internal Button ColumnsButton => _columnsButton;
    internal Action<bool>? SavePreviewColumn { get; set; }

    private void InitializeColumns()
    {
        WorkViewElement.SetId(_columnsButton, "tagging.columns");
        CommandBindings.Apply(_columnsButton, "table.choose-columns", iconWithLabel: true);
        _columnsButton.Click += (_, _) => _columnMenuHost?.Show(atPointer: false);
        int beforeView = _actions.Items.IndexOf(_viewToggle);
        _actions.Items.Insert(beforeView < 0 ? _actions.Items.Count : beforeView, _columnsButton);
        _columnMenuHost = new ContextMenuHost(_columnHeader, ComposeColumnMenu, fallbackFocus: () => FocusContent());
    }

    internal void RestorePreviewColumn(bool visible) => SetPreviewColumnVisible(visible, save: false);

    internal ContextMenuModel? ColumnMenuModel() => ComposeColumnMenu()?.ToModel();

    private ContextMenuBuilder? ComposeColumnMenu()
    {
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("command.table.choose-columns"));
        menu.Check("tagging.columns.preview", Texts.T("tagging.page.col.preview"), _showPreviewColumn,
            () => SetPreviewColumnVisible(!_showPreviewColumn));
        return menu;
    }

    private void SetPreviewColumnVisible(bool visible, bool save = true)
    {
        if (_showPreviewColumn == visible) return;
        _showPreviewColumn = visible;
        LocalizeColumns();
        _list.ItemTemplate = RowTemplate();
        Render();
        if (save) SavePreviewColumn?.Invoke(visible);
    }

    /// <summary>Moves the group and state filter into a real second row only when the page cannot keep them on the main toolbar.</summary>
    private void UpdateToolbarRows()
    {
        bool narrow = Bounds.Width > 0 && Bounds.Width < NarrowToolbarWidth;
        if (narrow == _narrowToolbar) return;
        _narrowToolbar = narrow;
        if (narrow)
        {
            _actions.Items.Remove(_groupPanel);
            _actions.Items.Remove(_filterPanel);
            if (!_secondaryToolbar.Children.Contains(_groupPanel)) _secondaryToolbar.Children.Add(_groupPanel);
            if (!_secondaryToolbar.Children.Contains(_filterPanel)) _secondaryToolbar.Children.Add(_filterPanel);
        }
        else
        {
            _secondaryToolbar.Children.Remove(_groupPanel);
            _secondaryToolbar.Children.Remove(_filterPanel);
            int beforeView = _actions.Items.IndexOf(_viewToggle);
            if (!_actions.Items.Contains(_groupPanel)) _actions.Items.Insert(beforeView < 0 ? _actions.Items.Count : beforeView, _groupPanel);
            beforeView = _actions.Items.IndexOf(_viewToggle);
            if (!_actions.Items.Contains(_filterPanel)) _actions.Items.Insert(beforeView < 0 ? _actions.Items.Count : beforeView, _filterPanel);
        }
        _secondaryToolbar.IsVisible = narrow;
    }

    private void EndColumns()
    {
        _columnMenuHost?.Dispose();
        _columnMenuHost = null;
    }
}
