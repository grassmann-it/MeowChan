using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Gallery;
using MeowChan.Import;
using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>Which entries the tagging page lists; the queue order never changes by filtering.</summary>
internal enum TaggingQueueFilter { All, Open, Done, Skipped }

/// <summary>One shown entry: the frozen queue entry, its position in the whole queue and whether "continue" starts there.</summary>
internal sealed record TaggingQueueRow(TagQueueItem Item, int Index, bool Next, string? GroupName = null)
{
    internal TagQueueKey Key => Item.Key;
    /// <summary>The 1-based position shown in the # column and spoken by a screen reader.</summary>
    internal int Number => Index + 1;
    internal string NumberText => Number.ToString(Texts.Culture);
    internal string KindText => TaggingQueueView.KindText(Item.Kind);
    internal string Name => Item.Name.Length == 0 ? Item.AssetId : Item.Name;
    internal string SourceText => TaggingQueueView.SourceText(Item);
    internal string StateText => TaggingQueueView.StateText(Item.State);
    /// <summary>All table values, used as the keyboard/screen-reader detail when a narrow cell trims its visible text.</summary>
    internal string AccessibleDetails(int? tagCount)
    {
        string tags = tagCount is { } count ? count.ToString("N0", Texts.Culture) : "–";
        return string.Join(", ",
            Texts.T("tagging.page.col.kind") + ": " + KindText,
            Texts.T("tagging.page.col.name") + ": " + Name,
            Texts.T("tagging.page.col.source") + ": " + SourceText,
            Texts.T("tagging.page.col.group") + ": " + GroupText,
            Texts.T("tagging.page.col.tags") + ": " + tags,
            Texts.T("tagging.page.col.state") + ": " + StateText);
    }
    internal string AccessibleName
    {
        get
        {
            string text = Texts.T(Next ? "tagging.page.row.next" : "tagging.page.row", Number, Name, StateText);
            return string.IsNullOrEmpty(GroupName) ? text : text + ", " + Texts.T("tagging.page.group.named", GroupName);
        }
    }
    internal string GroupText => GroupName ?? Texts.T("tagging.page.group.ungrouped");
    /// <summary>True for a video: its tile carries a video mark (TG1).</summary>
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
internal sealed record TaggingGroupChoice(string? Id, bool Ungrouped, string Label)
{
    public override string ToString() => Label;
}

internal readonly record struct TaggingQueueCounts(int Open, int Done, int Skipped)
{
    internal int Count => Open + Done + Skipped;
}

internal static class TaggingQueueView
{
    internal static IReadOnlyList<TaggingGroupChoice> GroupChoices(TagQueueSnapshot queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        List<TaggingGroupChoice> choices =
        [
            new(null, false, Texts.T("tagging.page.group.all")),
            new(null, true, Texts.T("tagging.page.group.ungrouped"))
        ];
        choices.AddRange(queue.Groups.OrderBy(group => group.Order).Select(group => new TaggingGroupChoice(group.Id, false, group.Name)));
        return choices;
    }

    internal static bool InGroup(TagQueueItem item, string? groupId, bool ungrouped) =>
        groupId != null ? string.Equals(item.GroupId, groupId, StringComparison.Ordinal) : !ungrouped || item.GroupId == null;


    /// <summary>
    /// The entries the page shows now: the queue's entries in queue order that pass the state filter and, when given, the
    /// <paramref name="scope"/> (the one source of "current entries" that a later filter, such as a group, narrows; the walk marker and
    /// the numbers stay those of the whole queue).
    /// </summary>
    private static int? ResumeIndex(TagQueueSnapshot queue, string? groupId, bool ungrouped, Func<TagQueueItem, bool>? scope)
    {
        bool IsOpen(int index) => InGroup(queue.Items[index], groupId, ungrouped) && (scope == null || scope(queue.Items[index]))
            && queue.Items[index].State == TagQueueState.Open;
        for (int i = queue.Cursor; i < queue.Items.Count; i++) if (IsOpen(i)) return i;
        for (int i = 0; i < Math.Min(queue.Cursor, queue.Items.Count); i++) if (IsOpen(i)) return i;
        return null;
    }

    internal static int? Resume(TagQueueSnapshot queue, string? groupId = null, bool ungrouped = false, Func<TagQueueItem, bool>? scope = null) =>
        ResumeIndex(queue, groupId, ungrouped, scope);

    internal static IReadOnlyList<TaggingQueueRow> Rows(TagQueueSnapshot queue, TaggingQueueFilter filter, Func<TagQueueItem, bool>? scope = null) =>
        Rows(queue, filter, scope, null, false);

    internal static IReadOnlyList<TaggingQueueRow> Rows(TagQueueSnapshot queue, TaggingQueueFilter filter, Func<TagQueueItem, bool>? scope,
        string? groupId, bool ungrouped)
    {
        ArgumentNullException.ThrowIfNull(queue);
        int? next = ResumeIndex(queue, groupId, ungrouped, scope);
        List<TaggingQueueRow> rows = new();
        for (int index = 0; index < queue.Items.Count; index++)
        {
            TagQueueItem item = queue.Items[index];
            if (InGroup(item, groupId, ungrouped) && Shows(filter, item.State) && (scope == null || scope(item)))
                rows.Add(new TaggingQueueRow(item, index, index == next, queue.Group(item.GroupId ?? string.Empty)?.Name));
        }
        return rows;
    }

    internal static TaggingQueueCounts Counts(TagQueueSnapshot queue, string? groupId = null, bool ungrouped = false, Func<TagQueueItem, bool>? scope = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        int open = 0, done = 0, skipped = 0;
        foreach (TagQueueItem item in queue.Items)
        {
            if (!InGroup(item, groupId, ungrouped) || scope != null && !scope(item)) continue;
            switch (item.State) { case TagQueueState.Open: open++; break; case TagQueueState.Done: done++; break; default: skipped++; break; }
        }
        return new(open, done, skipped);
    }

