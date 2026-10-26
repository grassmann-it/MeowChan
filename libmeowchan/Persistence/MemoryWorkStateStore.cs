namespace MeowChan.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Explicitly volatile authority with the same lifecycle and retry rules as durable mode.</summary>
public sealed class MemoryWorkStateStore : IWorkStateStore, IConditionalWorkStateStore
{
    private readonly object _sync = new();
    private readonly Dictionary<string, WorkResource> _resources = new(StringComparer.Ordinal);
    private readonly Dictionary<MediaWorkKey, MediaWork> _media = new();

    public IReadOnlyList<WorkResource> LoadResources()
    {
        lock (_sync) return _resources.Values.ToArray();
    }

    public IReadOnlyList<MediaWork> LoadMedia(string url, long generation)
    {
        lock (_sync) return _media.Values.Where(item => item.ResourceUrl == url && item.Generation == generation).ToArray();
    }

    WorkResource? IConditionalWorkStateStore.FindResource(string url)
    {
        lock (_sync) return _resources.GetValueOrDefault(url);
    }

    MediaWork? IConditionalWorkStateStore.FindMedia(MediaWorkKey key)
    {
        lock (_sync) return _media.GetValueOrDefault(key);
    }

    bool IConditionalWorkStateStore.TryCommitMedia(MediaWork next, MediaWork? expected)
    {
        WorkStateStore.Validate(next);
        lock (_sync)
        {
            if (!_resources.TryGetValue(next.ResourceUrl, out WorkResource? owner)
                || owner.Generation != next.Generation || owner.Intent == WorkIntent.Suppressed
                || !WorkStateComparison.Equal(_media.GetValueOrDefault(MediaWorkKey.Of(next)), expected)) return false;
            _media[MediaWorkKey.Of(next)] = next;
            return true;
        }
    }

    public WorkResource CommitIntent(WorkResource resource)
    {
        using IDisposable fence = WorkStateAccess.For(this).Enter(resource.Url);
        WorkResource result = CommitIntentCore(resource);
        WorkStateAccess.For(this).MarkDirty(resource.Url);
        return result;
    }
    WorkResource IConditionalWorkStateStore.CommitIntentUnderFence(WorkResource resource) => CommitIntentCore(resource);

    public WorkResource? CommitState(WorkResource resource)
    {
        using IDisposable fence = WorkStateAccess.For(this).Enter(resource.Url);
        WorkResource? result = CommitStateCore(resource);
        if (result != null) WorkStateAccess.For(this).MarkDirty(resource.Url);
        return result;
    }
    WorkResource? IConditionalWorkStateStore.CommitStateUnderFence(WorkResource resource) => CommitStateCore(resource);

    public WorkResource? CommitEnd(string url, long generation, ThreadSourceStateValue source,
        DateTimeOffset observedAt, string? finalSnapshotJson = null)
    {
        using IDisposable fence = WorkStateAccess.For(this).Enter(url);
        WorkResource? result = CommitEndCore(url, generation, source, observedAt, finalSnapshotJson);
        if (result != null) WorkStateAccess.For(this).MarkDirty(url);
        return result;
    }

    WorkResource? IConditionalWorkStateStore.CommitEndUnderFence(string url, long generation,
        ThreadSourceStateValue source, DateTimeOffset observedAt, string? finalSnapshotJson)
        => CommitEndCore(url, generation, source, observedAt, finalSnapshotJson);

    public bool CommitMedia(MediaWork media)
    {
        using IDisposable fence = WorkStateAccess.For(this).Enter(media.ResourceUrl);
        bool result = CommitMediaCore(media);
        if (result) WorkStateAccess.For(this).MarkDirty(media.ResourceUrl);
        return result;
    }

    private WorkResource CommitIntentCore(WorkResource resource)
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

    private WorkResource? CommitEndCore(string url, long generation, ThreadSourceStateValue source,
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

    private WorkResource? CommitStateCore(WorkResource resource)
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

    private bool CommitMediaCore(MediaWork media)
    {
        WorkStateStore.Validate(media);
        lock (_sync)
        {
            if (!_resources.TryGetValue(media.ResourceUrl, out WorkResource? resource)
                || resource.Generation != media.Generation || resource.Intent == WorkIntent.Suppressed) return false;
            _media[MediaWorkKey.Of(media)] = media;
            return true;
        }
    }
}
