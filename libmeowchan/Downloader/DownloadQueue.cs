namespace MeowChan.Downloader;

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Interfaces;
using MeowChan.Notifications;
using MeowChan.Storage;
using MeowChan.Persistence;
using System.Net;
using System.Net.Http;
using Serilog;

public enum DownloadPriority { Manual, BoardDiscovery }

/// <summary>Identifies the queue's closed source gate without classifying unrelated provider cancellation as shutdown.</summary>
internal sealed class QueueStoppingException(CancellationToken cancellationToken)
    : OperationCanceledException("The queue is stopping.", cancellationToken);


/// <summary>
/// Owns the session's media jobs and shared worker limit. Thread owners explicitly
/// cancel their jobs when their watch ends; the queue does not subscribe to events.
/// </summary>
public class DownloadQueue
{
    private readonly object _stateLock = new object();
    private readonly MetadataAdmission _metadataAdmission = new();
    private readonly SemaphoreSlim _pauseTransitions = new(1, 1);
    private CancellationTokenSource _transferEpoch = new();
    private TaskCompletionSource _workersDrained = CreateCompletedSource();
    // Interrupted reservations retain admission ownership without occupying bounded pending capacity.
    private readonly LinkedList<QueuedDownload> _interruptedDownloads = new();
    private readonly LinkedList<QueuedDownload> _pendingDownloads = new LinkedList<QueuedDownload>();
    private readonly ConditionalWeakTable<IThread, PriorityState> _priorities = new ConditionalWeakTable<IThread, PriorityState>();
    private readonly LinkedList<AdmissionWaiter> _admissionWaiters = new LinkedList<AdmissionWaiter>();
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
    private readonly Dictionary<IThread, WorkResource> _workOwners = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<JobIdentity, ScheduledWork> _scheduled = new();
    private readonly HashSet<JobIdentity> _quarantined = new();
    private IWorkStateStore? _workStore;
    private TaskCompletionSource _scheduleChanged = CreateCompletionSource();
    private Task? _scheduleTask;
    private TaskCompletionSource _idleCompletion = CreateCompletedSource();
    private TaskCompletionSource? _stopCompletion;
    private int _maxParallelDownloads;
    private int _activeDownloads;
    private long _receivedMediaBytes;
    private bool _stopping;
    private bool _paused;
    private TaskCompletionSource _resumed = CreateCompletedSource();

    /// <summary>Coalesced asynchronous invalidation; read a fresh snapshot when notified.</summary>
    public event Action? StateChanged
    {
        add => _changes.Add(value);
        remove => _changes.Remove(value);
    }
    public event Action<string>? PersistenceFailed;

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

    /// <summary>True while dispatch and provider checks are halted.</summary>
    public bool IsPaused { get { lock (_stateLock) { return _paused; } } }

    public void SetPaused(bool paused) => SetPausedAsync(paused).GetAwaiter().GetResult();

