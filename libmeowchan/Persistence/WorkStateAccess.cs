namespace MeowChan.Persistence;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Diagnostics;

// Optional internal capability: no new member is required from existing IWorkStateStore callers.
// Conditional writes must compare the complete expected row (null means absent), AND current
// owner generation/suppression in the same transaction. Point reads validate key/payload identity.
internal interface IConditionalWorkStateStore
{
    WorkResource? FindResource(string url);
    MediaWork? FindMedia(MediaWorkKey key);
    bool TryCommitMedia(MediaWork next, MediaWork? expected);
    WorkResource CommitIntentUnderFence(WorkResource resource);
    WorkResource? CommitStateUnderFence(WorkResource resource);
    WorkResource? CommitEndUnderFence(string url, long generation, ThreadSourceStateValue source, DateTimeOffset observedAt, string? finalSnapshotJson);
}

internal readonly record struct MediaWorkKey(string Url, long Generation, string MediaId, string Variant, string Target)
{
    internal static MediaWorkKey Of(MediaWork media) => new(media.ResourceUrl, media.Generation,
        media.MediaIdentifier, media.Variant, media.DestinationPath);
}

internal static class WorkStateComparison
{
    // MediaAttachmentReferences supplies immutable value equality, including JSON roundtrips.
    internal static bool Equal(MediaWork? first, MediaWork? second) => first == second;
}

internal readonly record struct WorkQueryCounts(long ResourcePoints, long MediaPoints, long ConditionalWrites,
    long FallbackReads, long FallbackWrites);

/// <summary>
/// One coordinator per store instance, shared by queue and Registry. Only live transitions retain
/// gates; there is no URL-history cache. Order: directory/file/integrity leases -> resource fence
/// -> short queue state OR profile lease. Never take a directory lease or drain under this fence.
/// Foreign stores retain their existing API; fallback read/modify/write is serialized for these
/// participants, but cannot provide a transaction against uncoordinated foreign writers.
/// </summary>
internal sealed class WorkStateAccess
{
    private static readonly ConditionalWeakTable<object, WorkStateAccess> Instances = new();
    private readonly IWorkStateStore _store;
    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _gates = new(StringComparer.Ordinal);
    private const int MaximumDirtyResources = 256;
    private readonly HashSet<string> _dirty = new(StringComparer.Ordinal);
    private bool _rebuild = true;
    private long _resourcePoints, _mediaPoints, _conditionalWrites, _fallbackReads, _fallbackWrites;
    internal static WorkStateAccess For(IWorkStateStore store) => Instances.GetValue(
        store is WorkStateStore sqlite ? sqlite.CoordinationIdentity : store, _ => new(store));
    private WorkStateAccess(IWorkStateStore store) => _store = store;
    internal WorkQueryCounts Counts => new(Interlocked.Read(ref _resourcePoints), Interlocked.Read(ref _mediaPoints),
        Interlocked.Read(ref _conditionalWrites), Interlocked.Read(ref _fallbackReads), Interlocked.Read(ref _fallbackWrites));
    internal int GateCount { get { lock (_sync) return _gates.Count; } }

    internal WorkResource? FindResource(string url)
    {
        Interlocked.Increment(ref _resourcePoints);
        PerformanceDiagnostics.Record("work.query", "resource-point", watch: url, value: 1);
        if (_store is IConditionalWorkStateStore fast) return fast.FindResource(url);
        Interlocked.Increment(ref _fallbackReads);
        PerformanceDiagnostics.Record("work.query", "fallback-batch", value: 1);
        return _store.LoadResources().SingleOrDefault(resource => resource.Url == url);
    }

    internal MediaWork? FindMedia(MediaWorkKey key)
    {
        Interlocked.Increment(ref _mediaPoints);
        PerformanceDiagnostics.Record("work.query", "media-point", watch: key.Url, asset: key.MediaId, epoch: key.Generation, value: 1);
        if (_store is IConditionalWorkStateStore fast) return fast.FindMedia(key);
        Interlocked.Increment(ref _fallbackReads);
        PerformanceDiagnostics.Record("work.query", "fallback-batch", value: 1);
        return _store.LoadMedia(key.Url, key.Generation).SingleOrDefault(media => MediaWorkKey.Of(media) == key);
    }

    // Caller owns the URL fence, including in the compatibility case.
    internal bool TryCommitMedia(MediaWork next, MediaWork? expected)
    {
        Interlocked.Increment(ref _conditionalWrites);
        if (expected != null && MediaWorkKey.Of(next) != MediaWorkKey.Of(expected))
            throw new ArgumentException("A conditional update cannot change its media key.", nameof(next));
        bool committed;
        try
        {
            using (PerformanceDiagnostics.Measure("work.conditional", watch: next.ResourceUrl, asset: next.MediaIdentifier, epoch: next.Generation))
            {
                if (_store is IConditionalWorkStateStore fast) committed = fast.TryCommitMedia(next, expected);
                else
                {
                    Interlocked.Increment(ref _fallbackWrites);
                    PerformanceDiagnostics.Record("work.query", "fallback-write", watch: next.ResourceUrl, value: 1);
                    WorkResource? owner = FindResource(next.ResourceUrl);
                    committed = owner != null && owner.Generation == next.Generation && owner.Intent != WorkIntent.Suppressed
                        && WorkStateComparison.Equal(FindMedia(MediaWorkKey.Of(next)), expected) && _store.CommitMedia(next);
                }
            }
        }
        catch
        {
            // A write can throw after adopting its transaction. Do not retry or announce success
            // until an authoritative read confirms the exact row and its still-current owner.
            bool confirmed = false;
            try
            {
                WorkResource? owner = FindResource(next.ResourceUrl);
                confirmed = owner != null && owner.Generation == next.Generation && owner.Intent != WorkIntent.Suppressed
                    && WorkStateComparison.Equal(FindMedia(MediaWorkKey.Of(next)), next);
            }
            catch { /* Keep the original persistence failure when recovery cannot establish authority. */ }
            if (!confirmed) throw;
            PerformanceDiagnostics.Record("work.conditional", "confirmed-after-error", watch: next.ResourceUrl, asset: next.MediaIdentifier, epoch: next.Generation);
            committed = true;
        }
        if (committed) MarkDirty(next.ResourceUrl);
        return committed;
    }

