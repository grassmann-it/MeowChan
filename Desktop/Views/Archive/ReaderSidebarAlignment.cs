using System;
using Avalonia;

namespace MeowChan.Desktop;

/// <summary>
/// Tom (16.11.2026): the reader sidebar's media frame has exactly the top and bottom of the thread reader's bordered box (the box with the Posts and
/// Saved media tabs, the posts and the pager); the sidebar's header line ("No. 22497148 · file · 302 KiB", star, open) stands above the frame, in the
/// strip over the box where the reader's toolbar and info rows are. The Media page's panel does the same with its grid card (MediaPage.PanelAlignment.cs);
/// both use <see cref="ReaderMediaSidebar.HeaderLineHeight"/> and <see cref="ReaderMediaSidebar.HeaderGap"/>.
/// The arithmetic is pure: all y values are in one coordinate space (the sidebar's host). The reader measures the box and the sidebar and applies the
/// result as the sidebar's top and bottom inset, so a toolbar that wraps or compacts, a language with longer words or a taller window change nothing here.
/// </summary>
internal static class ReaderSidebarAlignment
{
    /// <summary>Differences below this many dip are layout rounding, not a change (keeps the layout from chasing itself).</summary>
    internal const double Tolerance = 0.25;

    /// <summary>The room the header line and its gap need above the frame.</summary>
    internal const double HeaderBand = ReaderMediaSidebar.HeaderLineHeight + ReaderMediaSidebar.HeaderGap;

    /// <summary>The sidebar's top and bottom inset (its padding): above the header line and below the frame.</summary>
    internal readonly record struct Insets(double Top, double Bottom);

    /// <summary>Where the header line and the frame end up for an inset.</summary>
    internal readonly record struct Placement(double HeaderTop, double HeaderBottom, double FrameTop, double FrameBottom);

    /// <summary>
    /// The insets that put the frame's top on <paramref name="boxTop"/> and its bottom on <paramref name="boxBottom"/>. Without room above the box for the
    /// header line the top inset is 0 (the frame then starts lower than the box rather than the header leaving the sidebar); below the box there is
    /// never a negative inset.
    /// </summary>
    internal static Insets InsetsFor(double sidebarTop, double sidebarBottom, double boxTop, double boxBottom) =>
        new(Math.Max(0, boxTop - sidebarTop - HeaderBand), Math.Max(0, sidebarBottom - boxBottom));

    /// <summary>The header line's and the frame's edges for the sidebar's edges and insets (the sidebar's layout: header line, gap, frame).</summary>
    internal static Placement PlacementOf(double sidebarTop, double sidebarBottom, Insets insets)
    {
        double headerTop = sidebarTop + insets.Top, headerBottom = headerTop + ReaderMediaSidebar.HeaderLineHeight;
        return new Placement(headerTop, headerBottom, headerBottom + ReaderMediaSidebar.HeaderGap, sidebarBottom - insets.Bottom);
    }

    /// <summary>Whether the frame has the box's top and bottom and the header line stands above the frame (tests and smokes).</summary>
    internal static bool IsAligned(Placement placement, double boxTop, double boxBottom) =>
        Math.Abs(placement.FrameTop - boxTop) <= Tolerance && Math.Abs(placement.FrameBottom - boxBottom) <= Tolerance
        && placement.HeaderBottom <= placement.FrameTop + Tolerance;

    /// <summary>All values are finite and the edges are in order, so they can be measured against each other.</summary>
    internal static bool IsUsable(double sidebarTop, double sidebarBottom, double boxTop, double boxBottom) =>
        double.IsFinite(sidebarTop) && double.IsFinite(sidebarBottom) && double.IsFinite(boxTop) && double.IsFinite(boxBottom)
        && sidebarBottom > sidebarTop && boxBottom > boxTop;

    /// <summary>Whether the padding has to change (not for rounding differences).</summary>
    internal static bool Differs(Thickness current, Insets wanted) =>
        Math.Abs(current.Top - wanted.Top) > Tolerance || Math.Abs(current.Bottom - wanted.Bottom) > Tolerance;

    /// <summary>
    /// The archive view's sidebar stands in the pane's box row; it reaches up over the strip above the box by exactly the header line and its gap
    /// (negative top margin, the lift of the Media page's panel). The large reader's sidebar spans the whole frame and needs no lift.
    /// </summary>
    internal static Thickness HostMargin(bool archiveView) => archiveView ? new Thickness(0, -HeaderBand, 0, 0) : default;

    /// <summary>The horizontal insets of the sidebar: the archive view's frame is flush with its column (as the posts box), the large reader keeps its inset.</summary>
    internal static Thickness Padding(bool archiveView, double top = 0, double bottom = 0) =>
        archiveView ? new Thickness(0, top, 0, bottom) : new Thickness(16, top, 16, bottom);
}
