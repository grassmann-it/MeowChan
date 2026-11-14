using System;
using System.Collections.Generic;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>Which entries the tagging page lists; the queue order never changes by filtering.</summary>
internal enum TaggingQueueFilter { All, Open, Done, Skipped }

/// <summary>One shown entry: the frozen queue entry, its position in the whole queue and whether "continue" starts there.</summary>
internal sealed record TaggingQueueRow(TagQueueItem Item, int Index, bool Next)
{
    internal TagQueueKey Key => Item.Key;
    /// <summary>The 1-based position shown in the # column and spoken by a screen reader.</summary>
    internal int Number => Index + 1;
    internal string NumberText => Number.ToString(Texts.Culture);
    internal string KindText => TaggingQueueView.KindText(Item.Kind);
    internal string Name => Item.Name.Length == 0 ? Item.AssetId : Item.Name;
    internal string SourceText => TaggingQueueView.SourceText(Item);
    internal string StateText => TaggingQueueView.StateText(Item.State);
    internal string AccessibleName => Texts.T(Next ? "tagging.page.row.next" : "tagging.page.row", Number, Name, StateText);
}

/// <summary>
/// The pure part of the tagging page (PT2): which rows a filter shows, the progress sentence and share, the start label and where a
/// move lands. No Avalonia and no host, so the page and the tests read the same rules.
/// </summary>
internal static class TaggingQueueView
{
    internal static IReadOnlyList<TaggingQueueRow> Rows(TagQueueSnapshot queue, TaggingQueueFilter filter)
    {
        ArgumentNullException.ThrowIfNull(queue);
        int? next = queue.ResumeIndex();
        List<TaggingQueueRow> rows = new(filter == TaggingQueueFilter.All ? queue.Count : 0);
        for (int index = 0; index < queue.Items.Count; index++)
        {
            TagQueueItem item = queue.Items[index];
            if (Shows(filter, item.State)) rows.Add(new TaggingQueueRow(item, index, index == next));
        }
        return rows;
    }

    internal static bool Shows(TaggingQueueFilter filter, TagQueueState state) => filter switch
    {
        TaggingQueueFilter.Open => state == TagQueueState.Open,
        TaggingQueueFilter.Done => state == TagQueueState.Done,
        TaggingQueueFilter.Skipped => state == TagQueueState.Skipped,
        _ => true
    };

    /// <summary>The share of entries already decided (finished or skipped), 0 to 1; an empty queue has none.</summary>
    internal static double Share(TagQueueSnapshot queue) => queue.Count == 0 ? 0 : (double)(queue.Done + queue.Skipped) / queue.Count;

    internal static string ProgressText(TagQueueSnapshot queue) => Texts.T("tagging.page.progress", queue.Open, queue.Done, queue.Skipped);

    /// <summary>The visible percentage next to the bar (the culture writes the percent sign); never rounds a rest up to 100 %.</summary>
    internal static string PercentText(TagQueueSnapshot queue)
    {
        double share = Share(queue);
        double shown = Math.Floor(share * 100) / 100;
        return Texts.T("tagging.page.percent", shown);
    }

    /// <summary>
    /// The start button: "Start tagging" while nothing is decided and the walk stands at the first open entry, else "Continue at n of
    /// m" with n the position of the entry it resumes at. Null when there is nothing open, so the button is disabled.
    /// </summary>
    internal static string? StartLabel(TagQueueSnapshot queue)
    {
        if (queue.ResumeIndex() is not { } resume) return null;
        bool fresh = queue.Done + queue.Skipped == 0 && resume == FirstOpen(queue);
        return fresh ? Texts.T("command.tagging.start") : Texts.T("tagging.page.continue", resume + 1, queue.Count);
    }

    /// <summary>
    /// T1: the queue position of the entry shown last, which is where "continue" starts. Null on a fresh queue (nothing decided, the
    /// cursor still at the first entry: "Start tagging" says it all) and when nothing is open.
    /// </summary>
    internal static int? LastShownIndex(TagQueueSnapshot queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (queue.ResumeIndex() is not { } resume) return null;
        return queue.Cursor > 0 || queue.Done + queue.Skipped > 0 ? resume : null;
    }

    /// <summary>T1: "Last shown: file.jpg · position 3 of 27", or null when <see cref="LastShownIndex"/> has none.</summary>
    internal static string? LastShownText(TagQueueSnapshot queue)
    {
        if (LastShownIndex(queue) is not { } index) return null;
        TagQueueItem item = queue.Items[index];
        return Texts.T("tagging.page.lastShown", item.Name.Length == 0 ? item.AssetId : item.Name, index + 1, queue.Count);
    }

    private static int FirstOpen(TagQueueSnapshot queue)
    {
        for (int index = 0; index < queue.Items.Count; index++)
            if (queue.Items[index].State == TagQueueState.Open) return index;
        return -1;
    }

    /// <summary>
    /// Where <paramref name="key"/> lands when it moves one shown row up (<paramref name="direction"/> −1) or down (+1): the queue
    /// position of the neighbouring shown row, so a filtered list moves past the entries it hides. Null at the edge of the list.
    /// </summary>
    internal static int? MoveTarget(TagQueueSnapshot queue, TaggingQueueFilter filter, TagQueueKey key, int direction)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        int from = queue.IndexOf(key);
        if (from < 0) return null;
        for (int index = from + direction; index >= 0 && index < queue.Items.Count; index += direction)
            if (Shows(filter, queue.Items[index].State)) return index;
        return null;
    }

    internal static string StateText(TagQueueState state) => Texts.T(state switch
    {
        TagQueueState.Done => "tagging.page.state.done",
        TagQueueState.Skipped => "tagging.page.state.skipped",
        _ => "tagging.page.state.open"
    });

    internal static string FilterText(TaggingQueueFilter filter) => filter == TaggingQueueFilter.All ? Texts.T("tagging.page.filter.all")
        : StateText(filter switch { TaggingQueueFilter.Done => TagQueueState.Done, TaggingQueueFilter.Skipped => TagQueueState.Skipped, _ => TagQueueState.Open });

    internal static string KindText(SavedMediaKind kind) => Texts.T(kind switch
    {
        SavedMediaKind.Image => "import.kind.image",
        SavedMediaKind.Video => "import.kind.video",
        SavedMediaKind.Other => "import.kind.other",
        _ => "import.kind.unknown"
    });

    /// <summary>Own imports and provider media by their name; a chan thread as source · /board/ · thread.</summary>
    internal static string SourceText(TagQueueItem item)
    {
        ResourceId thread = item.Thread;
        if (ManualImportIdentity.IsImportThread(thread)) return Texts.T("import.media.source");
        if (BooruMediaIdentity.IsBooruThread(thread)) return Texts.T("booru.media.source");
        string board = string.IsNullOrEmpty(thread.Board) ? string.Empty : " · /" + thread.Board + "/";
        string number = string.IsNullOrEmpty(thread.Thread) ? string.Empty : " · " + thread.Thread;
        return thread.Source + board + number;
    }
}
