namespace MeowChan.Downloader;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Session-local bounded FIFO admission, rotating sources and both work classes.</summary>
internal sealed class MetadataAdmission
{
    private const int MaximumWaiters = 256;
    private readonly object _sync = new();
    private readonly LinkedList<Waiter> _waiting = new();
    private readonly Dictionary<string, Source> _sources = new(StringComparer.Ordinal);
    private TaskCompletionSource _changed = Signal();
    private int _active;
    private long _round;
    private bool _preferManual = true;
    private bool _closed;

    public (int Active, int Waiting) Snapshot { get { lock (_sync) return (_active, _waiting.Count); } }

    public async Task<IDisposable> AcquireAsync(string sourceId, DownloadPriority priority, CancellationToken token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        LinkedListNode<Waiter>? node = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Task changed;
                lock (_sync)
                {
                    if (_closed) throw new QueueStoppingException(token);
                    // Overflow callers retain only their own async frame, never another task/list per catalog entry.
                    if (node == null && _waiting.Count < MaximumWaiters)
                    {
                        if (!_sources.TryGetValue(sourceId, out Source? source))
                            _sources.Add(sourceId, source = new Source { LastDispatch = _round });
                        source.Waiters++;
                        node = _waiting.AddLast(new Waiter(sourceId, source, priority));
                    }
                    if (node != null && _active < 4 && ReferenceEquals(ChooseLocked(), node))
                    {
                        Waiter next = node.Value;
                        _waiting.Remove(node);
                        node = null;
                        next.Source.Waiters--;
                        next.Source.Active = true;
                        next.Source.LastDispatch = ++_round;
                        _active++;
                        _preferManual = priority != DownloadPriority.Manual;
                        PulseLocked();
                        return new Lease(this, sourceId, next.Source);
                    }
                    changed = _changed.Task;
                }
                await changed.WaitAsync(token).ConfigureAwait(false);
            }
        }
        finally
        {
            if (node != null) lock (_sync)
            {
                _waiting.Remove(node);
                node.Value.Source.Waiters--;
                RetireLocked(node.Value.Id, node.Value.Source);
                PulseLocked();
            }
        }
    }

    public void Close() { lock (_sync) { _closed = true; PulseLocked(); } }

    private LinkedListNode<Waiter>? ChooseLocked()
    {
        DownloadPriority preferred = _preferManual ? DownloadPriority.Manual : DownloadPriority.BoardDiscovery;
        LinkedListNode<Waiter>? chosen = null;
        for (LinkedListNode<Waiter>? node = _waiting.First; node != null; node = node.Next)
        {
            if (node.Value.Source.Active) continue;
            bool matches = node.Value.Priority == preferred;
            bool chosenMatches = chosen?.Value.Priority == preferred;
            if (chosen == null || matches && !chosenMatches || matches == chosenMatches
                && node.Value.Source.LastDispatch < chosen.Value.Source.LastDispatch) chosen = node;
        }
        return chosen;
    }

    private void Release(string id, Source source)
    {
        lock (_sync)
        {
            source.Active = false;
            _active--;
            RetireLocked(id, source);
            PulseLocked();
        }
    }
    private void RetireLocked(string id, Source source)
    {
        if (!source.Active && source.Waiters == 0) _sources.Remove(id);
    }
    private void PulseLocked() { TaskCompletionSource old = _changed; _changed = Signal(); old.TrySetResult(); }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Source { public bool Active; public int Waiters; public long LastDispatch; }
    private sealed record Waiter(string Id, Source Source, DownloadPriority Priority);
    private sealed class Lease(MetadataAdmission owner, string id, Source source) : IDisposable
    {
        private MetadataAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(id, source);
    }
}
