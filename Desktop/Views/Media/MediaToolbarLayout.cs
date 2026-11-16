using System;
using System.Collections.Generic;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

/// <summary>
/// How the media toolbar adapts to the width of its row (pure rules; <see cref="MediaPage"/> applies them). The order of the row is fixed:
/// the search field on the left (it fills the free width from <see cref="SearchMinWidth"/> up to the width of the selection actions line under
/// it, so both end at the same x, <see cref="SearchTargetWidth"/>), the filter toggle, then on the right the order (criterion and direction), which ends at
/// the grid's right edge (<see cref="Align"/>), and the view tools (refresh, tag list, layout, focus, More) above the media panel; the free width
/// is the spacer between the filter toggle and the order.
/// The stages: Wide shows everything; Reduced moves the focus mode, "Refresh" and "Shuffle again" under "More" (⋯); Compact moves the
/// tag list switch and the Layout menu under More as well, drops the "Sort" label in front of the criterion, and lets the criterion shrink to
/// its narrow minimum. The search keeps its minimum in every stage; the order, the search and the filter toggle never move. The widths are
/// layout measures of the row in dip (the width of the row, not of the window), not limits of the user's data: each stage starts at the width
/// its group of tools needs next to the search minimum, measured in the media sort toolbar smoke check over all 12 languages and every work
/// view (it fails when a stage needs more than the width that starts it, and checks that the last tool of the row fits at 640 to 1920).
/// </summary>
internal static class MediaToolbarLayout
{
    /// <summary>Below this width "Refresh" and "Shuffle again" move under More.</summary>
    internal const double ReducedWidth = 950;
    /// <summary>Below this width the Booru mode switch moves under More too, the "Sort" label goes, and the criterion shrinks to <see cref="NarrowSortMinWidth"/>.</summary>
    internal const double CompactWidth = 870;
    internal const double SearchMinWidth = 240;
    internal const double SortMinWidth = 160, NarrowSortMinWidth = 120;
    /// <summary>
    /// The longest the search field grows until the selection actions line has been measured (the page measures it as soon as it is shown); afterwards
    /// <see cref="SearchTargetWidth"/> is the cap. Free space beyond it stays empty between the filter toggle and the order.
    /// </summary>
    internal const double SearchMaxWidth = 640;

    /// <summary>The narrowest row the page gives the toolbar: the 640 dip window minimum less the 2 x 12 dip page margin. The Compact stage must fit it.</summary>
    internal const double NarrowestRowWidth = 616;

    internal enum Level { Wide, Reduced, Compact }

    /// <summary>The level for the width of the toolbar row. An unknown width (not measured yet) is Wide.</summary>
    internal static Level For(double width) =>
        !double.IsFinite(width) || width <= 0 ? Level.Wide
        : width < CompactWidth ? Level.Compact
        : width < ReducedWidth ? Level.Reduced : Level.Wide;

    // The work-view ids of the tools that leave the row first, in the order they leave (the order they have under More follows the policy).
    // The focus mode (M4) leaves the row first: it stays a shortcut (Shift+F) and an entry under More, so nothing is lost.
    private static readonly string[] DemotableReduced = ["media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle"];
    // N6: the Booru mode switch (a Direct tool from Simple on; since MP5 the "Tag list" switch, id media.toolbar.tag-list) is the last one to
    // leave, with the Compact stage; under More it keeps its checked entry. MP5's "Layout" menu sits right behind the switch and leaves with it.
    private static readonly string[] DemotableCompact = [.. DemotableReduced, "media.toolbar.tag-list", "media.toolbar.layout"];

    /// <summary>Whether the width moves this tool (by its work-view id) under More. The order, search and filter toggle never move.</summary>
    internal static bool IsMovedUnderMore(Level level, string? id) =>
        level != Level.Wide && id != null && Array.IndexOf(level == Level.Compact ? DemotableCompact : DemotableReduced, id) >= 0;

    /// <summary>The ids that move under More at this level.</summary>
    internal static IReadOnlyList<string> MovedUnderMore(Level level) =>
        level == Level.Wide ? Array.Empty<string>() : level == Level.Compact ? DemotableCompact : DemotableReduced;

