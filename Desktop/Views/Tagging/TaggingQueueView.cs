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
    /// <summary>True for a video: its tile's badges carry the play mark (TG1).</summary>
    internal bool IsVideo => Item.Kind == SavedMediaKind.Video;
    /// <summary>The catalog icon of the state (a drawn mark besides the text, never colour alone): open, finished, skipped.</summary>
    internal string StateIcon => TaggingQueueView.StateIcon(Item.State);
    /// <summary>
    /// The name of a grid tile for a screen reader and its tooltip: the row's own sentence, then the sentence of the tile's badges (video,
    /// tagging state, number of own tags or none, booru source; <paramref name="tagCount"/> null: not read yet, nothing is said about tags).
    /// </summary>
    internal string TileName(int? tagCount) => TaggingQueueView.TileName(this, tagCount);
}

/// <summary>
/// The pure part of the tagging page (PT2): which rows a filter shows, the progress sentence and share, the start label and where a
/// move lands. No Avalonia and no host, so the page and the tests read the same rules.
/// </summary>
internal static class TaggingQueueView
{
    /// <summary>
    /// The entries the page shows now: the queue's entries in queue order that pass the state filter and, when given, the
    /// <paramref name="scope"/> (the one source of "current entries" that a later filter, such as a group, narrows; the walk marker and
    /// the numbers stay those of the whole queue).
    /// </summary>
    internal static IReadOnlyList<TaggingQueueRow> Rows(TagQueueSnapshot queue, TaggingQueueFilter filter, Func<TagQueueItem, bool>? scope = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        int? next = queue.ResumeIndex();
        List<TaggingQueueRow> rows = new(filter == TaggingQueueFilter.All && scope == null ? queue.Count : 0);
        for (int index = 0; index < queue.Items.Count; index++)
        {
            TagQueueItem item = queue.Items[index];
            if (Shows(filter, item.State) && (scope == null || scope(item))) rows.Add(new TaggingQueueRow(item, index, index == next));
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
    internal static int? MoveTarget(TagQueueSnapshot queue, TaggingQueueFilter filter, TagQueueKey key, int direction, Func<TagQueueItem, bool>? scope = null)
    {
        if (direction is not (-1 or 1)) throw new ArgumentOutOfRangeException(nameof(direction));
        int from = queue.IndexOf(key);
        if (from < 0) return null;
        for (int index = from + direction; index >= 0 && index < queue.Items.Count; index += direction)
            if (Shows(filter, queue.Items[index].State) && (scope == null || scope(queue.Items[index]))) return index;
        return null;
    }

    /// <summary>
    /// "To the start" (<paramref name="toEnd"/> false) and "to the end" of the queue: the position the entry moves to, or null when it
    /// already stands there (or is not in the queue). Absolute queue positions, so a filter does not change where the end is.
    /// </summary>
    internal static int? EdgeTarget(TagQueueSnapshot queue, TagQueueKey key, bool toEnd)
    {
        int from = queue.IndexOf(key);
        if (from < 0) return null;
        int target = toEnd ? queue.Count - 1 : 0;
        return from == target ? null : target;
    }

    /// <summary>The catalog icon of a state: a circle (open), a check (finished), a skip arrow (skipped).</summary>
    internal static string StateIcon(TagQueueState state) => state switch
    {
        TagQueueState.Done => "check",
        TagQueueState.Skipped => "skip-forward",
        _ => "circle"
    };

    /// <summary>The name of a grid tile (see <see cref="TaggingQueueRow.TileName"/>).</summary>
    internal static string TileName(TaggingQueueRow row, int? tagCount)
    {
        ArgumentNullException.ThrowIfNull(row);
        string badges = TaggingTileBadges.DescribeOf(row, tagCount);
        return badges.Length == 0 ? row.AccessibleName : row.AccessibleName + ", " + badges;
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
