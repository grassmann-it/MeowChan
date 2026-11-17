using System;
using Avalonia.Controls;

namespace MeowChan;

// Tom (16.11.2026): the header's first row lines up with the grid below it, structurally and without a fixed width.
//  - The order (shuffle, the criterion with its "Sort" prefix, direction) ends at the grid card's right edge: the column of the view tools above
//    the media panel is widened to the room that the tag list and the panel keep on the right (the same room the header's last line keeps for
//    the panel header), so the order stands left of the panel's divider. Without a panel, or when the tools are wider than the panel, the order
//    simply sits next to the view tools as before.
//  - UX review 17.11.2026 (Tom's screencast: "things must not jump around"): the search no longer ends where the selection actions line ends.
//    That line changes with the counter and with the "index is being built" reason, and the search, the filter toggle and the free room jumped
//    with it when the index was ready. The search now has a fixed cap (MediaToolbarLayout.SearchMaxWidth) and the free room before the order
//    keeps the group gap; the row depends on its own width and stage only.
// The extra room of the tools column is a minimum of an existing grid column; when the row is too narrow it shrinks to the slack that is left,
// and the width stages (MediaToolbarLayout) stay the ones that move tools under More. The arithmetic is MediaToolbarLayout (pure, unit tested).
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
