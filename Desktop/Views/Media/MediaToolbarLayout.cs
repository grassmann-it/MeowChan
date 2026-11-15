using System;
using System.Collections.Generic;
using MeowChan.Desktop.WorkViews;

namespace MeowChan;

/// <summary>
/// How the media toolbar adapts to the width of its row (pure rules; <see cref="MediaPage"/> applies them). The order of the row is fixed:
/// the query group on the left (the search field, which fills the free width from <see cref="SearchMinWidth"/> to <see cref="SearchMaxWidth"/>,
/// then the filter toggle and the order: criterion and direction) and the view tools on the right (refresh, tag list, layout, focus, More); on a
/// wide row the free width beyond the search maximum is the spacer between the two groups.
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
    /// <summary>The longest the search field grows in the row; on a wide window the free space beyond it stays empty between the query group (search, filters, order) and the view tools.</summary>
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
}