    /// <summary>
    /// Whether one tool of the row sits under More at this width and view: the view puts it there (<see cref="Presence.Overflow"/>; a tool the
    /// view leaves out entirely, <see cref="Presence.Hidden"/>, is in no menu) or the width moves it there (<see cref="IsMovedUnderMore"/>).
    /// The More button (Tom, 15.11.2026) is shown exactly while at least one tool that the page's own state still wants is under it, and its menu lists
    /// those tools, so it never opens an empty menu. Export all and the saved-ratings manager are under More in every view: on the standard page the
    /// button is there at every width.
    /// </summary>
    internal static bool IsUnderMore(Level level, string? id, Presence inView) =>
        inView == Presence.Overflow || (inView != Presence.Hidden && IsMovedUnderMore(level, id));

    /// <summary>Whether the "Sort" label in front of the criterion is shown (it goes with the Compact stage; the criterion keeps its own name).</summary>
    internal static bool ShowsSortLabel(Level level) => level != Level.Compact;

    /// <summary>The search never gets narrower than <see cref="SearchMinWidth"/> in any stage: the tools leave the row instead.</summary>
    internal static double SearchMin(Level level) => SearchMinWidth;

    internal static double SortMin(Level level) => level == Level.Compact ? NarrowSortMinWidth : SortMinWidth;

    /// <summary>The width of the selection actions line when all of it stands on one line: the visible children and the gaps between them.</summary>
    internal static double SelectionRowWidth(IEnumerable<double> childWidths, double itemSpacing)
    {
        double sum = 0;
        int count = 0;
        foreach (double width in childWidths)
        {
            if (!double.IsFinite(width) || width <= 0) continue;
            sum += width;
            count++;
        }
        return count == 0 ? 0 : sum + itemSpacing * (count - 1);
    }

    /// <summary>
    /// The width that makes the search end where the selection actions line ends: the room the line keeps free on its left (the media panel's header
    /// room) and the line itself, never less than <see cref="SearchMinWidth"/>. A line that is not measured yet (0) gives <see cref="SearchMaxWidth"/>.
    /// </summary>
    internal static double SearchTargetWidth(double leftRoom, double selectionRowWidth) =>
        selectionRowWidth <= 0 || !double.IsFinite(selectionRowWidth) ? SearchMaxWidth
        : Math.Max(SearchMinWidth, Math.Max(0, leftRoom) + selectionRowWidth);

    /// <summary>
    /// The room between the right edge of the header and the right edge of the grid card: the blocks on the grid's right side (tag list and media
    /// panel with their handles), the grid's own right margin, less the margin the header itself already keeps from the page edge.
    /// </summary>
    internal static double GridRightReserve(double rightBlocks, double surfaceRightMargin, double pageMargin) =>
        Math.Max(0, Math.Max(0, rightBlocks) + surfaceRightMargin - pageMargin);

    /// <summary>The width the free room of the row leaves over after the search at its minimum and the three groups at their natural width.</summary>
    internal static double Slack(double rowWidth, double filtersWidth, double orderWidth, double viewToolsWidth) =>
        Math.Max(0, rowWidth - (SearchMinWidth + filtersWidth + orderWidth + viewToolsWidth));

    /// <summary>
    /// The minimum width of the column of the view tools: their own width, widened to the room above the media panel (so the order ends at the grid's right
    /// edge) as far as the row has slack for it; on a row that is too narrow the tools keep their width and the order sits next to them.
    /// </summary>
    internal static double ViewToolsColumnMin(double reserve, double viewToolsWidth, double slack) =>
        viewToolsWidth + Math.Clamp(reserve - viewToolsWidth, 0, Math.Max(0, slack));

    /// <summary>Where the pieces of the row end (x from the row's left edge): the search, the order and the view tools' left edge.</summary>
    internal readonly record struct Alignment(double SearchRight, double OrderRight, double ViewToolsLeft);

    /// <summary>
    /// The pure model of the row for the tests: the search takes the free width up to <see cref="SearchTargetWidth"/> (not below its minimum), the order
    /// ends where the view tools' column begins. The widths of the three groups include their 8 dip lead margin.
    /// </summary>
    internal static Alignment Align(double rowWidth, double leftRoom, double selectionRowWidth, double reserve, double filtersWidth, double orderWidth, double viewToolsWidth)
    {
        double columnMin = ViewToolsColumnMin(reserve, viewToolsWidth, Slack(rowWidth, filtersWidth, orderWidth, viewToolsWidth));
        double free = rowWidth - filtersWidth - orderWidth - columnMin;
        double search = Math.Max(SearchMinWidth, Math.Min(SearchTargetWidth(leftRoom, selectionRowWidth), free));
        return new Alignment(search, rowWidth - columnMin, rowWidth - viewToolsWidth);
    }
}
