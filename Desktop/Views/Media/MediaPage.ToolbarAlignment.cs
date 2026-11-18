using System;
using Avalonia.Controls;

namespace MeowChan;

// The header and grid now share a real column beside the full-height sidebar (UX-LEDGER-27).
// No synthetic sidebar-width reservation is needed. Search and order still depend only
// on their row width/stage, never on the changing selection counter or building reason.
public sealed partial class MediaPage
{
    private StackPanel? _toolbarQuery, _toolbarOrder, _toolbarEnd;
    private double _gridRightReserve;

    /// <summary>The room between the header's right edge and the grid card's right edge that the order keeps free beside the view tools (smokes).</summary>
    internal double GridRightReserve => _gridRightReserve;

    private void InstallToolbarAlignment(Grid tools, StackPanel query, StackPanel order, StackPanel right)
    {
        _toolbarQuery = query; _toolbarOrder = order; _toolbarEnd = right;
        tools.SizeChanged += (_, e) => { if (e.WidthChanged) ApplyToolbarAlignment(); };
        foreach (Control group in new Control[] { query, order, right })
            group.SizeChanged += (_, e) => { if (e.WidthChanged) ApplyToolbarAlignment(); };
    }

    // The minimum of the view tools' column (the order ends at the grid's right edge) and whether the row has room for the "Sort" prefix.
    private void ApplyToolbarAlignment()
    {
        if (_toolbar is not { } row || _toolbarQuery is not { } query || _toolbarOrder is not { } order || _toolbarEnd is not { } end || _lifetime.IsCancellationRequested) return;
        ApplySortPrefix();
        ColumnDefinitions columns = row.ColumnDefinitions;
        double rowWidth = row.Bounds.Width, filters = query.DesiredSize.Width, ordered = order.DesiredSize.Width, tools = end.DesiredSize.Width;
        if (rowWidth <= 0 || tools <= 0) return;
        double minimum = MediaToolbarLayout.ViewToolsColumnMin(_gridRightReserve, tools, MediaToolbarLayout.Slack(rowWidth, filters, ordered, tools));
        if (Math.Abs(columns[4].MinWidth - minimum) >= 0.5) columns[4].MinWidth = minimum;
    }
}
