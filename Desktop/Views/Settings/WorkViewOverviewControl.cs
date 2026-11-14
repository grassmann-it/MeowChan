using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

namespace MeowChan.Desktop.Views.Settings;

/// <summary>
/// Standalone, read-only WV11 settings category. All methods (including Dispose) require the UI thread.
/// Parent: create once, call EnsureLoaded when selecting settings.workviews (not while indexing settings search),
/// SetActiveView on applied view changes, SetPageFilter for a page deep-link, and LocaleRefresh after Texts changes.
/// Dispose with the settings editor, NOT on category switches. No global event subscription, config writes or page creation.
/// Prefer a bounded-height host; a vertical ScrollViewer gets a finite 480-dip measuring viewport to retain virtualization.
/// </summary>
internal sealed class WorkViewOverviewControl : UserControl, IDisposable
{
    private readonly HashSet<OverviewCell> _cells = new();
    private readonly Dictionary<WorkView, DataGridTemplateColumn> _headers = new();
    private IReadOnlyList<WorkViewOverview.Row> _rows = Array.Empty<WorkViewOverview.Row>();
    private DataGridCollectionView? _view;
    private DataGrid? _table;
    private ComboBox? _pages;
    private TextBox? _filter;
    private TextBlock? _description, _status, _pageLabel, _textLabel;
    private WorkView _activeView;
    private WorkViewPage? _pageFilter;
    private string _textFilter = "";
    private bool _updating, _disposed;

    internal WorkViewOverviewControl(WorkView activeView = WorkView.Barebones)
    {
        Dispatcher.UIThread.VerifyAccess();
        Name = "WorkViewOverview";
        if (!WorkViewSetting.All.Contains(activeView)) throw new ArgumentOutOfRangeException(nameof(activeView));
        _activeView = activeView;
        AttachedToVisualTree += OnAttached;
        KeyDown += OnKeyDown;
    }

    internal bool IsOverviewLoaded => _table != null;
    internal WorkView ActiveView => _activeView;
    internal WorkViewPage? PageFilter => _pageFilter;
    internal string TextFilter => _textFilter;