    /// <summary>Cancels transfer reservations and waits for their physical workers before confirming pause.</summary>
    public async Task SetPausedAsync(bool paused)
    {
        await _pauseTransitions.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource? cancel = null;
            Task drained = Task.CompletedTask;
            List<QueuedDownload> ready;
            lock (_stateLock)
            {
                if (_stopping) throw new InvalidOperationException("The queue is stopping.");
                if (_paused == paused) return;
                _paused = paused;
                if (paused)
                {
                    _resumed = CreateCompletionSource();
                    cancel = _transferEpoch;
                    drained = _workersDrained.Task;
                }
                else
                {
                    _transferEpoch = new CancellationTokenSource();
                    _resumed.TrySetResult();
                }
                ready = ReserveWorkersLocked();
            }
            StartWorkers(ready);
            _changes.Signal();
            if (cancel != null)
            {
                try { await cancel.CancelAsync().ConfigureAwait(false); }
                finally { await drained.ConfigureAwait(false); cancel.Dispose(); }
            }
        }
        finally { _pauseTransitions.Release(); }
    }

    /// <summary>Shared gate for automatic provider checks. Cancellation still ends a paused watch.</summary>
    public async Task WaitUntilResumedAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        while (true)
        {
            Task resumed;
            lock (_stateLock)
            {
                if (_stopping) throw new QueueStoppingException(cancellationToken);
                if (!_paused) return;
                resumed = _resumed.Task;
            }
            await resumed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Acquires one of four session metadata slots, at most one per source. Release before cooldown waits.</summary>
    public async Task<IDisposable> AcquireMetadataAsync(string sourceId, DownloadPriority priority,
        CancellationToken cancellationToken = default)
    {
        await WaitUntilResumedAsync(cancellationToken).ConfigureAwait(false);
        return await _metadataAdmission.AcquireAsync(sourceId, priority, cancellationToken).ConfigureAwait(false);
    }

    public (int Active, int Waiting) GetMetadataCapacitySnapshot() => _metadataAdmission.Snapshot;
    internal DownloadPriority GetThreadPriority(IThread thread) { lock (_stateLock) return GetPriorityLocked(thread); }

    public long MaxBandwidthBytesPerSecond => _bandwidthLimiter.MaxBytesPerSecond;
    /// <summary>Cumulative media response-body bytes read by this queue, including failed attempts; excludes local restoration.</summary>
    public long ReceivedMediaBytes => Interlocked.Read(ref _receivedMediaBytes);
    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();
    internal TimeProvider Clock => _timeProvider;

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
        if (!PrepareDurableAdmission(item, prepared)) return;
        TryEnqueuePrepared(item, prepared, out bool full, fromWaiter: false);
        if (full) throw new InvalidOperationException("Download capacity is full; use EnqueueAsync to wait without losing work.");
    }

    public int MaxPendingDownloads => _maxPendingDownloads;

    /// <summary>Explicit durable mode. A library caller that does not configure it remains volatile.</summary>
    public void ConfigureWorkState(IWorkStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_stateLock)
        {
            if (_workStore != null && !ReferenceEquals(_workStore, store)) throw new InvalidOperationException("The work store is already configured.");
            _workStore = store;
        }
    }

    /// <summary>Restores work before a provider lifetime starts; no endpoint request is required.</summary>
    public void BindWork(IThread thread, WorkResource resource)
    {
        ArgumentNullException.ThrowIfNull(thread);
        IWorkStateStore store = _workStore ?? throw new InvalidOperationException("Configure durable work before binding watches.");
        IReadOnlyList<MediaWork> saved = store.LoadMedia(resource.Url, resource.Generation);
        lock (_stateLock)
        {
            _workOwners[thread] = resource;
            foreach (MediaWork media in saved)
            {
                if (media.Status is MediaWorkStatus.Succeeded or MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired) continue;
                DownloadItem item = RestoreItem(thread, media);
                PreparedDownload prepared = Prepare(item);
                _scheduled[prepared.Job] = new ScheduledWork(item, media);
            }
            SignalScheduleLocked();
        }
        if (thread is Boards.Base.BaseThread baseThread) baseThread.RestoreDurableMedia(saved);
    }

    public int RequestMediaRetry(IThread thread, string? mediaIdentifier = null)
    {
        ArgumentNullException.ThrowIfNull(thread);
        IWorkStateStore? store = _workStore;
        if (store == null) return 0;
        WorkResource owner;
        lock (_stateLock)
        {
            if (!_workOwners.TryGetValue(thread, out WorkResource? found) || _stopping) return 0;
            owner = found;
        }
        int count = 0;
        foreach (MediaWork media in store.LoadMedia(owner.Url, owner.Generation))
        {
            if (mediaIdentifier != null && media.MediaIdentifier != mediaIdentifier) continue;
            if (media.Status == MediaWorkStatus.Succeeded || media.ManualAttempt) continue;
            DownloadItem item = RestoreItem(thread, media);
            PreparedDownload prepared = Prepare(item);
            MediaWork manual = media with { ManualAttempt = true, DueAt = _timeProvider.GetUtcNow(), Status = MediaWorkStatus.Ready };
            lock (_stateLock)
            {
                MediaWork? current = store.LoadMedia(owner.Url, owner.Generation).FirstOrDefault(work =>
                    work.MediaIdentifier == media.MediaIdentifier && work.DestinationPath == media.DestinationPath);
                if (current == null || current.ManualAttempt || current.Status == MediaWorkStatus.Succeeded
                    || _jobs.ContainsKey(prepared.Job)) continue;
                manual = current with { ManualAttempt = true, DueAt = _timeProvider.GetUtcNow(), Status = MediaWorkStatus.Ready };
                if (!store.CommitMedia(manual)) continue;
                _quarantined.Remove(prepared.Job);
                _scheduled[prepared.Job] = new ScheduledWork(item, manual);
                SignalScheduleLocked();
            }
            count++;
        }
        if (count > 0) _changes.Signal();
        return count;
    }

    private static DownloadItem RestoreItem(IThread thread, MediaWork media) => new(
        new Uri(media.DownloadUrl), media.DestinationPath, thread.Board.ImageBoard, thread, media.MediaIdentifier,
        media.ExpectedSize, media.ExpectedMd5, media.OriginalFileName, media.LegacyPath);

    private void SignalScheduleLocked()
    {
        TaskCompletionSource old = _scheduleChanged;
        _scheduleChanged = CreateCompletionSource();
        old.TrySetResult();
        if (_scheduleTask == null && !_stopping) _scheduleTask = Task.Run(RunScheduleAsync);
    }

    private async Task RunScheduleAsync()
    {
        while (true)
        {
            ScheduledWork? due = null;
            Task changed;
            TimeSpan? delay = null;
            List<ScheduledWork> expired = new();
            lock (_stateLock)
            {
                if (_stopping) return;
                DateTimeOffset now = _timeProvider.GetUtcNow();
                List<JobIdentity> expiredKeys = new();
                foreach (KeyValuePair<JobIdentity, ScheduledWork> entry in _scheduled)
                {
                    ScheduledWork candidate = entry.Value;
                    if (_quarantined.Contains(entry.Key)) continue;
                    if (_cancelledThreads.TryGetValue(candidate.Item.Thread, out _) || !_workOwners.TryGetValue(candidate.Item.Thread, out WorkResource? owner)
                        || owner.Generation != candidate.Work.Generation) continue;
                    DateTimeOffset at = candidate.Work.DueAt ?? now;
                    if (!candidate.Work.ManualAttempt && owner.EndObservedAt is DateTimeOffset end && candidate.Work.HttpStatus == 404)
                    {
                        DateTimeOffset deadline = end.AddHours(12);
                        if (now >= deadline) { expiredKeys.Add(entry.Key); expired.Add(candidate); continue; }
                        if (at >= deadline)
                        {
                            TimeSpan untilDeadline = deadline - now;
                            if (delay == null || untilDeadline < delay) delay = untilDeadline;
                            continue;
                        }
                    }
                    if (at <= now && due == null) due = candidate;
                    else if (at > now && (delay == null || at - now < delay)) delay = at - now;
                }
                foreach (JobIdentity key in expiredKeys) _scheduled.Remove(key);
                if (due != null) _scheduled.Remove(Prepare(due.Item).Job);
                changed = _scheduleChanged.Task;
            }
            foreach (ScheduledWork item in expired)
            {
                try
                {
                    lock (_stateLock)
                    {
                        // A manual request or new revision may have replaced this wait after the scan.
                        MediaWork? current = _workStore?.LoadMedia(item.Work.ResourceUrl, item.Work.Generation)
                            .FirstOrDefault(work => work.MediaIdentifier == item.Work.MediaIdentifier
                                && work.DestinationPath == item.Work.DestinationPath);
                        if (current == item.Work)
                            _workStore?.CommitMedia(item.Work with { Status = MediaWorkStatus.Expired, DueAt = null });
                    }
                }
                catch (Exception error) { Quarantine(item.Item, error); }
            }
            if (expired.Count > 0) _changes.Signal();
            if (due != null)
            {
                try
                {
                    Task<bool>? admission = null;
                    lock (_stateLock)
                    {
                        MediaWork? current = _workStore?.LoadMedia(due.Work.ResourceUrl, due.Work.Generation)
                            .FirstOrDefault(work => work.MediaIdentifier == due.Work.MediaIdentifier
                                && work.DestinationPath == due.Work.DestinationPath);
                        if (current == due.Work) admission = EnqueueAsync(due.Item);
                    }
                    if (admission != null) await admission.ConfigureAwait(false);
                }
                catch (Exception error) { Quarantine(due.Item, error); }
                continue;
            }
            if (delay == null) await changed.ConfigureAwait(false);
            else
            {
                using CancellationTokenSource timer = new();
                Task elapsed = Task.Delay(delay.Value, _timeProvider, timer.Token);
                if (await Task.WhenAny(changed, elapsed).ConfigureAwait(false) == changed) timer.Cancel();
            }
        }
    }

    /// <summary>Changes scheduling priority without changing HTTP policy or job identity.</summary>
    public void SetThreadPriority(IThread thread, DownloadPriority priority)
    {
        ArgumentNullException.ThrowIfNull(thread);
        if (!Enum.IsDefined(priority)) throw new ArgumentOutOfRangeException(nameof(priority));
        lock (_stateLock)
        {
            _priorities.GetValue(thread, static _ => new PriorityState()).Priority = priority;
            PulseCapacityLocked();
        }
    }

    /// <summary>
    /// Pending includes reserved successors of active media revisions, never exceeding the configured cap.
    /// Interrupted worker reservations remain admitted separately; GetThreadCounts includes them as queued.
    /// </summary>
    public (int Active, int Pending, int WaitingAdmission) GetCapacitySnapshot()
    {
        lock (_stateLock) return (_activeDownloads, _pendingDownloads.Count + _reservedSuccessors,
            _admissionWaiters.Count);
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
        if (!PrepareDurableAdmission(item, prepared)) return false;
        return TryEnqueuePrepared(item, prepared, out _, fromWaiter: false);
    }

    /// <summary>
    /// Waits for fair admission when capacity is full. False means duplicate or closed ownership,
    /// not a discarded job due to backpressure. Caller cancellation ends only this admission wait.
    /// </summary>
    public async Task<bool> EnqueueAsync(DownloadItem item, CancellationToken cancellationToken = default)
    {
        PreparedDownload prepared = Prepare(item);
        if (!PrepareDurableAdmission(item, prepared)) return false;
        AdmissionWaiter waiter;
        LinkedListNode<AdmissionWaiter> node;
        lock (_stateLock)
        {
            waiter = new AdmissionWaiter(item.Thread);
            node = _admissionWaiters.AddLast(waiter);
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
                            _preferManualAdmission = GetPriorityLocked(waiter.Thread) != DownloadPriority.Manual;
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
            lock (_stateLock) { _admissionWaiters.Remove(node); PulseCapacityLocked(); }
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

    private bool PrepareDurableAdmission(DownloadItem item, PreparedDownload prepared)
    {
        lock (_stateLock) return PrepareDurableAdmissionLocked(item, prepared);
    }

    private bool PrepareDurableAdmissionLocked(DownloadItem item, PreparedDownload prepared)
    {
        IWorkStateStore? store = _workStore;
        if (store == null) return true;
        WorkResource owner;
        lock (_stateLock)
        {
            if (_quarantined.Contains(prepared.Job)) return false;
            if (!_workOwners.TryGetValue(item.Thread, out WorkResource? found) || _cancelledThreads.TryGetValue(item.Thread, out _)) return false;
            owner = found;
        }
        MediaWork? previous = store.LoadMedia(owner.Url, owner.Generation)
            .FirstOrDefault(media => media.MediaIdentifier == item.MediaIdentifier && media.DestinationPath == item.DestinationPath);
        bool newRevision = previous != null && (previous.ExpectedSize != item.ExpectedSize
            || !string.Equals(previous.ExpectedMd5, item.ExpectedMd5, StringComparison.Ordinal));
        DateTimeOffset now = _timeProvider.GetUtcNow();
        MediaWork next = previous == null || newRevision ? new MediaWork
        {
            ResourceUrl = owner.Url, Generation = owner.Generation, MediaIdentifier = item.MediaIdentifier,
            DestinationPath = item.DestinationPath, DownloadUrl = item.DownloadUri.AbsoluteUri,
            ExpectedSize = item.ExpectedSize, ExpectedMd5 = item.ExpectedMd5,
            OriginalFileName = item.OriginalFileName, LegacyPath = item.LegacyPath,
            Status = MediaWorkStatus.Ready
        } : previous with
        {
            DownloadUrl = item.DownloadUri.AbsoluteUri,
            OriginalFileName = item.OriginalFileName,
            LegacyPath = item.LegacyPath
        };
        if (next.Status is MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired) return false;
        if (!next.ManualAttempt && owner.EndObservedAt is DateTimeOffset end && next.HttpStatus == 404 && now >= end.AddHours(12)) return false;
        if (!store.CommitMedia(next)) return false;
        if (next.DueAt is DateTimeOffset due && due > now)
        {
            lock (_stateLock)
            {
                if (!_scheduled.TryGetValue(prepared.Job, out ScheduledWork? scheduled)
                    || scheduled.Work != next || scheduled.Item.DownloadUri != item.DownloadUri)
                {
                    _scheduled[prepared.Job] = new ScheduledWork(item, next);
                    SignalScheduleLocked();
                }
            }
            return false;
        }
        lock (_stateLock) { _scheduled.Remove(prepared.Job); }
        return true;
    }

    public void UpdateWorkOwner(IThread thread, WorkResource resource)
    {
        lock (_stateLock)
        {
            if (_workOwners.ContainsKey(thread)) { _workOwners[thread] = resource; SignalScheduleLocked(); }
        }
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

                if ((!fromWaiter && (_admissionWaiters.Count > 0))
                    || ((_paused || _activeDownloads >= _maxParallelDownloads) && _pendingDownloads.Count + _reservedSuccessors >= _maxPendingDownloads))
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
                    // Join the current service round rather than starting at epoch zero: a stream
                // of new watches must not continually overtake an older waiting watch.
                threadState = new ThreadState(threadIdentity, item.Thread) { LastDispatch = _dispatchSequence - 1 };
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
            _workOwners.Remove(thread);
            foreach (JobIdentity key in _scheduled.Where(entry => ReferenceEquals(entry.Value.Item.Thread, thread))
                .Select(entry => entry.Key).ToArray()) _scheduled.Remove(key);
            if (_workStore != null) SignalScheduleLocked();
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
            _metadataAdmission.Close();
            _scheduleChanged.TrySetResult();
            _resumed.TrySetResult();
            stopCompletion = CreateCompletionSource();
            _stopCompletion = stopCompletion;
            threads = new List<ThreadState>(_threads.Values);
            foreach (ThreadState threadState in threads)
            {
                threadState.CancellationLeases++;
            }

            while (_interruptedDownloads.First is LinkedListNode<QueuedDownload> interrupted)
            {
                _interruptedDownloads.RemoveFirst();
                _jobs.Remove(interrupted.Value.Identity);
                interrupted.Value.ThreadState.OutstandingJobs--;
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

    private AdmissionWaiter? NextWaiterLocked()
    {
        DownloadPriority preferred = _preferManualAdmission ? DownloadPriority.Manual : DownloadPriority.BoardDiscovery;
        foreach (AdmissionWaiter waiter in _admissionWaiters)
            if (GetPriorityLocked(waiter.Thread) == preferred) return waiter;
        return _admissionWaiters.First?.Value;
    }

    private void PulseCapacityLocked()
    {
        TaskCompletionSource previous = _capacityChanged;
        _capacityChanged = CreateCompletionSource();
        previous.TrySetResult();
    }

    private List<QueuedDownload> ReserveWorkersLocked()
    {
        List<QueuedDownload> ready = new List<QueuedDownload>();
        while (!_stopping && !_paused && _activeDownloads < _maxParallelDownloads && (_pendingDownloads.Count > 0 || _interruptedDownloads.Count > 0))
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
            QueuedDownload job;
            if (_interruptedDownloads.First is { } interrupted)
            {
                job = interrupted.Value;
                _interruptedDownloads.RemoveFirst();
            }
            else
            {
                job = chosen!.Value;
                _pendingDownloads.Remove(chosen);
            }
            _preferManualDispatch = GetPriorityLocked(job.Item.Thread) != DownloadPriority.Manual;
            job.ThreadState.LastDispatch = ++_dispatchSequence;
            job.IsActive = true;
            job.EpochToken = _transferEpoch.Token;
            if (_activeDownloads == 0) _workersDrained = CreateCompletionSource();
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
        bool succeeded = false;
        bool interrupted = false;
        using AsyncCancellationLink cancellation = new(job.ThreadState.Token, job.EpochToken);
        try
        {
            job.EpochToken.ThrowIfCancellationRequested();
            cancellation.Token.ThrowIfCancellationRequested();
            if (!CanRunDurable(job.Item)) return;
            DownloadWorker worker = new DownloadWorker(job.Item, _bandwidthLimiter, _mediaStore, _bodyIdleTimeout, _timeProvider,
                _logger, notifyThread: _workStore == null)
            {
                BytesReceived = bytes => Interlocked.Add(ref _receivedMediaBytes, bytes)
            };
            await worker.ExecuteAsync(cancellation.Token).ConfigureAwait(false);
            succeeded = true;
        }
        catch (OperationCanceledException) when (job.ThreadState.Token.IsCancellationRequested || job.EpochToken.IsCancellationRequested)
        {
            interrupted = job.EpochToken.IsCancellationRequested;
            _logger.Debug("Download for {DownloadUri} was cancelled.", job.Item.DownloadUri);
        }
        catch (Exception exception)
        {
            bool current = _workStore == null;
            try { current |= RecordDurableResult(job.Item, exception); }
            catch (Exception persistenceFailure)
            {
                Quarantine(job.Item, persistenceFailure);
            }
            if (current) try
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
            if (succeeded)
            {
                try
                {
                    if (RecordDurableResult(job.Item, null))
                        job.Item.Thread.MarkMediaAsDownloaded(job.Item.MediaIdentifier, job.Item.DestinationPath);
                }
                catch (Exception persistenceFailure)
                {
                    Quarantine(job.Item, persistenceFailure);
                }
            }
            Cleanup cleanup = new Cleanup();
            List<QueuedDownload> ready;
            lock (_stateLock)
            {
                _activeDownloads--;
                if (_activeDownloads == 0) _workersDrained.TrySetResult();
                if (interrupted && !_stopping && !job.ThreadState.Token.IsCancellationRequested
                    && !_cancelledThreads.TryGetValue(job.Item.Thread, out _))
                {
                    job.IsActive = false;
                    if (job.Replacement != null)
                    {
                        job.Item = job.Replacement;
                        job.Replacement = null;
                        _reservedSuccessors--;
                    }
                    _interruptedDownloads.AddLast(job);
                    ready = ReserveWorkersLocked();
                }
                else
                {
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
            }

            RunCleanup(cleanup);
            StartWorkers(ready);
            _changes.Signal();
        }
    }

    private void Quarantine(DownloadItem item, Exception failure)
    {
        lock (_stateLock) { _quarantined.Add(Prepare(item).Job); _scheduled.Remove(Prepare(item).Job); }
        string warning = $"Media work for {item.MediaIdentifier} could not be committed: {failure.Message}. Automatic retries are suspended.";
        _logger.Error(failure, "{Warning}", warning);
        PersistenceFailed?.Invoke(warning);
        _changes.Signal();
    }

    private bool CanRunDurable(DownloadItem item)
    {
        lock (_stateLock) return CanRunDurableLocked(item);
    }

    private bool CanRunDurableLocked(DownloadItem item)
    {
        IWorkStateStore? store = _workStore;
        if (store == null) return true;
        WorkResource owner;
        lock (_stateLock)
        {
            if (_quarantined.Contains(Prepare(item).Job)) return false;
            if (!_workOwners.TryGetValue(item.Thread, out WorkResource? found)) return false;
            owner = found;
        }
        WorkResource? current = store.LoadResources().FirstOrDefault(resource => resource.Url == owner.Url);
        if (current == null || current.Generation != owner.Generation || current.Intent == WorkIntent.Suppressed) return false;
        MediaWork? media = store.LoadMedia(owner.Url, owner.Generation)
            .FirstOrDefault(work => work.MediaIdentifier == item.MediaIdentifier && work.DestinationPath == item.DestinationPath);
        if (media == null || media.ExpectedSize != item.ExpectedSize
            || !string.Equals(media.ExpectedMd5, item.ExpectedMd5, StringComparison.Ordinal)) return false;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!media.ManualAttempt && media.HttpStatus == 404 && current.EndObservedAt is DateTimeOffset end
            && now >= end.AddHours(12))
        {
            store.CommitMedia(media with { Status = MediaWorkStatus.Expired, DueAt = null });
            _changes.Signal();
            return false;
        }
        return media.Status is not (MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired)
            && (media.DueAt == null || media.DueAt <= now);
    }

    private bool RecordDurableResult(DownloadItem item, Exception? failure)
    {
        lock (_stateLock) return RecordDurableResultLocked(item, failure);
    }

    private bool RecordDurableResultLocked(DownloadItem item, Exception? failure)
    {
        IWorkStateStore? store = _workStore;
        if (store == null) return false;
        WorkResource owner;
        lock (_stateLock)
        {
            if (!_workOwners.TryGetValue(item.Thread, out WorkResource? found)) return false;
            owner = found;
        }
        MediaWork? previous = store.LoadMedia(owner.Url, owner.Generation)
            .FirstOrDefault(media => media.MediaIdentifier == item.MediaIdentifier && media.DestinationPath == item.DestinationPath);
        if (previous == null || previous.ExpectedSize != item.ExpectedSize
            || !string.Equals(previous.ExpectedMd5, item.ExpectedMd5, StringComparison.Ordinal)) return false;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        int attempt = previous.Attempts + 1;
        int? status = (failure as HttpRequestException)?.StatusCode is HttpStatusCode http ? (int)http : null;
        int missing = status == 404 ? previous.ConsecutiveNotFound + 1 : 0;
        MediaWork next;
        if (failure == null)
        {
            next = previous with { Status = MediaWorkStatus.Succeeded, Attempts = 0, ConsecutiveNotFound = 0,
                LastAttemptAt = now, DueAt = null, Failure = null, HttpStatus = null, ManualAttempt = false };
        }
        else
        {
            bool transient = status == 404 || status is 429 or >= 500 and <= 599
                || failure is HttpRequestException { StatusCode: null } or HttpIOException or TimeoutException;
            bool intervention = !transient || status != 404 && (attempt >= 3 || previous.ManualAttempt);
            TimeSpan wait = status == 404
                ? TimeSpan.FromMinutes(15 * (1 << Math.Min(4, missing - 1)))
                : attempt == 1 ? TimeSpan.FromMinutes(1) : TimeSpan.FromMinutes(5);
            DateTimeOffset? due = intervention ? null : now + wait;
            bool expired = status == 404 && owner.EndObservedAt is DateTimeOffset end && now >= end.AddHours(12);
            next = previous with { Status = intervention ? MediaWorkStatus.NeedsIntervention
                    : expired ? MediaWorkStatus.Expired : MediaWorkStatus.Waiting,
                Attempts = attempt, ConsecutiveNotFound = missing, LastAttemptAt = now,
                DueAt = expired ? null : due, Failure = failure.Message, HttpStatus = status, ManualAttempt = false };
        }
        if (!store.CommitMedia(next)) return false;
        if (next.Status == MediaWorkStatus.Waiting && next.DueAt != null)
        {
            lock (_stateLock) { _scheduled[Prepare(item).Job] = new ScheduledWork(item, next); SignalScheduleLocked(); }
        }
        _changes.Signal();
        return true;
    }

    private void RemovePendingJobsLocked(ThreadState threadState)
    {
        foreach (QueuedDownload interrupted in _interruptedDownloads.Where(job => ReferenceEquals(job.ThreadState, threadState)).ToArray())
        {
            _interruptedDownloads.Remove(interrupted);
            _jobs.Remove(interrupted.Identity);
            threadState.OutstandingJobs--;
        }
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
            if (_scheduleTask != null) await _scheduleTask.ConfigureAwait(false);
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
    private sealed record ScheduledWork(DownloadItem Item, MediaWork Work);
    private sealed record AdmissionWaiter(IThread Thread);
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
        public CancellationToken EpochToken { get; set; }

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
