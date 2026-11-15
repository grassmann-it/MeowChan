using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The menu of "Continue reading" (right-click or the menu key on the header button, and the same entries under the app menu): the
/// last threads, newest first, each one the command <see cref="ContinueCommandId"/> with the text of its place, then "Show full
/// history…" (<see cref="HistoryCommandId"/>). Every entry therefore carries the command's one icon. Pure composition: the window
/// supplies the saved places and what choosing one and the last entry do.
/// <para>
/// The list is user data, bounded by the setting "Threads in the Continue reading menu" (3 to 25, ten by default) and never longer than
/// the history keeps, so it is the one menu whose entry section may pass <see cref="ContextMenuModel.MaxEntriesPerSection"/>: the
/// "Show full history…" window is its answer for more.
/// </para>
/// </summary>
internal static class ReadingHistoryMenu
{
    /// <summary>The command every place entry runs (the header button, the app menu, the archive list and the history window run it too).</summary>
    internal const string ContinueCommandId = "navigation.continue-reading";
    /// <summary>The command of the last entry: it opens the window with every remembered thread.</summary>
    internal const string HistoryCommandId = "navigation.reading-history";
    /// <summary>The icon of <see cref="ContinueCommandId"/> in the catalog, wherever "continue reading" is linked.</summary>
    internal const string ContinueIcon = "book-open";

    private const int TitleDisplayLength = 60;

    /// <summary>The id of the entry of one place (one entry per thread).</summary>
    internal static string EntryId(LocalReadingPosition place) => "reading.place:" + place.Source + "/" + place.Board + "/" + place.Thread;

    /// <summary>How many places the menu lists: the setting, but never more than the history keeps (both clamped to their ranges).</summary>
    internal static int Shown(int menuEntries, int historyLimit) =>
        Math.Min(Math.Clamp(menuEntries, Config.ReadingHistoryMenuEntriesMinimum, Config.ReadingHistoryMenuEntriesMaximum),
            Math.Clamp(historyLimit, Config.ReadingHistoryLimitMinimum, Config.ReadingHistoryLimitMaximum));

    /// <summary>"{title} · No. {post} · {2 h ago}". A thread without a title snapshot shows its board and number.</summary>
    internal static string EntryText(LocalReadingHistoryEntry entry, DateTimeOffset now)
    {
        LocalReadingPosition position = entry.Position;
        string title = position.Title ?? Texts.T("resume.history.untitled", position.Board, position.Thread);
        if (title.Length > TitleDisplayLength) title = title[..(TitleDisplayLength - 1)].TrimEnd() + "…";
        return title + " · " + Texts.T("reader.postNumber", position.PostId) + " · " + ReaderDateText.Relative(entry.At, now);
    }

    /// <summary>The two lines of a row of the history window: the thread (title, or board and number) and "{source} /{board}/ · {thread} · No. {post} · {time}".</summary>
    internal static (string Title, string Detail) Row(LocalReadingHistoryEntry entry, DateTimeOffset now)
    {
        LocalReadingPosition place = entry.Position;
        string title = place.Title ?? Texts.T("resume.history.untitled", place.Board, place.Thread);
        string detail = place.Source + " /" + place.Board + "/ · " + place.Thread + " · " + Texts.T("reader.postNumber", place.PostId)
            + " · " + ReaderDateText.Relative(entry.At, now);
        return (title, detail);
    }

    /// <summary>The tooltip of an entry: the thread and the post it continues at (the title, or board and number, never empty).</summary>
    internal static string EntryTip(LocalReadingPosition place) =>
        Texts.T("resume.history.entryTip", place.Title ?? Texts.T("resume.history.untitled", place.Board, place.Thread), place.PostId);

    /// <param name="entries">The saved places, newest first (the history as the host reads it).</param>
    /// <param name="menuEntries">The setting <c>ReadingHistoryMenuEntries</c>.</param>
    /// <param name="historyLimit">The setting <c>ReadingHistoryLimit</c>.</param>
    /// <param name="canJump">False while another action owns the reader: the entries stay, disabled, with the reason.</param>
    /// <param name="jump">Opens one place.</param>
    /// <param name="showAll">Opens the window with every remembered thread.</param>
    internal static ContextMenuBuilder Compose(IReadOnlyList<LocalReadingHistoryEntry> entries, int menuEntries, int historyLimit,
        DateTimeOffset now, bool canJump, Action<LocalReadingPosition> jump, Action showAll)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(jump);
        ArgumentNullException.ThrowIfNull(showAll);
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("resume.history.menu"));
        LocalReadingHistoryEntry[] shown = entries.Take(Shown(menuEntries, historyLimit)).ToArray();
        string? busy = canJump ? null : Texts.T("archive.command.busy");
        if (shown.Length == 0) menu.Note(Texts.T("resume.history.empty"));
        for (int index = 0; index < shown.Length; index++)
        {
            LocalReadingPosition place = shown[index].Position;
            // Underscores in a thread title are access-key markers in a menu header: doubled they stay literal.
            menu.Command(ContinueCommandId, () => jump(place), canJump, busy, EntryText(shown[index], now).Replace("_", "__"),
                EntryId(place), showShortcut: index == 0);
        }
        // The last entry is the window for everything the menu cut off; it needs at least one remembered thread.
        return menu.Section().Command(HistoryCommandId, showAll, entries.Count > 0, entries.Count > 0 ? null : Texts.T("resume.history.empty"));
    }
}
