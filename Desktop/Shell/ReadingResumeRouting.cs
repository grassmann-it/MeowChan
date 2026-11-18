namespace MeowChan.Desktop;

/// <summary>
/// The rules of the header "Continue reading" button as plain functions (tests and the window call the same ones).
/// <para>
/// Visibility: the button is there whenever the reading history has at least one entry, in every work view and on every page
/// (also while a thread is open): it is the way back to a place and, inside a thread, the way to another one.
/// </para>
/// <para>
/// Left click: outside a thread it jumps to the last reading position; inside an open thread (the reader or the gallery over it is
/// showing) a jump would replace what is on screen, so the click opens the history list and the person picks the place. The arrow
/// next to the button, the right mouse button and the Menu key always open the history.
/// </para>
/// </summary>
internal static class ReadingResumeRouting
{
    /// <summary>What a click on the button itself does.</summary>
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

    /// <summary>The left click: the history list inside an open thread, the jump to the last position anywhere else.</summary>
    internal static ClickAction Click(bool threadOpen) => threadOpen ? ClickAction.OpenHistory : ClickAction.JumpToLast;

    /// <summary>The key of the sentence that names the current click action (tooltip and help text).</summary>
    internal static string TipKey(bool threadOpen) => Click(threadOpen) == ClickAction.OpenHistory ? "resume.tip.history" : "resume.tip.jump";

    /// <summary>
    /// True when the places can be opened now. A thread or gallery may be open: the selected place replaces the current
    /// reader context, and the shell closes the covering gallery before applying it. Only another action in progress blocks it.
    /// </summary>
    internal static bool PlacesOpenable(bool galleryOpen, bool otherActionBusy) => !otherActionBusy;
}
