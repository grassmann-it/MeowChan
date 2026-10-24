namespace MeowChan;

using System;
using System.Collections.Generic;
using System.Linq;
using MeowChan.Persistence;

internal sealed record ArchiveCleanupWorkEntry(WorkResource Resource, IReadOnlyList<MediaWork> Media);
internal sealed record ArchiveCleanupWorkState(IReadOnlyList<ArchiveCleanupWorkEntry> Entries,
    IReadOnlyList<ResourceId> ActiveThreads, bool IsStopping);

public sealed partial class Registry
{
    // The authoritative journal is read directly; the presentation work cache may lag a commit.
    // This does not restore, initialize or mutate a store. The collector compares two captures.
    internal ArchiveCleanupWorkState ReadArchiveCleanupWorkState()
    {
        if (_workStore == null) throw new InvalidOperationException("The work journal is unavailable.");
        WorkResource[] resources = _workStore.LoadResources().OrderBy(item => item.Url, StringComparer.Ordinal).ToArray();
        ArchiveCleanupWorkEntry[] entries = resources.Select(resource => new ArchiveCleanupWorkEntry(resource,
            Array.AsReadOnly(_workStore.LoadMedia(resource.Url, resource.Generation).ToArray()))).ToArray();
        ResourceId[] watches;
        bool stopping;
        lock (_state)
        {
            watches = _watches.Keys.Where(key => key.Thread != null)
                .Select(key => new ResourceId(key.Source, key.Board, key.Thread)).ToArray();
            stopping = _stopping;
        }
        // The presentation view omits not-yet-ready and stopping watches. Either may
        // still own a capture or queue item and must remain protected by this snapshot.
        return new(Array.AsReadOnly(entries), Array.AsReadOnly(watches.Concat(_queue.ReadArchiveCleanupActiveThreadIds()).Distinct()
            .OrderBy(id => id.Source, StringComparer.Ordinal).ThenBy(id => id.Board, StringComparer.Ordinal)
            .ThenBy(id => id.Thread, StringComparer.Ordinal).ToArray()), stopping);
    }
}
