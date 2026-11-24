namespace MeowChan.Desktop;

/// <summary>
/// The visibility and contextual routing rules for "Continue reading".
/// <para>
/// Visibility: the button is there whenever the reading history has at least one entry, in every work view and on every page
/// (also while a thread is open): the book resumes the newest place and the arrow opens the history.
/// </para>
/// <para>
/// The shortcut and application menu retain contextual routing: resume outside a thread and open the history while the
/// reader or the gallery is showing. The arrow, the right mouse button and the Menu key always open the history.
/// </para>
/// </summary>
internal static class ReadingResumeRouting
{
    /// <summary>The action of the shortcut or application menu.</summary>
    internal enum ClickAction
    {
        /// <summary>Opens the newest place of the history (the stored reading position).</summary>
        JumpToLast,
        /// <summary>Opens the list of the remembered places under the button.</summary>
        OpenHistory
    }

    /// <summary>
    /// True when the button (and its arrow) is shown. <paramref name="historyEntries"/> is the size of the loaded history snapshot;
    /// <paramref name="storedPositionPresent"/> covers the moment before the first snapshot arrives (the position is known to exist).
    /// </summary>
    internal static bool Visible(bool rememberReadingPosition, int historyEntries, bool storedPositionPresent) =>
        rememberReadingPosition && (historyEntries > 0 || storedPositionPresent);

    /// <summary>The contextual action: the history list inside a thread, the jump to the last position elsewhere.</summary>
    internal static ClickAction Click(bool threadOpen) => threadOpen ? ClickAction.OpenHistory : ClickAction.JumpToLast;

    /// <summary>The key of the sentence that names the current click action (tooltip and help text).</summary>
    internal static string TipKey(bool threadOpen) => Click(threadOpen) == ClickAction.OpenHistory ? "resume.tip.history" : "resume.tip.jump";

    /// <summary>
    /// True when the places can be opened now. A thread or gallery may be open: the selected place replaces the current
    /// reader context, and the shell closes the covering gallery before applying it. Only another action in progress blocks it.
    /// </summary>
    internal static bool PlacesOpenable(bool galleryOpen, bool otherActionBusy) => !otherActionBusy;
}
