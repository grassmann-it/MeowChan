namespace oChan.Downloader;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using oChan.Interfaces;
using oChan.Notifications;
using oChan.Storage;
using Serilog;

public enum DownloadPriority { Manual, BoardDiscovery }

/// <summary>
/// Owns the session's media jobs and shared worker limit. Thread owners explicitly
/// cancel their jobs when their watch ends; the queue does not subscribe to events.
/// </summary>
public class DownloadQueue
{
    private readonly object _stateLock = new object();
    private readonly LinkedList<QueuedDownload> _pendingDownloads = new LinkedList<QueuedDownload>();
    private readonly ConditionalWeakTable<IThread, PriorityState> _priorities = new ConditionalWeakTable<IThread, PriorityState>();
    private readonly LinkedList<AdmissionWaiter> _manualWaiters = new LinkedList<AdmissionWaiter>();
    private readonly LinkedList<AdmissionWaiter> _boardWaiters = new LinkedList<AdmissionWaiter>();
    private TaskCompletionSource _capacityChanged = CreateCompletionSource();
    private readonly int _maxPendingDownloads;
    private int _reservedSuccessors;
    private bool _preferManualAdmission = true;
    private bool _preferManualDispatch = true;
    private long _dispatchSequence;
    private readonly Dictionary<JobIdentity, QueuedDownload> _jobs = new Dictionary<JobIdentity, QueuedDownload>();
    private readonly Dictionary<ThreadIdentity, ThreadState> _threads = new Dictionary<ThreadIdentity, ThreadState>();
    private readonly ConditionalWeakTable<IThread, object> _cancelledThreads = new ConditionalWeakTable<IThread, object>();
    private readonly BandwidthLimiter _bandwidthLimiter;
    private readonly MediaStore _mediaStore;
    private readonly TimeSpan _bodyIdleTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CoalescedEvent<Action> _changes;
    private TaskCompletionSource _idleCompletion = CreateCompletedSource();
    private TaskCompletionSource? _stopCompletion;
    private int _maxParallelDownloads;
    private int _activeDownloads;
    private bool _stopping;

    /// <summary>Coalesced asynchronous invalidation; read a fresh snapshot when notified.</summary>
    public event Action? StateChanged
    {
        add => _changes.Add(value);
        remove => _changes.Remove(value);
    }

    public int MaxParallelDownloads
    {
        get
        {
            lock (_stateLock)
            {
                return _maxParallelDownloads;
            }
        }
    }

    public long MaxBandwidthBytesPerSecond => _bandwidthLimiter.MaxBytesPerSecond;

    /// <summary>
    /// Copies counts for the requested watch instances under one queue lock.
    /// Active includes jobs holding a worker slot, including network admission or storage work.
    /// A replacement watch with the same URL never receives its predecessor's counts.
    /// </summary>
    public IReadOnlyDictionary<IThread, DownloadCounts> GetThreadCounts(IReadOnlyList<IThread> threads)
    {
        ArgumentNullException.ThrowIfNull(threads);
        Dictionary<IThread, DownloadCounts> counts = new Dictionary<IThread, DownloadCounts>(ReferenceEqualityComparer.Instance);
        foreach (IThread thread in threads)
        {
            ArgumentNullException.ThrowIfNull(thread);
            counts.TryAdd(thread, new DownloadCounts(0, 0));
        }

        lock (_stateLock)
        {
            foreach (QueuedDownload job in _jobs.Values)
            {
                IThread thread = job.Item.Thread;
                if (counts.TryGetValue(thread, out DownloadCounts previous))
                {
                    counts[thread] = job.IsActive
                        ? previous with { Active = previous.Active + 1 }
                        : previous with { Queued = previous.Queued + 1 };
                    if (job.Replacement != null)
                        counts[thread] = counts[thread] with { Queued = counts[thread].Queued + 1 };
                }
            }
        }

        return new ReadOnlyDictionary<IThread, DownloadCounts>(counts);
    }

    public DownloadQueue(int maxParallelDownloads, long maxBandwidthBytesPerSecond, MediaStore? mediaStore = null,
        TimeSpan? bodyIdleTimeout = null, TimeProvider? timeProvider = null, ILogger? logger = null)
        : this(maxParallelDownloads, maxBandwidthBytesPerSecond, 256, mediaStore, bodyIdleTimeout, timeProvider, logger) { }

