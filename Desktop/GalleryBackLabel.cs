namespace MeowChan.Desktop;

/// <summary>
/// When the gallery's way back (the arrow with "Back to thread", "Back to media", ...) shows its label and when only its arrow (Tom, 16.11.:
/// at half-screen widths the label was cut to its first letter). The rule is computed from the measured width of the labelled button, so a longer
/// label (German, "Back to media" against "Back to thread") collapses at a wider viewer than a short one; there is no guessed breakpoint. The
/// button keeps its accessible name and tooltip in both forms.
/// <para>
/// The way back and the favourite star beside it form one pair at the top left edge of the picture. The label shows while the whole pair
/// (the edge margin, the labelled button, the gap, the star, the margin again) takes at most <see cref="MaximumShare"/> of the viewer's width,
/// so a labelled pair never crowds the picture it floats on; wider than that it is the arrow and the star.
/// </para>
/// </summary>
internal static class GalleryBackLabel
{
    /// <summary>The way back's margin to the top and left edge, in dip (<c>GalleryViewer._close.Margin</c>).</summary>
    internal const double EdgeMargin = 12;

    /// <summary>The favourite star beside the way back: a square of the input height, in dip.</summary>
    internal const double StarWidth = 32;

    /// <summary>The widest share of the viewer's width the labelled pair may take. A fixed layout rule of the picture's corner, not a user setting.</summary>
    internal const double MaximumShare = 0.5;

    /// <summary>The width of the pair at the top left with a way back of <paramref name="labelledButtonWidth"/>, from the left edge to the star's right margin.</summary>
    internal static double PairWidth(double labelledButtonWidth, double starGap) =>
        EdgeMargin + labelledButtonWidth + starGap + StarWidth + EdgeMargin;

    /// <summary>
    /// Whether the way back shows its label. An unknown width (the viewer is not laid out yet) or an unmeasured button (0 or less) shows it, so
    /// the first layout is never the collapsed form by accident.
    /// </summary>
    internal static bool ShowsLabel(double viewerWidth, double labelledButtonWidth, double starGap) =>
        viewerWidth <= 0 || labelledButtonWidth <= 0 || PairWidth(labelledButtonWidth, starGap) <= viewerWidth * MaximumShare;

    /// <summary>The viewer width from which a way back of <paramref name="labelledButtonWidth"/> shows its label (the inverse of <see cref="ShowsLabel"/>).</summary>
    internal static double LabelFromWidth(double labelledButtonWidth, double starGap) => PairWidth(labelledButtonWidth, starGap) / MaximumShare;
}
