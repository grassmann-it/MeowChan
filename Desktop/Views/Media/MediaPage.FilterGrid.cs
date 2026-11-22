using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using MeowChan.Desktop;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// M5: the fields of the filter section stand in a grid of 4, 3, 2 or 1 columns (MediaFilterGridLayout, MediaFilterFieldGrid), every combo with
// its label above it and stretched to its column, so no field has a width of its own. The check boxes are cells too (their text is their
// label). The tag button has a row of its own below the grid, with the "all | any" switch beside it. What is a filter and how the fields write
// the term set is unchanged (MediaPage.Filters.cs); only where the controls stand and how wide they are changed.
public sealed partial class MediaPage
{
    private readonly MediaFilterFieldGrid _filterGrid = new() { Name = "MediaFilterGrid" };
    private readonly List<(TextBlock Label, string Key)> _filterLabels = new();
    private readonly TextBlock _tagLabel = new() { Name = "MediaTagLabel", Classes = { "workspace-field-label" } };
    private Control? _tagRow;

    /// <summary>The grid of the combos and check boxes (tests and smokes).</summary>
    internal MediaFilterFieldGrid FilterGrid => _filterGrid;
    /// <summary>The columns the grid is laid out in now (tests and smokes).</summary>
    internal int FilterGridColumns => _filterGrid.Columns;
    /// <summary>The row with the tag button and the "all | any" switch (smokes).</summary>
    internal Control TagRow => _tagRow!;
    /// <summary>The "all | any" switch of the tag mode (smokes).</summary>
    internal TwoWayToggle TagMode => _tagMode;

    private Control BuildFilterFields()
    {
        // Column by column: down the first column (type, sound), then the next (source, board), then rating and the flags.
        AddFieldCell("media.filter.kind", _kindFilter);
        AddFieldCell("media.filter.sound", _soundFilter);
        AddFieldCell("media.filter.source", _sourceFilter);
        AddFieldCell("media.filter.board", _boardFilter);
        AddFieldCell("media.filter.rating", _minRating);
        foreach (CheckBox flag in new[] { _favoritesOnly, _withoutMediaTags, _missing }) AddFlagCell(flag);
        _tagAdd.HorizontalAlignment = HorizontalAlignment.Stretch;
        _tagAdd.VerticalAlignment = _tagMode.VerticalAlignment = VerticalAlignment.Center;
        Grid pickers = new() { ColumnSpacing = 8, ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        pickers.Children.Add(_tagAdd);
        pickers.Children.Add(_tagMode); Avalonia.Controls.Grid.SetColumn(_tagMode, 1);
        StackPanel tagCell = new() { Name = "MediaTagCell", Classes = { "workspace-field" } };
        tagCell.Children.Add(_tagLabel); tagCell.Children.Add(pickers);
        // The cell is as wide as the popover under it and no wider; the free width stays empty beside it.
        Grid tagRow = new() { Name = "MediaTagRow", ColumnDefinitions = new ColumnDefinitions("100*,*") };
        tagRow.ColumnDefinitions[0].MaxWidth = MediaFilterGridLayout.TagRowMaxWidth;
        tagRow.Children.Add(tagCell);
        Mirror(tagRow, _tagAdd, _tagMode);
        _tagRow = tagRow;
        _moreFilters.HorizontalAlignment = HorizontalAlignment.Left;
        _audioNote.HorizontalAlignment = HorizontalAlignment.Stretch;
        StackPanel fields = new() { Name = "MediaFilterFields", Spacing = 8 };
        fields.Children.Add(_filterGrid); fields.Children.Add(tagRow); fields.Children.Add(_moreFilters); fields.Children.Add(_audioNote);
        // The page width decides the columns, the page height the most the section may take (M5).
        SizeChanged += (_, e) => ApplyFilterSurfaceSize(e.NewSize.Width, e.NewSize.Height);
        return fields;
    }

    // A labelled field: the label above, the control stretched to the column. The cell shows while the field does (a work view may hold the field back).
    private void AddFieldCell(string labelKey, Control field)
    {
        TextBlock label = new() { Classes = { "workspace-field-label" } };
        _filterLabels.Add((label, labelKey));
        field.HorizontalAlignment = HorizontalAlignment.Stretch;
        field.VerticalAlignment = VerticalAlignment.Center;
        StackPanel cell = new() { Classes = { "workspace-field" } };
        cell.Children.Add(label); cell.Children.Add(field);
        AutomationProperties.SetLabeledBy(field, label);
        Mirror(cell, field);
        _filterGrid.Children.Add(cell);
    }

    // A check box has its text as label: its cell has no label above. F05: the cell keeps the field cells' shape (a label-high spacer, then a box as
    // high as the combo boxes, Workspace.Size.Input = 32 dip) and the check box stands vertically centred in that box, so it is level with the
    // dropdowns beside it instead of hanging at the bottom of the cell.
    internal const double FilterFieldHeight = WorkspaceMetrics.InputHeight;
    private void AddFlagCell(CheckBox flag)
    {
        flag.VerticalAlignment = VerticalAlignment.Center;
        flag.MinHeight = 0;
        // The spacer is the height of a one-line field label; it is not read and takes no input.
        // It is no workspace-field-label itself: the labels are real text, the layout checks count them.
        TextBlock spacer = new()
        {
            [!TextBlock.FontSizeProperty] = new DynamicResourceExtension("Workspace.Text.Body"),
            FontWeight = Avalonia.Media.FontWeight.Medium,
            Text = "\u00A0", Opacity = 0, IsHitTestVisible = false, Name = "MediaFilterFlagSpacer"
        };
        AutomationProperties.SetAccessibilityView(spacer, AccessibilityView.Raw);
        Avalonia.Controls.Grid box = new() { Name = "MediaFilterFlagBox", Height = FilterFieldHeight, Children = { flag } };
        StackPanel cell = new() { Classes = { "workspace-field" }, VerticalAlignment = VerticalAlignment.Bottom, ClipToBounds = true };
        cell.Children.Add(spacer); cell.Children.Add(box);
        Mirror(cell, flag);
        _filterGrid.Children.Add(cell);
    }

    // The work view shows and hides the fields themselves (WorkViewGate); the cell around a field follows, so no empty cell is left in the grid.
    private static void Mirror(Control cell, params Control[] fields)
    {
        void Follow() => cell.IsVisible = fields.Any(field => field.IsVisible);
        foreach (Control field in fields)
            field.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) Follow(); };
        Follow();
    }

    private void LocalizeFilterGrid()
    {
        foreach ((TextBlock label, string key) in _filterLabels) label.Text = Texts.T(key);
        _tagLabel.Text = Texts.T("media.filter.group.tag");
        _tagMode.SetItems(Texts.T("media.filter.match.all"), Texts.T("media.filter.match.any"),
            Texts.T("tags.filter.matchAll"), Texts.T("tags.filter.matchAny"));
        AutomationProperties.SetLabeledBy(_tagAdd, _tagLabel);
    }

    /// <summary>Lays the section out for the size of the page: the column count by the width, the height the section may take by the height.</summary>
    internal void ApplyFilterSurfaceSize(double width, double height)
    {
        if (_lifetime.IsCancellationRequested) return;
        _filterGrid.Columns = MediaFilterGridLayout.Columns(width);
        ApplyFilterSurfaceHeight(height);
    }
}
