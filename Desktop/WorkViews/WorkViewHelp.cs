using System;
using System.Linq;

namespace MeowChan.Desktop.WorkViews;

/// <summary>Plan AP5 R9: what the shortcut help (F1) says about a view. Pure, no window.</summary>
public static class WorkViewHelp
{
    /// <summary>
    /// Whether <paramref name="view"/> keeps the button of <paramref name="commandId"/> under "More" (⋯) on one of
    /// <paramref name="pages"/> (the active page and the shell's footer). A command that some surface of those pages still
    /// shows directly is not marked: its shortcut line is accurate as it is.
    /// </summary>
    public static bool UnderMore(string commandId, WorkView view, params WorkViewPage[] pages)
    {
        WorkViewEntry[] entries = WorkViewPolicy.Entries
            .Where(entry => entry.CommandId == commandId && pages.Contains(entry.Page)
                && entry.Kind is WorkViewKind.Action or WorkViewKind.Destination).ToArray();
        return entries.Length > 0 && entries.All(entry => entry.For(view) == Presence.Overflow);
    }

    /// <summary>The policy page of the window's page; the large reader is the Reader page.</summary>
    public static WorkViewPage[] HelpPages(string selectedPageName, bool largeReaderOpen) =>
        [WorkViewPage.Shell, largeReaderOpen ? WorkViewPage.Reader
            : Enum.TryParse(selectedPageName, out WorkViewPage page) ? page : WorkViewPage.Downloads];
}
