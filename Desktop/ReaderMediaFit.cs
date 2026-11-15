using System;
using Avalonia;

namespace MeowChan.Desktop;

/// <summary>
/// Keeps an enlarged reader medium whole inside the visible reader (the focused post's enlargement, and any other picture the reader
/// draws larger than its thumbnail). The reader list is virtualized, so what counts is the height of the viewport minus what the post
/// puts around the medium (post header, file line, paddings): the medium may use the rest, at its aspect, never more than its
/// configured percentage and never more than the column. Pure functions: no control, no layout pass.
/// </summary>
internal static class ReaderMediaFit
{
    /// <summary>Air kept above and below a medium that is scrolled into view (fixed layout value, shrinks to fit a tight viewport).</summary>
    internal const double ScrollMargin = 8;
    /// <summary>What the card keeps under its media row: the bottom margin of the card's stack (12) and the card border (1).</summary>
    internal const double CardBottomPadding = 13;
    /// <summary>The post header block above the media row (card margin, header line, spacing) when the visual tree cannot measure it yet.</summary>
    internal const double HeaderEstimate = 44;
    /// <summary>Where a reader jump puts the focused card's top edge: this far below the top of the viewport.</summary>
    internal const double AlignedTop = 8;
    /// <summary>The margin under each reader card (the list item's vertical margin).</summary>
    internal const double CardMarginBelow = 4;
    /// <summary>
    /// What a fully capped card leaves of the viewport: the aligned top, the card margin and the scroll air below. With it a capped card
    /// placed at <see cref="AlignedTop"/> shows its bottom edge, so the jump's alignment and <see cref="ScrollDelta"/> agree.
    /// </summary>
    internal const double AlignedAir = AlignedTop + CardMarginBelow + ScrollMargin;

    /// <summary>
    /// The last measured chrome of one reader list's cards. A card that was realized but not arranged yet cannot measure its header (its
    /// layout reports no offset in its card) nor its file line; its cap then uses what the list's last arranged card measured instead of
    /// a fixed estimate, so the enlarged frame has its final height in the first layout and is not shrunk once more after the jump.
    /// </summary>
    internal sealed class ChromeMemo
    {
        private double _header = HeaderEstimate;
        private double _column = double.NaN;

        /// <summary>The header height: <paramref name="measured"/> when it is a measurement (positive, finite), which is then remembered.</summary>
        internal double Header(double measured)
        {
            if (double.IsFinite(measured) && measured > 0) _header = measured;
            return _header;
        }

        /// <summary>
        /// The height around the frame in the attachment column (file line, video row, provenance): <paramref name="measured"/> when the
        /// column was measured, which is then remembered; otherwise the remembered value, or <paramref name="measured"/> before any.
        /// </summary>
        internal double Column(double measured, bool isMeasurement)
        {
            if (isMeasurement && double.IsFinite(measured)) return _column = measured;
            return double.IsFinite(_column) ? _column : measured;
        }
    }

    /// <summary>
    /// The height a medium's frame may use: the viewport minus <paramref name="chromeHeight"/> (header, file line, paddings) and minus
    /// <paramref name="air"/> (what the viewport keeps free around the card, <see cref="AlignedAir"/> in the reader). NaN when the viewport
    /// is not known (no cap then); never below one dip.
    /// </summary>
    internal static double VisibleHeight(double viewportHeight, double chromeHeight, double air = 0)
    {
        if (!double.IsFinite(viewportHeight) || viewportHeight <= 0) return double.NaN;
        double chrome = double.IsFinite(chromeHeight) ? Math.Max(0, chromeHeight) : 0;
        double free = double.IsFinite(air) ? Math.Max(0, air) : 0;
        return Math.Max(1, viewportHeight - chrome - free);
    }

    /// <summary>
    /// The height cap of an enlarged frame: the ordinary cap times the factor, limited to the visible height, but never below the
    /// picture's ordinary shown height (an enlargement never shrinks a medium; a viewport that tiny is handled by scrolling the top in).
    /// </summary>
    internal static double HeightCap(double ordinaryMaximumHeight, double factor, double visibleHeight, double ordinaryShownHeight)
    {
        double wanted = ordinaryMaximumHeight * Math.Max(1, factor);
        if (!double.IsFinite(visibleHeight)) return wanted;
        double floor = double.IsFinite(ordinaryShownHeight) ? Math.Min(wanted, Math.Max(0, ordinaryShownHeight)) : 0;
        return Math.Min(wanted, Math.Max(floor, visibleHeight));
    }

    /// <summary>
    /// The size an enlarged medium is shown at. <paramref name="picture"/> is the ordinary (100 percent) shown size. It is scaled by
    /// <paramref name="percent"/>, but only as far as it fits the viewport height minus <paramref name="headerHeight"/> and the column width;
    /// the aspect is kept, the percentage is never exceeded, and the medium never gets smaller than its ordinary size by this rule
    /// (except where the column itself is narrower). Equals the frame <see cref="MediaSlotGeometry.Fit"/> produces with <see cref="HeightCap"/>.
    /// </summary>
    internal static Size ShownSize(double viewportHeight, double headerHeight, Size picture, int percent, double columnWidth)
    {
        if (!(picture.Width > 0) || !(picture.Height > 0) || !double.IsFinite(picture.Width) || !double.IsFinite(picture.Height)) return picture;
        double scale = Math.Max(1, percent / 100d);
        double visible = VisibleHeight(viewportHeight, headerHeight);
        if (double.IsFinite(visible)) scale = Math.Min(scale, Math.Max(1, visible / picture.Height));
        if (double.IsFinite(columnWidth) && columnWidth > 0) scale = Math.Min(scale, columnWidth / picture.Width);
        return new Size(picture.Width * scale, picture.Height * scale);
    }

    /// <summary>
    /// How far the list scrolls (positive: further down) so a medium whose frame spans <paramref name="top"/> to <paramref name="bottom"/>
    /// (dip from the top of the viewport) is visible whole, with a little air. Zero when it already is. A medium that is not shorter than
    /// the viewport (it should not be, after the cap) gets its top edge aligned to the top of the viewport.
    /// </summary>
    internal static double ScrollDelta(double top, double bottom, double viewportHeight, double margin = ScrollMargin)
    {
        if (!double.IsFinite(top) || !double.IsFinite(bottom) || !double.IsFinite(viewportHeight) || viewportHeight <= 0 || bottom < top) return 0;
        double height = bottom - top;
        if (height >= viewportHeight) return top;
        double air = Math.Max(0, Math.Min(margin, (viewportHeight - height) / 2));
        if (top < air) return top - air;
        if (bottom > viewportHeight - air) return bottom - (viewportHeight - air);
        return 0;
    }
}
