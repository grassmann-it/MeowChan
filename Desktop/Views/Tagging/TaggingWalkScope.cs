using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Tags;

namespace MeowChan.Desktop;

internal sealed record TaggingWalkScope(string? GroupId, bool Ungrouped, Func<TagQueueItem, bool>? Filter = null, TagQueueKey? Start = null)
{
    internal bool Includes(TagQueueItem item) => TaggingQueueView.InGroup(item, GroupId, Ungrouped) && (Filter == null || Filter(item));

    // Prefer the page's marked entry even if the shared cursor moved during the queue reload.
    internal IEnumerable<TagQueueItem> OpenEntries(TagQueueSnapshot queue)
    {
        int index = Start is { } key ? queue.IndexOf(key) : -1;
        if (index < 0 || queue.Items[index].State != TagQueueState.Open || !Includes(queue.Items[index]))
        {
            if (TaggingQueueView.Resume(queue, GroupId, Ungrouped, Filter) is not { } resume) return [];
            index = resume;
        }
        return queue.Items.Skip(index).Concat(queue.Items.Take(index))
            .Where(item => item.State == TagQueueState.Open && Includes(item));
    }
}