    /// <summary>Configures bounded pending admission while retaining the original constructor signature.</summary>
    public DownloadQueue(int maxParallelDownloads, long maxBandwidthBytesPerSecond, int maxPendingDownloads,
        MediaStore? mediaStore = null, TimeSpan? bodyIdleTimeout = null, TimeProvider? timeProvider = null, ILogger? logger = null)
    {
        if (maxParallelDownloads <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxParallelDownloads), "Max parallel downloads must be greater than zero.");
        }

        if (maxBandwidthBytesPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBandwidthBytesPerSecond), "Max bandwidth must be greater than zero.");
        }

        if (maxPendingDownloads <= 0) throw new ArgumentOutOfRangeException(nameof(maxPendingDownloads));
        _maxPendingDownloads = maxPendingDownloads;
        _bodyIdleTimeout = bodyIdleTimeout ?? TimeSpan.FromSeconds(30);
        if (_bodyIdleTimeout <= TimeSpan.Zero || _bodyIdleTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(bodyIdleTimeout));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? Serilog.Core.Logger.None;
        _changes = new CoalescedEvent<Action>(handler => handler(),
            exception => _logger.Error(exception, "Download queue observer failed"));
        _bandwidthLimiter = new BandwidthLimiter(maxBandwidthBytesPerSecond, _timeProvider, _logger);
        _mediaStore = mediaStore ?? new MediaStore();
        _maxParallelDownloads = maxParallelDownloads;
    }

    /// <summary>
    /// Diagnostic lookup including active jobs. Admission uses the complete job
    /// identity because one URL may have several legitimate destinations.
    /// </summary>
    public bool IsInQueue(string url)
    {
        lock (_stateLock)
        {
            foreach (QueuedDownload job in _jobs.Values)
            {
                if (string.Equals(job.Url, url, StringComparison.Ordinal)
                    || string.Equals(job.Replacement?.DownloadUri.ToString(), url, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool IsDownloading(string url)
    {
        lock (_stateLock)
        {
            foreach (QueuedDownload job in _jobs.Values)
            {
                if (job.IsActive && string.Equals(job.Url, url, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void EnqueueDownload(DownloadItem item)
    {
        PreparedDownload prepared = Prepare(item);
        TryEnqueuePrepared(item, prepared, out bool full, fromWaiter: false);
        if (full) throw new InvalidOperationException("Download capacity is full; use EnqueueAsync to wait without losing work.");
    }

    public int MaxPendingDownloads => _maxPendingDownloads;

    /// <summary>Changes scheduling priority without changing HTTP policy or job identity.</summary>
    public void SetThreadPriority(IThread thread, DownloadPriority priority)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_stateLock) { _priorities.GetValue(thread, static _ => new PriorityState()).Priority = priority; }
    }

    /// <summary>Pending includes reserved successors of active media revisions, never exceeding the configured cap.</summary>
    public (int Active, int Pending, int WaitingAdmission) GetCapacitySnapshot()
    {
        lock (_stateLock) return (_activeDownloads, _pendingDownloads.Count + _reservedSuccessors,
            _manualWaiters.Count + _boardWaiters.Count);
    }

    /// <summary>
    /// Atomically reserves a job. Returns false for a duplicate, a stopped queue,
    /// a cancelled instance, unavailable capacity or a still-active older instance of the same thread.
    /// Use EnqueueAsync to wait for capacity instead of retrying or discarding work.
    /// Completed reservations are released and can be submitted again.
    /// </summary>
    public bool TryEnqueue(DownloadItem item)
    {
        PreparedDownload prepared = Prepare(item);
        return TryEnqueuePrepared(item, prepared, out _, fromWaiter: false);
    }

    /// <summary>
    /// Waits for fair admission when capacity is full. False means duplicate or closed ownership,
    /// not a discarded job due to backpressure. Caller cancellation ends only this admission wait.
    /// </summary>
    public async Task<bool> EnqueueAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        PreparedDownload prepared = Prepare(item);
        AdmissionWaiter waiter;
        LinkedList<AdmissionWaiter> waiters;
        LinkedListNode<AdmissionWaiter> node;
        lock (_stateLock)
        {
            waiter = new AdmissionWaiter(item.Thread, GetPriorityLocked(item.Thread));
            waiters = waiter.Priority == DownloadPriority.Manual ? _manualWaiters : _boardWaiters;
            node = waiters.AddLast(waiter);
            PulseCapacityLocked();
        }
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                lock (_stateLock)
                {
                    if (_stopping || _cancelledThreads.TryGetValue(item.Thread, out _)) return false;
                    if (ReferenceEquals(NextWaiterLocked(), waiter))
                    {
                        bool accepted = TryEnqueuePrepared(item, prepared, out bool full, fromWaiter: true);
                        if (!full)
                        {
                            _preferManualAdmission = waiter.Priority != DownloadPriority.Manual;
                            return accepted;
                        }
                    }
                    changed = _capacityChanged.Task;
                }
                await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_stateLock) { waiters.Remove(node); PulseCapacityLocked(); }
        }
    }

    private static PreparedDownload Prepare(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThreadIdentity threadIdentity = GetThreadIdentity(item.Thread);
        string destinationPath = Path.GetFullPath(item.DestinationPath);
        if (OperatingSystem.IsWindows()) destinationPath = destinationPath.ToUpperInvariant();
        return new PreparedDownload(threadIdentity, new JobIdentity(threadIdentity, item.MediaIdentifier, destinationPath));
    }

    private bool TryEnqueuePrepared(DownloadItem item, PreparedDownload prepared, out bool full, bool fromWaiter)
    {
        full = false;
        ThreadIdentity threadIdentity = prepared.Thread;
        JobIdentity jobIdentity = prepared.Job;
        string url = item.DownloadUri.ToString();
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            if (_stopping || _cancelledThreads.TryGetValue(item.Thread, out _))
            {
                return false;
            }

            if (_jobs.TryGetValue(jobIdentity, out QueuedDownload? existing))
            {
                if (!ReferenceEquals(existing.Item.Thread, item.Thread)) return false;
                if (SameRetrieval(existing.Replacement ?? existing.Item, item)) return false;
                // Keep the active bytes owned by their original worker. The latest observed revision
                // runs after it even if this was the source's final poll. At most one successor exists.
                if (existing.IsActive)
                {
                    bool needsSuccessor = !SameRetrieval(existing.Item, item);
                    if (needsSuccessor && existing.Replacement == null)
                    {
                        if (_pendingDownloads.Count + _reservedSuccessors >= _maxPendingDownloads)
                        { full = true; return false; }
                        _reservedSuccessors++;
                    }
                    else if (!needsSuccessor && existing.Replacement != null)
                    { _reservedSuccessors--; PulseCapacityLocked(); }
                    existing.Replacement = needsSuccessor ? item : null;
                }
                else existing.Item = item;
                ready = new List<QueuedDownload>();
            }
            else
            {

                if ((!fromWaiter && (_manualWaiters.Count + _boardWaiters.Count > 0))
                    || (_activeDownloads >= _maxParallelDownloads && _pendingDownloads.Count + _reservedSuccessors >= _maxPendingDownloads))
                { full = true; return false; }

                if (_threads.TryGetValue(threadIdentity, out ThreadState? threadState))
                {
                    if (!ReferenceEquals(threadState.Thread, item.Thread))
                    {
                        return false;
                    }
                }
                else
                {
                    threadState = new ThreadState(threadIdentity, item.Thread);
                    _threads.Add(threadIdentity, threadState);
                }

                if (_jobs.Count == 0)
                {
                    _idleCompletion = CreateCompletionSource();
                }

                QueuedDownload job = new QueuedDownload(jobIdentity, url, item, threadState);
                _jobs.Add(jobIdentity, job);
                _pendingDownloads.AddLast(job);
                threadState.OutstandingJobs++;
                ready = ReserveWorkersLocked();
            }
        }

        StartWorkers(ready);
        _changes.Signal();
        return true;
    }

    /// <summary>
    /// Waits for the current busy period to end without closing admission. A later
    /// submission can start another busy period after this task completes.
    /// </summary>
    public Task WaitForIdleAsync()
    {
        lock (_stateLock)
        {
            return _idleCompletion.Task;
        }
    }

    public void CancelDownloadsForThread(IThread thread)
    {
        _ = CancelDownloadsForThreadAsync(thread);
    }

    /// <summary>
    /// Closes admission for this instance, removes waiting jobs and waits for its
    /// active workers. New instances of its identity may start after those finish.
    /// </summary>
    public Task CancelDownloadsForThreadAsync(IThread thread)
    {
        ArgumentNullException.ThrowIfNull(thread);
        ThreadIdentity identity = GetThreadIdentity(thread);
        Cleanup cleanup = new Cleanup();
        ThreadState? threadState;
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            _cancelledThreads.GetValue(thread, static (IThread _) => new object());
            if (!_threads.TryGetValue(identity, out threadState) || !ReferenceEquals(threadState.Thread, thread))
            {
                PulseCapacityLocked();
                return Task.CompletedTask;
            }

            // Keep the CTS alive while cancellation callbacks execute outside the
            // lock, even if the final worker finishes at the same time.
            threadState.CancellationLeases++;
            RemovePendingJobsLocked(threadState);
            RetireThreadIfIdleLocked(threadState, cleanup);
            SignalIdleIfNeededLocked(cleanup);
            ready = ReserveWorkersLocked();
        }

        RunCleanup(cleanup);
        Task cancellation = CancelThreadStateAsync(threadState);
        StartWorkers(ready);
        _changes.Signal();
        return cancellation;
    }

    public void StopAll()
    {
        _ = StopAllAsync();
    }

    /// <summary>
    /// Permanently closes admission, cancels jobs and waits for all workers.
    /// Repeated calls return the same stop operation.
    /// </summary>
    public Task StopAllAsync()
    {
        Cleanup cleanup = new Cleanup();
        List<ThreadState> threads;
        TaskCompletionSource stopCompletion;
        lock (_stateLock)
        {
            if (_stopCompletion != null)
            {
                return _stopCompletion.Task;
            }

            _stopping = true;
            stopCompletion = CreateCompletionSource();
            _stopCompletion = stopCompletion;
            threads = new List<ThreadState>(_threads.Values);
            foreach (ThreadState threadState in threads)
            {
                threadState.CancellationLeases++;
            }

            while (_pendingDownloads.First is LinkedListNode<QueuedDownload> node)
            {
                QueuedDownload job = node.Value;
                _pendingDownloads.RemoveFirst();
                _jobs.Remove(job.Identity);
                job.ThreadState.OutstandingJobs--;
            }

            foreach (ThreadState threadState in threads)
            {
                RetireThreadIfIdleLocked(threadState, cleanup);
            }

            SignalIdleIfNeededLocked(cleanup);
            PulseCapacityLocked();
        }

        RunCleanup(cleanup);
        _changes.Signal();
        _ = CompleteStopAsync(threads, stopCompletion);
        return stopCompletion.Task;
    }

    public void UpdateMaxParallelDownloads(int newMax)
    {
        if (newMax <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(newMax), "Max parallel downloads must be greater than zero.");
        }

        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            _maxParallelDownloads = newMax;
            ready = ReserveWorkersLocked();
        }

        StartWorkers(ready);
        _changes.Signal();
    }

    public void UpdateMaxBandwidth(long newMaxBytesPerSecond)
    {
        _bandwidthLimiter.UpdateMaxBytesPerSecond(newMaxBytesPerSecond);
    }

    private DownloadPriority GetPriorityLocked(IThread thread) =>
        _priorities.TryGetValue(thread, out PriorityState? state) ? state.Priority : DownloadPriority.Manual;

    private AdmissionWaiter? NextWaiterLocked() => _preferManualAdmission
        ? _manualWaiters.First?.Value ?? _boardWaiters.First?.Value
        : _boardWaiters.First?.Value ?? _manualWaiters.First?.Value;

    private void PulseCapacityLocked()
    {
        TaskCompletionSource previous = _capacityChanged;
        _capacityChanged = CreateCompletionSource();
        previous.TrySetResult();
    }

    private List<QueuedDownload> ReserveWorkersLocked()
    {
        List<QueuedDownload> ready = new List<QueuedDownload>();
        while (!_stopping && _activeDownloads < _maxParallelDownloads && _pendingDownloads.Count > 0)
        {
            DownloadPriority preferred = _preferManualDispatch ? DownloadPriority.Manual : DownloadPriority.BoardDiscovery;
            LinkedListNode<QueuedDownload>? chosen = null;
            for (LinkedListNode<QueuedDownload>? node = _pendingDownloads.First; node != null; node = node.Next)
            {
                bool matches = GetPriorityLocked(node.Value.Item.Thread) == preferred;
                bool chosenMatches = chosen != null && GetPriorityLocked(chosen.Value.Item.Thread) == preferred;
                if (chosen == null || (matches && !chosenMatches)
                    || (matches == chosenMatches && node.Value.ThreadState.LastDispatch < chosen.Value.ThreadState.LastDispatch))
                    chosen = node;
            }
            QueuedDownload job = chosen!.Value;
            _pendingDownloads.Remove(chosen);
            _preferManualDispatch = GetPriorityLocked(job.Item.Thread) != DownloadPriority.Manual;
            job.ThreadState.LastDispatch = ++_dispatchSequence;
            job.IsActive = true;
            _activeDownloads++;
            ready.Add(job);
        }
        PulseCapacityLocked();
        return ready;
    }

    private void StartWorkers(List<QueuedDownload> ready)
    {
        foreach (QueuedDownload job in ready)
        {
            _ = Task.Run(() => ProcessDownloadItemAsync(job));
        }
    }

    private async Task ProcessDownloadItemAsync(QueuedDownload job)
    {
        try
        {
            job.ThreadState.Token.ThrowIfCancellationRequested();
            DownloadWorker worker = new DownloadWorker(job.Item, _bandwidthLimiter, _mediaStore, _bodyIdleTimeout, _timeProvider, _logger);
            await worker.ExecuteAsync(job.ThreadState.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (job.ThreadState.Token.IsCancellationRequested)
        {
            _logger.Debug("Download for {DownloadUri} was cancelled.", job.Item.DownloadUri);
        }
        catch (Exception exception)
        {
            try
            {
                job.Item.Thread.ReportMediaFailure(job.Item.MediaIdentifier, exception, job.Item.DestinationPath);
            }
            catch (Exception notificationException)
            {
                _logger.Error(notificationException, "Could not report media failure for {MediaId}", job.Item.MediaIdentifier);
            }
            _logger.Error(exception, "Error downloading {DownloadUri}", job.Item.DownloadUri);
        }
        finally
        {
            Cleanup cleanup = new Cleanup();
            List<QueuedDownload> ready;
            lock (_stateLock)
            {
                _activeDownloads--;
                _jobs.Remove(job.Identity);
                job.ThreadState.OutstandingJobs--;
                if (job.Replacement != null) _reservedSuccessors--;
                if (job.Replacement is DownloadItem replacement && !_stopping && !job.ThreadState.Token.IsCancellationRequested
                    && !_cancelledThreads.TryGetValue(replacement.Thread, out _))
                {
                    QueuedDownload successor = new QueuedDownload(job.Identity, replacement.DownloadUri.ToString(), replacement, job.ThreadState);
                    _jobs.Add(job.Identity, successor);
                    _pendingDownloads.AddLast(successor);
                    job.ThreadState.OutstandingJobs++;
                }
                RetireThreadIfIdleLocked(job.ThreadState, cleanup);
                SignalIdleIfNeededLocked(cleanup);
                ready = ReserveWorkersLocked();
            }

            RunCleanup(cleanup);
            StartWorkers(ready);
            _changes.Signal();
        }
    }

    private void RemovePendingJobsLocked(ThreadState threadState)
    {
        LinkedListNode<QueuedDownload>? node = _pendingDownloads.First;
        while (node != null)
        {
            LinkedListNode<QueuedDownload>? next = node.Next;
            QueuedDownload job = node.Value;
            if (ReferenceEquals(job.ThreadState, threadState))
            {
                _pendingDownloads.Remove(node);
                _jobs.Remove(job.Identity);
                threadState.OutstandingJobs--;
            }
            node = next;
        }
        PulseCapacityLocked();
    }

    private async Task CancelThreadStateAsync(ThreadState threadState)
    {
        try
        {
            try
            {
                await threadState.Cancellation.CancelAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.Error(exception, "Error in cancellation callback for thread {ThreadId}", threadState.Identity.ThreadId);
            }

            await threadState.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            Cleanup cleanup = new Cleanup();
            lock (_stateLock)
            {
                threadState.CancellationLeases--;
                ReleaseCancellationIfUnusedLocked(threadState, cleanup);
            }

            RunCleanup(cleanup);
        }
    }

    private async Task CompleteStopAsync(List<ThreadState> threads, TaskCompletionSource completion)
    {
        try
        {
            List<Task> cancellations = new List<Task>(threads.Count);
            foreach (ThreadState threadState in threads)
            {
                cancellations.Add(CancelThreadStateAsync(threadState));
            }

            await Task.WhenAll(cancellations).ConfigureAwait(false);
            await WaitForIdleAsync().ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void RetireThreadIfIdleLocked(ThreadState threadState, Cleanup cleanup)
    {
        if (threadState.OutstandingJobs != 0 || threadState.IsRetired)
        {
            return;
        }

        threadState.IsRetired = true;
        _threads.Remove(threadState.Identity);
        cleanup.Completions.Add(threadState.Completion);
        ReleaseCancellationIfUnusedLocked(threadState, cleanup);
    }

    private static void ReleaseCancellationIfUnusedLocked(ThreadState threadState, Cleanup cleanup)
    {
        if (threadState.OutstandingJobs == 0 && threadState.CancellationLeases == 0 && !threadState.IsDisposed)
        {
            threadState.IsDisposed = true;
            cleanup.Cancellations.Add(threadState.Cancellation);
        }
    }

    private void SignalIdleIfNeededLocked(Cleanup cleanup)
    {
        if (_jobs.Count == 0)
        {
            cleanup.Completions.Add(_idleCompletion);
        }
    }

    private static void RunCleanup(Cleanup cleanup)
    {
        foreach (CancellationTokenSource cancellation in cleanup.Cancellations)
        {
            cancellation.Dispose();
        }

        foreach (TaskCompletionSource completion in cleanup.Completions)
        {
            completion.TrySetResult();
        }
    }

    private static ThreadIdentity GetThreadIdentity(IThread thread)
    {
        return new ThreadIdentity(thread.Board.ImageBoard.Name, thread.Board.BoardCode, thread.ThreadId);
    }

    private static bool SameRetrieval(DownloadItem first, DownloadItem second) =>
        first.ExpectedSize == second.ExpectedSize
        && string.Equals(first.ExpectedMd5, second.ExpectedMd5, StringComparison.Ordinal);

    private static TaskCompletionSource CreateCompletionSource()
    {
        return new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static TaskCompletionSource CreateCompletedSource()
    {
        TaskCompletionSource completion = CreateCompletionSource();
        completion.SetResult();
        return completion;
    }

    private sealed class PriorityState { public DownloadPriority Priority { get; set; } }
    private sealed record AdmissionWaiter(IThread Thread, DownloadPriority Priority);
    private readonly record struct PreparedDownload(ThreadIdentity Thread, JobIdentity Job);

    private readonly record struct ThreadIdentity(string Source, string Board, string ThreadId);
    private readonly record struct JobIdentity(ThreadIdentity Thread, string MediaId, string DestinationPath);

    private sealed class QueuedDownload
    {
        public JobIdentity Identity { get; }
        public string Url => Item.DownloadUri.ToString();
        public DownloadItem Item { get; set; }
        public DownloadItem? Replacement { get; set; }
        public ThreadState ThreadState { get; }
        public bool IsActive { get; set; }

        public QueuedDownload(JobIdentity identity, string url, DownloadItem item, ThreadState threadState)
        {
            Identity = identity;
            Item = item;
            ThreadState = threadState;
        }
    }

    private sealed class ThreadState
    {
        public ThreadIdentity Identity { get; }
        public IThread Thread { get; }
        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();
        public CancellationToken Token { get; }
        public TaskCompletionSource Completion { get; } = CreateCompletionSource();
        public long LastDispatch { get; set; }
        public int OutstandingJobs { get; set; }
        public int CancellationLeases { get; set; }
        public bool IsRetired { get; set; }
        public bool IsDisposed { get; set; }

        public ThreadState(ThreadIdentity identity, IThread thread)
        {
            Identity = identity;
            Thread = thread;
            Token = Cancellation.Token;
        }
    }

    private sealed class Cleanup
    {
        public List<CancellationTokenSource> Cancellations { get; } = new List<CancellationTokenSource>();
        public List<TaskCompletionSource> Completions { get; } = new List<TaskCompletionSource>();
    }
}