    internal void MarkDirty(string url)
    {
        lock (_sync)
        {
            if (_rebuild) return;
            if (_dirty.Count == MaximumDirtyResources && !_dirty.Contains(url)) { _dirty.Clear(); _rebuild = true; }
            else _dirty.Add(url);
        }
    }

    internal bool IsDirty(string url) { lock (_sync) return _rebuild || _dirty.Contains(url); }
    internal void ClearDirty(string url) { lock (_sync) if (!_rebuild) _dirty.Remove(url); }
    internal void InvalidateAll() { lock (_sync) { _rebuild = true; _dirty.Clear(); } }
    internal (bool Full, string[] Urls) TakeDirty()
    {
        lock (_sync)
        {
            (bool, string[]) batch = (_rebuild, _dirty.ToArray());
            _rebuild = false; _dirty.Clear();
            return batch;
        }
    }
    internal (bool Full, int Count) DirtyCount { get { lock (_sync) return (_rebuild, _dirty.Count); } }

    // Caller owns the resource fence. These bypass the built-in public entry point's fence,
    // not its validation, transaction, generation rules or profile lease.
    internal WorkResource CommitIntentUnderFence(WorkResource resource)
    {
        WorkResource result;
        try { result = _store is IConditionalWorkStateStore fast ? fast.CommitIntentUnderFence(resource) : _store.CommitIntent(resource); }
        catch
        {
            bool confirmed = false;
            try { confirmed = FindResource(resource.Url) == resource; } catch { }
            if (!confirmed) throw;
            result = resource;
        }
        MarkDirty(resource.Url);
        return result;
    }
    internal WorkResource? CommitStateUnderFence(WorkResource resource)
    {
        WorkResource? result;
        try { result = _store is IConditionalWorkStateStore fast ? fast.CommitStateUnderFence(resource) : _store.CommitState(resource); }
        catch
        {
            bool confirmed = false;
            try { confirmed = FindResource(resource.Url) == resource; } catch { }
            if (!confirmed) throw;
            result = resource;
        }
        if (result != null) MarkDirty(resource.Url);
        return result;
    }

    internal WorkResource? CommitEndUnderFence(string url, long generation, ThreadSourceStateValue source,
        DateTimeOffset observedAt, string? finalSnapshotJson)
    {
        WorkResource? before = FindResource(url);
        WorkResource? expected = before == null || before.Generation != generation || before.Intent != WorkIntent.Watching ? null
            : before with { Source = source, EndObservedAt = before.EndObservedAt ?? observedAt,
                CapturePending = before.Source == ThreadSourceStateValue.Active || before.CapturePending,
                FinalSnapshotJson = finalSnapshotJson ?? before.FinalSnapshotJson };
        WorkResource? result;
        try
        {
            result = _store is IConditionalWorkStateStore fast
                ? fast.CommitEndUnderFence(url, generation, source, observedAt, finalSnapshotJson)
                : _store.CommitEnd(url, generation, source, observedAt, finalSnapshotJson);
        }
        catch
        {
            bool confirmed = false;
            try { confirmed = expected != null && FindResource(url) == expected; } catch { }
            if (!confirmed) throw;
            result = expected;
        }
        if (result != null) MarkDirty(url);
        return result;
    }

    internal IDisposable Enter(string url)
    {
        Entry entry = Retain(url);
        try { entry.Gate.Wait(); return new Lease(this, url, entry); }
        catch { Release(url, entry, acquired: false); throw; }
    }

    internal async ValueTask<IDisposable> EnterAsync(string url, CancellationToken token = default)
    {
        Entry entry = Retain(url);
        try { await entry.Gate.WaitAsync(token).ConfigureAwait(false); return new Lease(this, url, entry); }
        catch { Release(url, entry, acquired: false); throw; }
    }

    private Entry Retain(string url)
    {
        lock (_sync)
        {
            if (!_gates.TryGetValue(url, out Entry? entry)) _gates.Add(url, entry = new());
            entry.Users++;
            return entry;
        }
    }

    private void Release(string url, Entry entry, bool acquired)
    {
        lock (_sync)
        {
            if (acquired) entry.Gate.Release();
            if (--entry.Users == 0) { _gates.Remove(url); entry.Gate.Dispose(); }
        }
    }

    private sealed class Entry { internal readonly SemaphoreSlim Gate = new(1, 1); internal int Users; }
    private sealed class Lease(WorkStateAccess owner, string url, Entry entry) : IDisposable
    {
        private int _released;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Release(url, entry, acquired: true); }
    }
}
