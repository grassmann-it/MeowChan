using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// Tom (16.11.2026): the header's first row lines up with the rows below it, structurally and without a fixed width.
//  - The search ends where the selection actions line ends ("Clear selection"): the line's width is measured from its own buttons (and the
//    counter's text) and becomes the cap of the search column. While nothing is selected the line is hidden; its buttons are then measured
//    as if it were shown (the counter with "1 selected"), so the search keeps its width when a selection starts and the header does not
//    jump. The widths follow the localized texts, the work view's gates (a button under More leaves the line) and the page's DPI/theme.
//  - The order (shuffle, "Sort" label, criterion, direction) ends at the grid card's right edge: the column of the view tools above the media
//    panel is widened to the room that the tag list and the panel keep on the right (the same room the header's last line keeps for the
//    panel header), so the order stands left of the panel's divider. Without a panel, or when the tools are wider than the panel, the order
//    simply sits next to the view tools as before.
// Both are caps/minimums of the existing grid columns; when the row is too narrow the columns give way (the search shrinks to its minimum, the
// extra room of the tools column shrinks to the slack that is left), and the width stages (MediaToolbarLayout) stay the ones that move tools
// under More. The arithmetic is MediaToolbarLayout (pure, unit tested).
public sealed partial class MediaPage
{
    private StackPanel? _toolbarQuery, _toolbarOrder, _toolbarEnd;
    private double _selectionRowWidth, _headerRoomLeft, _gridRightReserve;
    private bool _rulerPosted, _rulerWatching;

    /// <summary>The width of the selection actions line on one line (its buttons and the counter), measured also while it is hidden; 0 until it is measured (smokes).</summary>
    internal double SelectionRowNaturalWidth => _selectionRowWidth;

    /// <summary>The room between the header's right edge and the grid card's right edge that the order keeps free beside the view tools (smokes).</summary>
    internal double GridRightReserve => _gridRightReserve;

    private void InstallToolbarAlignment(Grid tools, StackPanel query, StackPanel order, StackPanel right)
    {
        _toolbarQuery = query; _toolbarOrder = order; _toolbarEnd = right;
        tools.SizeChanged += (_, e) => { if (e.WidthChanged) ApplyToolbarAlignment(); };
        foreach (Control group in new Control[] { query, order, right })
            group.SizeChanged += (_, e) => { if (e.WidthChanged) ApplyToolbarAlignment(); };
        // The buttons exist and are styled once the page is in a window; the line is measured then (and again when its texts or buttons change).
        tools.AttachedToVisualTree += (_, _) =>
        {
            WatchSelectionRow();
            ScheduleSelectionRowMeasure();
        };
    }

    // A button that a work view brings in or takes out of the line changes its width without any selection change.
    private void WatchSelectionRow()
    {
        if (_rulerWatching) return;
        _rulerWatching = true;
        foreach (Control child in _selectionRow.Children)
            child.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) ScheduleSelectionRowMeasure(); };
    }

    /// <summary>Measures the selection actions line once, after the pending work of the dispatcher (many changes in a row measure it once).</summary>
    private void ScheduleSelectionRowMeasure()
    {
        if (_rulerPosted || _lifetime.IsCancellationRequested) return;
        _rulerPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _rulerPosted = false;
            if (!_lifetime.IsCancellationRequested) MeasureSelectionRow();
        }, DispatcherPriority.Loaded);
    }

    // Outside a layout pass. A shown line is read from the desired sizes its own layout produced (nothing is measured under it); a hidden line is not
    // laid out, so its buttons are measured at their natural width here (their parent being hidden does not matter to them) and the counter is
    // measured with the text of one selected tile, which is empty while nothing is selected.
    private void MeasureSelectionRow()
    {
        if (TopLevel.GetTopLevel(_selectionRow) == null) return;
        bool shown = _selectionRow.IsVisible && _selectionRow.IsMeasureValid;
        string text = _selectionText.Text ?? string.Empty;
        bool substitute = text.Length == 0;
        List<double> widths = new();
        try
        {
            if (substitute) _selectionText.Text = Texts.T("media.selection.count", 1);
            foreach (Control child in _selectionRow.Children)
            {
                if (!child.IsVisible) continue;
                if (!shown || child.DesiredSize.Width <= 0) child.Measure(Size.Infinity);
                widths.Add(child.DesiredSize.Width);
            }
        }
        finally { if (substitute) _selectionText.Text = text; }
        double width = MediaToolbarLayout.SelectionRowWidth(widths, _selectionRow.ItemSpacing);
        if (Math.Abs(width - _selectionRowWidth) < 0.5) return;
        _selectionRowWidth = width;
        ApplyToolbarAlignment();
    }

    // The two columns the rows below decide: the search's cap and the minimum of the view tools' column.
    private void ApplyToolbarAlignment()
    {
        if (_toolbar is not { } row || _toolbarQuery is not { } query || _toolbarOrder is not { } order || _toolbarEnd is not { } end || _lifetime.IsCancellationRequested) return;
        ColumnDefinitions columns = row.ColumnDefinitions;
        double target = MediaToolbarLayout.SearchTargetWidth(_headerRoomLeft, _selectionRowWidth);
        if (Math.Abs(columns[0].MaxWidth - target) >= 0.5) columns[0].MaxWidth = target;
        double rowWidth = row.Bounds.Width, filters = query.DesiredSize.Width, ordered = order.DesiredSize.Width, tools = end.DesiredSize.Width;
        if (rowWidth <= 0 || tools <= 0) return;
        double minimum = MediaToolbarLayout.ViewToolsColumnMin(_gridRightReserve, tools, MediaToolbarLayout.Slack(rowWidth, filters, ordered, tools));
        if (Math.Abs(columns[4].MinWidth - minimum) >= 0.5) columns[4].MinWidth = minimum;
    }
}
