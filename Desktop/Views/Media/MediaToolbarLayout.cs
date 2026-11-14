using System;
using System.Collections.Generic;

namespace MeowChan;

/// <summary>
/// How the media toolbar adapts to the width of its page (pure rules; <see cref="MediaPage"/> applies them). The order of the row is fixed:
/// search and filters on the left, the order (criterion and direction) on the right, with the less important tools in front of or behind it.
/// The stages: Wide shows everything; Reduced moves the focus mode, "Refresh" and "Shuffle again" under "More" (⋯); Compact moves the
/// Booru mode switch under More as well, drops the "Sort" label in front of the criterion, and lets the search field and the criterion shrink
/// to their narrow minimum. The order itself, the search and the filter toggle never move. The widths are layout measures of this row in
/// dip (the Booru mode switch and the "Sort" label added about 80 dip to the old fit points of 840 and 660), not limits of the user's data;
/// the media sort toolbar smoke check measures that the last tool of the row still fits at each stage.
/// </summary>
internal static class MediaToolbarLayout
{
    /// <summary>Below this width "Refresh" and "Shuffle again" move under More.</summary>
    internal const double ReducedWidth = 920;
    /// <summary>Below this width the Booru mode switch moves under More too, the "Sort" label goes, and the search field and the criterion shrink to <see cref="NarrowSearchMinWidth"/> and <see cref="NarrowSortMinWidth"/>.</summary>
    internal const double CompactWidth = 740;
    internal const double SearchMinWidth = 180, NarrowSearchMinWidth = 120;
    internal const double SortMinWidth = 160, NarrowSortMinWidth = 120;
    /// <summary>The longest the search field grows in the row; the free space beyond it stays empty between the two groups.</summary>
    internal const double SearchMaxWidth = 480;

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

    /// <summary>Whether the "Sort" label in front of the criterion is shown (it goes with the Compact stage; the criterion keeps its own name).</summary>
    internal static bool ShowsSortLabel(Level level) => level != Level.Compact;

    internal static double SearchMin(Level level) => level == Level.Compact ? NarrowSearchMinWidth : SearchMinWidth;

    internal static double SortMin(Level level) => level == Level.Compact ? NarrowSortMinWidth : SortMinWidth;
}
