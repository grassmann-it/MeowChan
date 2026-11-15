using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop;

/// <summary>
/// The list of "Continue reading" (the arrow next to the header button, right-click or the menu key on it, and the app menu entry): the
/// last threads, newest first, each one the command <see cref="ContinueCommandId"/> with the text of its place, then "Show full
/// history…" (<see cref="HistoryCommandId"/>). Every row therefore carries the command's one icon. Pure composition of the rows
/// (<see cref="Rows"/>); <see cref="ReadingHistoryFlyout"/> shows them.
/// <para>
/// The list is user data, bounded by the setting "Threads in the Continue reading menu" (3 to 25, ten by default) and never longer than
/// the history keeps; the "Show full history…" window is the answer for more.
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

    /// <summary>
    /// The places the history list shows: the setting (<see cref="Shown"/>) many, newest first. Each row carries the thread's title on
    /// the first line, where and when on the second and the sentence of its tooltip; its text for a screen reader is the one-line
    /// <see cref="EntryText"/>.
    /// </summary>
    /// <param name="entries">The saved places, newest first (the history as the host reads it).</param>
    /// <param name="menuEntries">The setting <c>ReadingHistoryMenuEntries</c>.</param>
    /// <param name="historyLimit">The setting <c>ReadingHistoryLimit</c>.</param>
    internal static IReadOnlyList<ReadingHistoryRow> Rows(IReadOnlyList<LocalReadingHistoryEntry> entries, int menuEntries, int historyLimit, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Take(Shown(menuEntries, historyLimit)).Select(entry =>
        {
            (string title, string detail) = Row(entry, now);
            return new ReadingHistoryRow(EntryId(entry.Position), entry.Position, title, detail, EntryTip(entry.Position), EntryText(entry, now));
        }).ToArray();
    }
}

/// <summary>One place of the history list (see <see cref="ReadingHistoryMenu.Rows"/>).</summary>
/// <param name="Id">The stable id of the row (one per thread).</param>
/// <param name="Place">What opening the row continues at.</param>
/// <param name="Title">First line: the thread's title, or its board and number.</param>
/// <param name="Detail">Second line: source, board, thread, post number and how long ago.</param>
/// <param name="Tip">The tooltip sentence: the thread and the post it continues at.</param>
/// <param name="Text">The one-line text ("title · No. n · time").</param>
internal sealed record ReadingHistoryRow(string Id, LocalReadingPosition Place, string Title, string Detail, string Tip, string Text);
