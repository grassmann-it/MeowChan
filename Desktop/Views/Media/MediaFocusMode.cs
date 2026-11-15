using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>What one Escape press does on the media page, in the order of its levels (M4): the focus mode ends first, then the media panel
/// hands the keyboard back to the grid (when it has the focus), then a pending "export all" question, then the selection.</summary>
internal enum MediaEscapeStep { None, ExitFocus, ReturnToGrid, DismissExportAll, ClearSelection }

/// <summary>
/// The pure rules of the media page's focus mode (M4): which level Escape ends and the sentences of the focus row and of the
/// announcement. The mode itself is a working state of the page (MediaPage.Focus.cs): it is never saved, so every start of the app and
/// every new page begins with the header shown.
/// </summary>
internal static class MediaFocusMode
{
    /// <summary>The catalog command (Shift+F by default) behind the toolbar button, the shortcut and the entry under More.</summary>
    internal const string CommandId = "media.focus-mode";

    /// <summary>The height of the focus row in dip: a layout measure (the height of an input), not a limit of the user's data.</summary>
    internal const double FocusRowHeight = 32;

    /// <summary>The level an Escape press ends: the focus mode first, so a user who focused to work keeps a pending question and the
    /// selection until the header is back; then, while the media panel has the focus, the way back to the grid (the panel closes only with
    /// its × or its switch, so its medium and the selection stay); then the question; then the selection.</summary>
    internal static MediaEscapeStep Escape(bool focusMode, bool panelFocused, bool exportAllAsk, int selectedCount) =>
        focusMode ? MediaEscapeStep.ExitFocus
        : panelFocused ? MediaEscapeStep.ReturnToGrid
        : exportAllAsk ? MediaEscapeStep.DismissExportAll
        : selectedCount > 0 ? MediaEscapeStep.ClearSelection : MediaEscapeStep.None;

    /// <summary>"Focus · 20 of 32 media" (the status line of the page behind the label); just the label while the status is empty.</summary>
    internal static string Line(string? status) =>
        string.IsNullOrWhiteSpace(status) ? Texts.T("media.focus.label") : Texts.T("media.focus.label") + " · " + status;

    /// <summary>The sentence a screen reader hears when the mode starts or ends (a polite live region).</summary>
    internal static string Announcement(bool on) => Texts.T(on ? "media.focus.on" : "media.focus.off");

    /// <summary>The filter chips as the list the focus row shows on demand, one per line; the chip keys carry the label after \u0001.</summary>
    internal static string FilterList(System.Collections.Generic.IEnumerable<string> renderedChips) =>
        string.Join("\n", System.Linq.Enumerable.Select(renderedChips, chip => chip.Substring(chip.IndexOf('\u0001') + 1)));
}
