using System;
using System.Collections.Generic;

namespace MeowChan;

/// <summary>
/// How the media toolbar adapts to the width of its page (pure rules; <see cref="MediaPage"/> applies them). The order of the row is fixed:
/// search and filters on the left, the order (criterion and direction) on the right, with the less important tools in front of or behind it.
/// When the width runs short, the less important tools move under "More" (⋯) first and the order stays visible; only below
/// <see cref="CompactWidth"/> do the search field and the criterion shrink to their narrow minimum. The widths are layout measures of
/// this row in dip (the 760 dip window minimum leaves the page about 600), not limits of the user's data.
/// </summary>
internal static class MediaToolbarLayout
{
    /// <summary>Below this width "Refresh" and "Shuffle again" move under More.</summary>
    internal const double ReducedWidth = 840;
    /// <summary>Below this width the search field and the criterion shrink to <see cref="NarrowSearchMinWidth"/> and <see cref="NarrowSortMinWidth"/>.</summary>
    internal const double CompactWidth = 660;
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
    private static readonly string[] Demotable = ["media.toolbar.focus", "media.toolbar.refresh", "media.toolbar.shuffle"];

    /// <summary>Whether the width moves this tool (by its work-view id) under More. The order, search and filter toggle never move.</summary>
    internal static bool IsMovedUnderMore(Level level, string? id) =>
        level != Level.Wide && id != null && Array.IndexOf(Demotable, id) >= 0;

    /// <summary>The ids that move under More at this level.</summary>
    internal static IReadOnlyList<string> MovedUnderMore(Level level) => level == Level.Wide ? Array.Empty<string>() : Demotable;

    internal static double SearchMin(Level level) => level == Level.Compact ? NarrowSearchMinWidth : SearchMinWidth;

    internal static double SortMin(Level level) => level == Level.Compact ? NarrowSortMinWidth : SortMinWidth;
}