    /// <summary>Idempotent first-open hook. No policy projection or DataGrid exists before this call.</summary>
    internal void EnsureLoaded()
    {
        VerifyUsable();
        if (IsOverviewLoaded) return;
        _description = Wrap("");
        _description.Classes.Add("workspace-help");
        _pageLabel = Wrap("");
        _pages = new ComboBox { Name = "WorkViewOverviewPageFilter", MinWidth = 0, MaxWidth = 220,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        _filter = new TextBox { Name = "WorkViewOverviewTextFilter", MinWidth = 0, Text = _textFilter,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        _pages.SelectionChanged += OnPageChanged;
        _filter.TextChanged += OnTextChanged;
        var pageField = new StackPanel { Spacing = 4, Children = { _pageLabel, _pages } };
        var fields = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8 };
        fields.Children.Add(pageField);
        // A visible label, not only a placeholder which disappears while searching.
        _textLabel = Wrap("");
        _textLabel.Name = "WorkViewOverviewTextLabel";
        var textField = new StackPanel { Spacing = 4, Children = { _textLabel, _filter } };
        Grid.SetColumn(textField, 1); fields.Children.Add(textField);

        _table = new DataGrid
        {
            Name = "WorkViewOverviewTable", AutoGenerateColumns = false, IsReadOnly = true,
            CanUserReorderColumns = false, CanUserResizeColumns = false, CanUserSortColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column, MinColumnWidth = 0,
            RowHeight = double.NaN, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            SelectionMode = DataGridSelectionMode.Single, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal
        };
        // Row names follow recycled rows through bindings, not only a one-time LoadingRow callback.
        _table.Styles.Add(new Style(selector => selector.OfType<DataGridRow>())
        {
            Setters =
            {
                new Setter(AutomationProperties.NameProperty, new Binding(nameof(WorkViewOverview.Row.AccessibleName))),
                new Setter(DataGridRow.HeaderProperty, new Binding(nameof(WorkViewOverview.Row.Label)))
            }
        });
        _table.Columns.Add(new DataGridTemplateColumn
        {
            Width = new DataGridLength(2, DataGridLengthUnitType.Star), MinWidth = 0,
            Header = T("workview.overview.element"), HeaderTemplate = HeaderTemplate(),
            CellTemplate = new FuncDataTemplate<WorkViewOverview.Row>((row, _) => row == null ? null : new OverviewCell(this, row, null), supportsRecycling: false)
        });
        foreach (WorkView view in WorkViewSetting.All)
        {
            var column = new DataGridTemplateColumn
            {
                Header = WorkViewOverview.ViewLabel(view), HeaderTemplate = HeaderTemplate(),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star), MinWidth = 0,
                CellTemplate = new FuncDataTemplate<WorkViewOverview.Row>((row, _) => row == null ? null : new OverviewCell(this, row, view), supportsRecycling: false)
            };
            _headers.Add(view, column);
            _table.Columns.Add(column);
        }
        _status = Wrap("");
        _status.Name = "WorkViewOverviewStatus";
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 8 };
        layout.Children.Add(_description);
        Grid.SetRow(fields, 1); layout.Children.Add(fields);
        Grid.SetRow(_table, 2); layout.Children.Add(_table);
        Grid.SetRow(_status, 3); layout.Children.Add(_status);
        Content = layout;
        RefreshTextAndRows();
    }

    /// <summary>Marks the actually applied view; this does not switch the app view or change the settings draft.</summary>
    internal void SetActiveView(WorkView view)
    {
        VerifyUsable();
        if (!WorkViewSetting.All.Contains(view)) throw new ArgumentOutOfRangeException(nameof(view));
        _activeView = view;
        UpdateActiveColumn();
    }

    /// <summary>Null = all pages. May be called before the category's first open; does not force loading.</summary>
    internal void SetPageFilter(WorkViewPage? page)
    {
        VerifyUsable();
        if (page.HasValue && !Enum.IsDefined(page.Value)) throw new ArgumentOutOfRangeException(nameof(page));
        _pageFilter = page;
        if (_pages != null)
        {
            _updating = true;
            try { _pages.SelectedItem = _pages.ItemsSource!.Cast<PageChoice>().First(choice => choice.Page == page); }
            finally { _updating = false; }
        }
        RefreshFilter();
    }

    internal void SetTextFilter(string? text)
    {
        VerifyUsable();
        _textFilter = text ?? "";
        if (_filter != null && _filter.Text != _textFilter) _filter.Text = _textFilter;
        else RefreshFilter();
    }

    /// <summary>
    /// Parent forwards locale changes on UIThread. Re-captures names of already registered controls; never constructs pages.
    /// Retains active view, literal filters and selected policy id. Before first open this remains lazy.
    /// </summary>
    internal void LocaleRefresh()
    {
        VerifyUsable();
        if (IsOverviewLoaded) RefreshTextAndRows();
    }

    private void RefreshTextAndRows()
    {
        string? selectedId = (_table!.SelectedItem as WorkViewOverview.Row)?.Id;
        // WorkViewElement.All and every Avalonia property read stay strictly on this UI thread.
        Dispatcher.UIThread.VerifyAccess();
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (Control control in WorkViewElement.All())
        {
            string? name = AutomationProperties.GetName(control);
            if (string.IsNullOrWhiteSpace(name)) name = control switch
            {
                TextBlock text => text.Text,
                ContentControl { Content: string text } => text,
                ContentControl { Content: TextBlock text } => text.Text,
                _ => null
            };
            if (!string.IsNullOrWhiteSpace(name)) names.TryAdd(WorkViewElement.GetId(control)!, name.Trim());
        }
        _rows = WorkViewOverview.Build(id => names.GetValueOrDefault(id));
        // Lookup is only used during projection; no live controls or name closures are kept by the rows.
        _updating = true;
        try
        {
            _pages!.ItemsSource = new[] { new PageChoice(null, T("workview.overview.allPages")) }
                .Concat(Enum.GetValues<WorkViewPage>().Select(page => new PageChoice(page, WorkViewOverview.PageLabel(page)))).ToArray();
            _pages.SelectedItem = _pages.ItemsSource.Cast<PageChoice>().First(choice => choice.Page == _pageFilter);
        }
        finally { _updating = false; }
        _description!.Text = T("workview.overview.description");
        _pageLabel!.Text = T("workview.overview.page");
        _textLabel!.Text = T("workview.overview.filter");
        _table.Columns[0].Header = T("workview.overview.element");
        AutomationProperties.SetName(this, T("settings.section.workViews"));
        AutomationProperties.SetName(_pages, T("workview.overview.page"));
        AutomationProperties.SetName(_filter!, T("workview.overview.filter"));
        AutomationProperties.SetName(_table, T("settings.section.workViews"));
        _view = new DataGridCollectionView(_rows) { Filter = item => WorkViewOverview.Matches((WorkViewOverview.Row)item, _pageFilter, _textFilter) };
        _view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(WorkViewOverview.Row.PageLabel)));
        _table.ItemsSource = _view;
        _table.SelectedItem = _view.Cast<WorkViewOverview.Row>().FirstOrDefault(row => row.Id == selectedId);
        UpdateActiveColumn();
        UpdateStatus();
    }

    private void UpdateActiveColumn()
    {
        foreach (var (view, column) in _headers)
            column.Header = WorkViewOverview.ViewLabel(view, view == _activeView);
        foreach (OverviewCell cell in _cells) cell.Refresh();
    }

    private void RefreshFilter()
    {
        _view?.Refresh(); // Same projection and grid; filtering does not recreate rows or registered pages.
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_status == null || _view == null) return;
        _status.Text = _view.Count == 0 ? T("workview.overview.empty") : T("workview.overview.count", _view.Count, _rows.Count);
    }

    private void OnPageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || _disposed) return;
        _pageFilter = (_pages!.SelectedItem as PageChoice)?.Page;
        RefreshFilter();
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_disposed) return;
        _textFilter = _filter!.Text ?? "";
        RefreshFilter();
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (!_disposed && IsEffectivelyVisible) EnsureLoaded();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        KeyModifiers primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        if (_disposed || e.Key != Key.F || e.KeyModifiers != primary) return;
        EnsureLoaded();
        _filter!.Focus(); _filter.SelectAll(); e.Handled = true;
    }

    protected override Size MeasureOverride(Size availableSize) => base.MeasureOverride(new Size(availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 480 : availableSize.Height));

    private void VerifyUsable()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (_disposed) return;
        _disposed = true;
        AttachedToVisualTree -= OnAttached;
        KeyDown -= OnKeyDown;
        if (_pages != null) { _pages.SelectionChanged -= OnPageChanged; _pages.ItemsSource = null; }
        if (_filter != null) _filter.TextChanged -= OnTextChanged;
        if (_table != null) _table.ItemsSource = null;
        if (_view != null) _view.Filter = null;
        _view = null; _rows = Array.Empty<WorkViewOverview.Row>();
        Content = null;
        _cells.Clear(); _headers.Clear();
        _table = null; _pages = null; _filter = null; _description = null; _pageLabel = null; _status = null; _textLabel = null;
    }

    private sealed record PageChoice(WorkViewPage? Page, string Label)
    {
        public override string ToString() => Label;
    }

    // Only realized cells are tracked, and only while attached. Active-column refresh never walks the application tree.
    private sealed class OverviewCell : TextBlock
    {
        private readonly WorkViewOverviewControl _owner;
        private readonly WorkViewOverview.Row _row;
        private readonly WorkView? _view;

        internal OverviewCell(WorkViewOverviewControl owner, WorkViewOverview.Row row, WorkView? view)
        {
            _owner = owner; _row = row; _view = view;
            TextWrapping = TextWrapping.Wrap; Margin = new Thickness(4); MinWidth = 0;
            AttachedToVisualTree += (_, _) => { _owner._cells.Add(this); Refresh(); };
            DetachedFromVisualTree += (_, _) => _owner._cells.Remove(this);
            Refresh();
        }

        internal void Refresh()
        {
            bool active = _view == _owner._activeView;
            Text = _view.HasValue ? _row.Cell(_view.Value) : _row.ElementText;
            FontWeight = active ? FontWeight.Bold : FontWeight.Normal;
            string name = _view.HasValue ? _row.CellName(_view.Value, active) : _row.AccessibleName;
            AutomationProperties.SetName(this, name);
            // DataGrid's actual cell peer also needs the name, not only its TextBlock child.
            if (this.FindAncestorOfType<DataGridCell>() is { } cell) AutomationProperties.SetName(cell, name);
        }
    }

    private static IDataTemplate HeaderTemplate() => new FuncDataTemplate<string>((label, _) => Wrap(label ?? ""));

    private static TextBlock Wrap(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, MinWidth = 0 };
    private static string T(string key, params object?[] args) => WorkViewOverviewCatalog.T(key, args);
}