    internal static bool Shows(TaggingQueueFilter filter, TagQueueState state) => filter switch
    {
        TaggingQueueFilter.Open => state == TagQueueState.Open,
        TaggingQueueFilter.Done => state == TagQueueState.Done,
        TaggingQueueFilter.Skipped => state == TagQueueState.Skipped,
        _ => true
    };

    /// <summary>The share of entries already decided (finished or skipped), 0 to 1; an empty queue has none.</summary>
    internal static double Share(TagQueueSnapshot queue) => Share(queue, null, false);

    internal static double Share(TagQueueSnapshot queue, string? groupId, bool ungrouped, Func<TagQueueItem, bool>? scope = null)
    {
        TaggingQueueCounts counts = Counts(queue, groupId, ungrouped, scope);
        return counts.Count == 0 ? 0 : (double)(counts.Done + counts.Skipped) / counts.Count;
    }

    internal static string ProgressText(TagQueueSnapshot queue) => ProgressText(queue, null, false);

    internal static string ProgressText(TagQueueSnapshot queue, string? groupId, bool ungrouped, Func<TagQueueItem, bool>? scope = null)
    {
        TaggingQueueCounts counts = Counts(queue, groupId, ungrouped, scope);
        return Texts.T("tagging.page.progress", counts.Open, counts.Done, counts.Skipped);
    }

    /// <summary>The visible percentage next to the bar (the culture writes the percent sign); never rounds a rest up to 100 %.</summary>
    internal static string PercentText(TagQueueSnapshot queue) => PercentText(queue, null, false);

    internal static string PercentText(TagQueueSnapshot queue, string? groupId, bool ungrouped, Func<TagQueueItem, bool>? scope = null)
    {
        double share = Share(queue, groupId, ungrouped, scope);
        double shown = Math.Floor(share * 100) / 100;
        return Texts.T("tagging.page.percent", shown);
    }

    /// <summary>
    /// The start button: "Start tagging" while nothing is decided and the walk stands at the first open entry, else "Continue at n of
    /// m" with n the position of the entry it resumes at. Null when there is nothing open, so the button is disabled.
    /// </summary>
    internal static string? StartLabel(TagQueueSnapshot queue) => StartLabel(queue, null, false);

    internal static string? StartLabel(TagQueueSnapshot queue, string? groupId, bool ungrouped, Func<TagQueueItem, bool>? scope = null)
    {
        if (ResumeIndex(queue, groupId, ungrouped, scope) is not { } resume) return null;
        TaggingQueueCounts counts = Counts(queue, groupId, ungrouped, scope);
        bool fresh = counts.Done + counts.Skipped == 0 && resume == FirstOpen(queue, groupId, ungrouped, scope);
        int ordinal = Ordinal(queue, resume, groupId, ungrouped, scope);
        return fresh ? Texts.T("command.tagging.start") : Texts.T("tagging.page.continue", ordinal, counts.Count);
    }

    /// <summary>
    /// T1: the queue position of the entry shown last, which is where "continue" starts. Null on a fresh queue (nothing decided, the
    /// cursor still at the first entry: "Start tagging" says it all) and when nothing is open.
    /// </summary>
    internal static int? LastShownIndex(TagQueueSnapshot queue) => LastShownIndex(queue, null, false);

    internal static int? LastShownIndex(TagQueueSnapshot queue, string? groupId, bool ungrouped, Func<TagQueueItem, bool>? scope = null)
    {
        ArgumentNullException.ThrowIfNull(queue);
        if (ResumeIndex(queue, groupId, ungrouped, scope) is not { } resume) return null;
        TaggingQueueCounts counts = Counts(queue, groupId, ungrouped, scope);
        return queue.Cursor > 0 || counts.Done + counts.Skipped > 0 ? resume : null;
    }

    /// <summary>T1: "Last shown: file.jpg · position 3 of 27", or null when <see cref="LastShownIndex"/> has none.</summary>
    internal static string? LastShownText(TagQueueSnapshot queue) => LastShownText(queue, null, false);

    internal static string? LastShownText(TagQueueSnapshot queue, string? groupId, bool ungrouped, Func<TagQueueItem, bool>? scope = null)
    {
        if (LastShownIndex(queue, groupId, ungrouped, scope) is not { } index) return null;
        TagQueueItem item = queue.Items[index];
        TaggingQueueCounts counts = Counts(queue, groupId, ungrouped, scope);
        return Texts.T("tagging.page.lastShown", item.Name.Length == 0 ? item.AssetId : item.Name, Ordinal(queue, index, groupId, ungrouped, scope), counts.Count);
    }

    private static int FirstOpen(TagQueueSnapshot queue, string? groupId, bool ungrouped, Func<TagQueueItem, bool>? scope)
    {
        for (int index = 0; index < queue.Items.Count; index++)
            if (InGroup(queue.Items[index], groupId, ungrouped) && (scope == null || scope(queue.Items[index])) && queue.Items[index].State == TagQueueState.Open) return index;
        return -1;
    }

    private static int Ordinal(TagQueueSnapshot queue, int index, string? groupId, bool ungrouped, Func<TagQueueItem, bool>? scope)
    {
        int ordinal = 0;
        for (int i = 0; i <= index && i < queue.Items.Count; i++)
            if (InGroup(queue.Items[i], groupId, ungrouped) && (scope == null || scope(queue.Items[i]))) ordinal++;
        return ordinal;
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
