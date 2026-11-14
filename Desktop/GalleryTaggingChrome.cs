namespace MeowChan.Desktop;

/// <summary>
/// The "say it once" rules of the gallery chrome while the tagging mode runs (PLAN-TAGGING-GALERIE-UX-1.1 §6, QW1 to QW5), as pure
/// functions so they are tested without a window and fold into the later chrome plan (G1). Each rule names the one place that keeps
/// a piece of information; the viewer only applies the answer.
/// </summary>
internal static class GalleryTaggingChrome
{
    /// <summary>
    /// QW1: the position ("Queue 3 of 27") stands once, between the arrows. The tag panel's head shows no "Medium 3 of 27" in the
    /// tagging mode; outside the mode it shows it as before.
    /// </summary>
    internal static string? PanelCounter(string? counter, bool taggingMode) => taggingMode ? null : counter;

    /// <summary>
    /// QW1: the filmstrip repeats the position only while the arrows are away (the controls are hidden and the strip peeks in alone
    /// at the top edge); with the arrows on the screen it shows no text.
    /// </summary>
    internal static string? FilmstripPosition(string? position, bool controlsHidden) => controlsHidden ? position : null;

    /// <summary>
    /// QW2: the strip's status line speaks only without the tag panel; with the panel open its own status line says every sentence
    /// (decisions, undo, errors), so it is never read or shown twice.
    /// </summary>
    internal static bool StripStatusVisible(bool panelCoupled) => !panelCoupled;

    /// <summary>
    /// QW5: the info line's "Rating: …" yields to the panel's favorite and rating row, which shows the same value for the same
    /// medium (or for the marked ones) and is the one place to change it.
    /// </summary>
    internal static bool InfoRatingVisible(bool mediaFocus, bool decisionRowVisible) => !mediaFocus && !decisionRowVisible;
}
