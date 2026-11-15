using MeowChan.Tags;

namespace MeowChan.Desktop;

/// <summary>Why the tagging page shows no entries: nothing queued, everything decided, a group without entries, or a state filter without a hit.</summary>
internal enum TaggingEmptyKind { None, QueueEmpty, AllDone, GroupEmpty, FilterNoHits }

/// <summary>The pure choice of the page's empty state (TG1), so the page and the tests read the same rule.</summary>
internal static class TaggingEmptyStates
{
    /// <summary>
    /// <paramref name="shown"/> is the number of rows the filter shows. An empty queue is "queue empty"; with entries but none shown, the
    /// "Open" filter on a queue without open entries is "all done" (the next steps are to clean up or reopen), any other filter has "no hits".
    /// </summary>
    internal static TaggingEmptyKind Of(TagQueueSnapshot queue, TaggingQueueFilter filter, int shown)
        => Of(queue, filter, shown, groupId: null, ungrouped: false);

    internal static TaggingEmptyKind Of(TagQueueSnapshot queue, TaggingQueueFilter filter, int shown, string? groupId, bool ungrouped)
    {
        if (shown > 0) return TaggingEmptyKind.None;
        if (groupId != null && queue.CountFor(groupId) == 0 || ungrouped && queue.CountFor(null) == 0) return TaggingEmptyKind.GroupEmpty;
        if (queue.Count == 0) return TaggingEmptyKind.QueueEmpty;
        return filter == TaggingQueueFilter.Open && TaggingQueueView.Counts(queue, groupId, ungrouped).Open == 0
            ? TaggingEmptyKind.AllDone : TaggingEmptyKind.FilterNoHits;
    }
}
