namespace MeowChan.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Explicitly volatile authority with the same lifecycle and retry rules as durable mode.</summary>
public sealed class MemoryWorkStateStore : IWorkStateStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, WorkResource> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Url, long Generation, string MediaId, string Target), MediaWork> _media = new();

    public IReadOnlyList<WorkResource> LoadResources()
    {
        lock (_sync) return _resources.Values.ToArray();
    }

    public IReadOnlyList<MediaWork> LoadMedia(string url, long generation)
    {
        lock (_sync) return _media.Values.Where(item => item.ResourceUrl == url && item.Generation == generation).ToArray();
    }

    public WorkResource CommitIntent(WorkResource resource)
    {
        WorkStateStore.Validate(resource);
        lock (_sync)
        {
            if (_resources.TryGetValue(resource.Url, out WorkResource? old) && resource.Generation <= old.Generation)
                throw new InvalidOperationException("Watch generation must advance when intent changes.");
            _resources[resource.Url] = resource;
            return resource;
        }
    }

    public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source,
        DateTimeOffset observedAt, string? finalSnapshotJson = null)
    {
        if (!Enum.IsDefined(source) || source == ThreadSourceStateValue.Active) throw new ArgumentOutOfRangeException(nameof(source));
        lock (_sync)
        {
            if (!_resources.TryGetValue(url, out WorkResource? previous) || previous.Generation != generation
                || previous.Intent != WorkIntent.Watching) return null;
            WorkResource next = previous with { Source = source, EndObservedAt = previous.EndObservedAt ?? observedAt,
                CapturePending = previous.Source == ThreadSourceStateValue.Active || previous.CapturePending,
                FinalSnapshotJson = finalSnapshotJson ?? previous.FinalSnapshotJson };
            _resources[url] = next;
            return next;
        }
    }

    public WorkResource? CommitState(WorkResource resource)
    {
        WorkStateStore.Validate(resource);
        lock (_sync)
        {
            if (!_resources.TryGetValue(resource.Url, out WorkResource? previous) || previous.Generation != resource.Generation
                || previous.Intent == WorkIntent.Suppressed && resource.Intent != WorkIntent.Suppressed) return null;
            if (previous.EndObservedAt != null && resource.EndObservedAt != previous.EndObservedAt)
                throw new InvalidOperationException("The first observed source end is immutable within a generation.");
            _resources[resource.Url] = resource;
            return resource;
        }
    }

    public bool CommitMedia(MediaWork media)
    {
        WorkStateStore.Validate(media);
        lock (_sync)
        {
            if (!_resources.TryGetValue(media.ResourceUrl, out WorkResource? resource)
                || resource.Generation != media.Generation || resource.Intent == WorkIntent.Suppressed) return false;
            _media[(media.ResourceUrl, media.Generation, media.MediaIdentifier, media.DestinationPath)] = media;
            return true;
        }
    }
}
