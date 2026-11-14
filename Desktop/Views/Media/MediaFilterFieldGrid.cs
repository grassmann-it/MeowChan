using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace MeowChan;

/// <summary>
/// The grid of the filter fields of the media page (M5): <see cref="Columns"/> equal columns, the cells of the visible children placed
/// column by column (<see cref="MediaFilterGridLayout.Place"/>), each row as high as its tallest cell. A child that is hidden (a work view
/// holds the field back) leaves no gap: the others close up. Because the tree order is the column order, the tab key walks down one
/// column and on to the next without any tab index.
/// </summary>
internal sealed class MediaFilterFieldGrid : Panel
{
    public static readonly StyledProperty<int> ColumnsProperty =
        AvaloniaProperty.Register<MediaFilterFieldGrid, int>(nameof(Columns), MediaFilterGridLayout.MaximumColumns, coerce: (_, value) => Math.Max(1, value));
    public static readonly StyledProperty<double> ColumnSpacingProperty = AvaloniaProperty.Register<MediaFilterFieldGrid, double>(nameof(ColumnSpacing), 8);
    public static readonly StyledProperty<double> RowSpacingProperty = AvaloniaProperty.Register<MediaFilterFieldGrid, double>(nameof(RowSpacing), 8);

    static MediaFilterFieldGrid() => AffectsMeasure<MediaFilterFieldGrid>(ColumnsProperty, ColumnSpacingProperty, RowSpacingProperty);

    private double[] _rowHeights = Array.Empty<double>();

    public int Columns { get => GetValue(ColumnsProperty); set => SetValue(ColumnsProperty, value); }
    public double ColumnSpacing { get => GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }
    public double RowSpacing { get => GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    /// <summary>The cells that take part in the layout (the visible children), in tree order.</summary>
    internal IReadOnlyList<Control> Cells => Children.Where(child => child.IsVisible).ToArray();

    /// <summary>How many columns the visible cells fill.</summary>
    internal int UsedColumns => MediaFilterGridLayout.Place(Cells.Count, Columns).Select(slot => slot.Column + 1).DefaultIfEmpty(0).Max();

    private double CellWidth(double width) => double.IsFinite(width) ? Math.Max(0, (width - ColumnSpacing * (Columns - 1)) / Columns) : double.PositiveInfinity;

    protected override Size MeasureOverride(Size availableSize)
    {
        IReadOnlyList<Control> cells = Cells;
        IReadOnlyList<MediaFilterGridLayout.Slot> slots = MediaFilterGridLayout.Place(cells.Count, Columns);
        int rows = slots.Count == 0 ? 0 : slots.Max(slot => slot.Row) + 1;
        _rowHeights = new double[rows];
        double cellWidth = CellWidth(availableSize.Width), widest = 0;
        for (int index = 0; index < cells.Count; index++)
        {
            cells[index].Measure(new Size(cellWidth, double.PositiveInfinity));
            _rowHeights[slots[index].Row] = Math.Max(_rowHeights[slots[index].Row], cells[index].DesiredSize.Height);
            widest = Math.Max(widest, cells[index].DesiredSize.Width);
        }
        int used = UsedColumns;
        double width = double.IsFinite(availableSize.Width) ? availableSize.Width : used * widest + Math.Max(0, used - 1) * ColumnSpacing;
        return new Size(width, _rowHeights.Sum() + Math.Max(0, rows - 1) * RowSpacing);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        IReadOnlyList<Control> cells = Cells;
        IReadOnlyList<MediaFilterGridLayout.Slot> slots = MediaFilterGridLayout.Place(cells.Count, Columns);
        if (_rowHeights.Length == 0 && cells.Count > 0) MeasureOverride(finalSize);
        double cellWidth = CellWidth(finalSize.Width);
        if (!double.IsFinite(cellWidth)) cellWidth = cells.Count == 0 ? 0 : cells.Max(cell => cell.DesiredSize.Width);
        double[] tops = new double[_rowHeights.Length];
        for (int row = 1; row < tops.Length; row++) tops[row] = tops[row - 1] + _rowHeights[row - 1] + RowSpacing;
        for (int index = 0; index < cells.Count; index++)
        {
            MediaFilterGridLayout.Slot slot = slots[index];
            if (slot.Row >= _rowHeights.Length) continue;
            cells[index].Arrange(new Rect(slot.Column * (cellWidth + ColumnSpacing), tops[slot.Row], cellWidth, _rowHeights[slot.Row]));
        }
        return finalSize;
    }
}
