namespace MeowChan;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using MeowChan.Persistence;
using MeowChan.Diagnostics;

public sealed partial class Registry
{
    // _gate owns cache construction. Readers see immutable published copies only.
    private sealed record WorkCacheEntry(WorkResource Resource, ThreadWorkSnapshot Snapshot);
    private Dictionary<string, WorkCacheEntry> _workCacheEntries = new(StringComparer.Ordinal);

    private WorkResource CommitWorkIntent(WorkResource resource)
    {
        WorkStateAccess access = WorkStateAccess.For(_workStore!);
        using IDisposable fence = access.Enter(resource.Url);
        return access.CommitIntentUnderFence(resource);
    }

    private WorkResource? CommitWorkState(WorkResource resource)
    {
        WorkStateAccess access = WorkStateAccess.For(_workStore!);
        using IDisposable fence = access.Enter(resource.Url);
        return access.CommitStateUnderFence(resource);
    }

    private WorkResource? CommitWorkEnd(string url, long generation, ThreadSourceStateValue source,
        DateTimeOffset observedAt, string? finalSnapshotJson)
    {
        WorkStateAccess access = WorkStateAccess.For(_workStore!);
        using IDisposable fence = access.Enter(url);
        return access.CommitEndUnderFence(url, generation, source, observedAt, finalSnapshotJson);
    }

    private static WorkCacheEntry CreateWorkCacheEntry(WorkResource resource, IReadOnlyList<MediaWork> media)
        => new(resource, new ThreadWorkSnapshot(resource.Url, resource.Intent, resource.Source,
            resource.EndObservedAt, resource.CapturePending, Array.AsReadOnly(media.ToArray())));

    private void RefreshWorkCache()
    {
        if (_workStore == null) return;
        WorkStateAccess access = WorkStateAccess.For(_workStore);
        (bool full, string[] urls) = access.TakeDirty();
        if (!full && urls.Length == 0) return;
        using var measurement = PerformanceDiagnostics.Measure("registry.work-cache");
        try
        {
            Dictionary<string, WorkCacheEntry> next = full ? new(StringComparer.Ordinal) : new(_workCacheEntries, StringComparer.Ordinal);
            // Full reconstruction is explicit: startup/recovery, unknown invalidation or bounded-set overflow.
            if (full) urls = _workStore.LoadResources().Where(resource => resource.IsThread).Select(resource => resource.Url).ToArray();
            foreach (string url in urls)
            {
                using IDisposable fence = access.Enter(url);
                WorkResource? resource = access.FindResource(url);
                if (resource == null || !resource.IsThread) next.Remove(url);
                else next[url] = CreateWorkCacheEntry(resource, _workStore.LoadMedia(url, resource.Generation));
            }
            _workCacheEntries = next;
            PublishWorkCache();
            PerformanceDiagnostics.Record("registry.work-cache", full ? "full" : "targeted", value: urls.Length);
        }
        catch
        {
            // Keep the last complete view. Recovery is explicit on the next external invalidation;
            // this does not schedule a self-triggering persistence retry loop.
            access.InvalidateAll();
            throw;
        }
    }

    private void PublishWorkCache()
    {
        ThreadWorkSnapshot[] current = _workCacheEntries.Values.Select(entry => entry.Snapshot)
            .OrderBy(item => item.Url, StringComparer.Ordinal).ToArray();
        Volatile.Write(ref _workCacheByUrl, new ReadOnlyDictionary<string, ThreadWorkSnapshot>(
            current.ToDictionary(item => item.Url, StringComparer.Ordinal)));
        Volatile.Write(ref _workCache, current);
    }
}
