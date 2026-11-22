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
using MeowChan.Diagnostics;
using System.Net;
using System.Net.Http;
using Serilog;
using Microsoft.Data.Sqlite;

public enum DownloadPriority { Manual, BoardDiscovery }

/// <summary>Identifies the queue's closed source gate without classifying unrelated provider cancellation as shutdown.</summary>
internal sealed class QueueStoppingException(CancellationToken cancellationToken)
    : OperationCanceledException("The queue is stopping.", cancellationToken);


/// <summary>
/// Owns the session's media jobs and shared worker limit. Thread owners explicitly
/// cancel their jobs when their watch ends; the queue does not subscribe to events.
/// </summary>
public partial class DownloadQueue
{
    private readonly object _stateLock = new object();
    // Resource fences are shared with Registry and span final manifest adoption. Never acquire
    // a fence, call the store or invoke an external callback while holding _stateLock.
    private int _durableTransitions;
    private TaskCompletionSource _durableDrained = CreateCompletedSource();
    private readonly MetadataAdmission _metadataAdmission = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly DownloadTargetGate _targets;
    private Task? _targetPolling;
    private int _targetPollingFailures;
    private TaskCompletionSource _targetChanged = CreateCompletionSource();
    private int _activeFilteredDownloads;
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
    private readonly ConditionalWeakTable<IThread, object> _retiringThreads = new();
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
    // Consecutive unexpected loop faults without a completed pass; the loop is not restarted at the limit.
    private int _scheduleFailures;
    private const int MaxLoopFailures = 3;
    private TaskCompletionSource _idleCompletion = CreateCompletedSource();
    private TaskCompletionSource? _stopCompletion;
    private int _maxParallelDownloads;
    private int _activeDownloads;
    private long _receivedMediaBytes;
    private bool _stopping;
    private bool _paused;
    // Internal owner suspension is distinct from the user pause preference.
    // It parks logical work without retiring owners or descriptors.
    
    private TaskCompletionSource _resumed = CreateCompletedSource();

    /// <summary>Coalesced asynchronous invalidation; read a fresh snapshot when notified.</summary>
    public event Action? StateChanged
    {
        add => _changes.Add(value);
        remove => _changes.Remove(value);
    }
    public event Action<string>? PersistenceFailed;

    /// <summary>
    /// Raised after the queue itself changed the profile's private media tags (a carry-over to a new revision
    /// key). The tag file is always written read-modify-write under the profile lease, so no update is lost;
    /// the event lets a host refresh the tag view it published earlier.
    /// </summary>
    internal event Action? MediaTagsCarriedOver;

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

    /// <summary>True once <see cref="StopAllAsync"/> has permanently closed admission.</summary>
    internal bool IsStopping { get { lock (_stateLock) { return _stopping; } } }

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
                _metadataAdmission.SetPaused(paused);
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
                if (!_paused && !_producerSuspended) return;
                resumed = _producerSuspended ? _producerResumed.Task : _resumed.Task;
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

    public long MinimumFreeSpaceBytes => _targets.Reserve;
    public IReadOnlyList<DownloadTargetWaitState> TargetWaitStates => _targets.Snapshot();
    public void UpdateMinimumFreeSpaceBytes(long bytes) => _targets.UpdateReserve(bytes);

    /// <summary>The profile's configured private tag write limits, used when a commit carries media tags over.</summary>
    internal Tags.PrivateTagLimits PrivateTagLimits { get; set; } = new();
    public Task CheckTargetsAsync(CancellationToken cancellationToken = default) => _targets.CheckBlockedAsync(true, cancellationToken);
    internal DownloadTargetGate TargetGate => _targets;

    private string TargetPath(DownloadItem item)
    {
        lock (_stateLock) return _workOwners.TryGetValue(item.Thread, out WorkResource? owner)
            ? Path.GetFullPath(owner.OutputRoot) : Path.GetDirectoryName(item.DestinationPath)!;
    }

    private void OnTargetsChanged()
    {
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            if (_stopping) return;
            TaskCompletionSource old = _targetChanged;
            _targetChanged = CreateCompletionSource();
            old.TrySetResult();
            if (_targetPolling == null && !_producerSuspended && _targetPollingFailures < MaxLoopFailures && _targets.NextCheckAt != null)
                _targetPolling = Task.Run(() => SuperviseLoopAsync(PollTargetsAsync, schedule: false));
            if (_workStore != null) SignalScheduleLocked();
            ready = ReserveWorkersLocked();
        }
        StartWorkers(ready);
        _changes.Signal();
    }

    private async Task PollTargetsAsync()
    {
        try
        {
            while (true)
            {
                Task changed;
                TimeSpan delay;
                lock (_stateLock)
                {
                    if (_stopping) return;
                    DateTimeOffset? next = _targets.NextCheckAt;
                    if (next == null) { _targetPolling = null; return; }
                    changed = _targetChanged.Task;
                    delay = next.Value - _timeProvider.GetUtcNow();
                }
                await WaitUntilResumedAsync(_lifetime.Token).ConfigureAwait(false);
                if (delay > TimeSpan.Zero)
                {
                    try { await changed.WaitAsync(delay, _timeProvider, _lifetime.Token).ConfigureAwait(false); }
                    catch (TimeoutException) { }
                }
                else
                {
                    try
                    {
                        using Transition execution = BeginTransition();
                        await _targets.CheckBlockedAsync(false, _lifetime.Token).ConfigureAwait(false);
                    }
                    catch (ProducerSuspendedException) { }
                }
                lock (_stateLock) _targetPollingFailures = 0;
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    /// <summary>
    /// Reports an unexpected background loop fault and clears its task so the next signal restarts it.
    /// Repeated faults without a completed pass stop the loop visibly instead of restarting it in a circle.
    /// </summary>
    private async Task SuperviseLoopAsync(Func<Task> loop, bool schedule)
    {
        try { await loop().ConfigureAwait(false); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || _stopping) { }
        catch (Exception failure)
        {
            bool stopped;
            lock (_stateLock)
            {
                if (schedule) { _scheduleTask = null; stopped = ++_scheduleFailures >= MaxLoopFailures; }
                else { _targetPolling = null; stopped = ++_targetPollingFailures >= MaxLoopFailures; }
            }
            string name = schedule ? "Automatic media retry scheduling" : "Storage target rechecking";
            string warning = stopped
                ? $"{name} stopped after {MaxLoopFailures} consecutive failures: {failure.Message}"
                : $"{name} failed and restarts with the next change: {failure.Message}";
            _logger.Error(failure, "{Warning}", warning);
            RaisePersistenceFailed(warning);
            _changes.Signal();
        }
    }

    private async Task<bool> WaitForTargetAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        while (true)
        {
            Task changed;
            lock (_stateLock)
            {
                if (_stopping || _cancelledThreads.TryGetValue(item.Thread, out _)) return false;
                changed = _capacityChanged.Task;
            }
            await WaitForProducerAsync(linked.Token).ConfigureAwait(false);
            Task targetChanged = _targets.ChangeTask;
            try
            {
                if (await CheckTargetAsync(item, linked.Token).ConfigureAwait(false)) return true;
            }
            catch (ProducerSuspendedException) { continue; }
            await Task.WhenAny(changed, targetChanged).WaitAsync(linked.Token).ConfigureAwait(false);
        }
    }

    private async Task<bool> CheckTargetAsync(DownloadItem item, CancellationToken cancellationToken)
    {
        using Transition execution = BeginTransition(item);
        return await _targets.CheckAsync(TargetPath(item), item.ExpectedSize, cancellationToken).ConfigureAwait(false);
    }

    private void OnFilterCapacityChanged()
    {
        List<QueuedDownload> ready;
        lock (_stateLock) { ready = ReserveWorkersLocked(); }
        StartWorkers(ready);
        _changes.Signal();
    }

    private void AttachFilter(QueuedDownload job)
    {
        job.SubscribedFilter = job.Item.Filter;
        if (job.SubscribedFilter != null) job.SubscribedFilter.CapacityChanged += OnFilterCapacityChanged;
    }
    private void DetachFilter(QueuedDownload job)
    {
        if (job.SubscribedFilter != null) job.SubscribedFilter.CapacityChanged -= OnFilterCapacityChanged;
        job.SubscribedFilter = null;
    }

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
        _targets = new DownloadTargetGate(_timeProvider, _lifetime.Token);
        _targets.Changed += OnTargetsChanged;
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
        lock (_stateLock)
        {
            if (CleanupWaitLocked(item) != null)
                throw new InvalidOperationException("The target is reserved for archive cleanup; use EnqueueAsync to wait.");
        }
        using (Transition transition = EnterDurable(item))
        {
            if (!PrepareDurableAdmission(ref item, prepared, admission: transition.Admission)) return;
            TryEnqueuePrepared(item, prepared, out bool full, fromWaiter: false);
            if (full) throw new InvalidOperationException("Download capacity is full; use EnqueueAsync to wait without losing work.");
        }
    }

    public int MaxPendingDownloads => _maxPendingDownloads;

    /// <summary>Explicit durable mode. A library caller that does not configure it remains volatile.</summary>
    public void ConfigureWorkState(IWorkStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        lock (_stateLock)
        {
            if (_workStore != null && !ReferenceEquals(_workStore, store)) throw new InvalidOperationException("The work store is already configured.");
            if (_boundProfileLease != null && store is WorkStateStore sqlite && !ReferenceEquals(sqlite.ProfileLease, _boundProfileLease))
                throw new InvalidOperationException("The queue work store belongs to a different profile lease.");
            _workStore = store;
        }
    }

    /// <summary>Restores work before a provider lifetime starts; no endpoint request is required.</summary>
    public void BindWork(IThread thread, WorkResource resource)
    {
        ArgumentNullException.ThrowIfNull(thread);
        IWorkStateStore store = _workStore ?? throw new InvalidOperationException("Configure durable work before binding watches.");
        using IDisposable transition = BeginTransition(executing: false);
        IReadOnlyList<MediaWork> saved;
        using (IDisposable fence = WorkStateAccess.For(store).Enter(resource.Url))
        {
            lock (_stateLock)
                if (_stopping || _cancelledThreads.TryGetValue(thread, out _)) return;
            WorkResource? current = WorkStateAccess.For(store).FindResource(resource.Url);
            if (current == null || current.Generation != resource.Generation || current.Intent == WorkIntent.Suppressed) return;
            saved = store.LoadMedia(resource.Url, resource.Generation);
            // Filter binding can invoke provider code: construct outside the queue state lock.
            ScheduledWork[] restored = saved.Where(media => media.Status is not (MediaWorkStatus.Succeeded
                or MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired or MediaWorkStatus.Excluded))
                .Select(media => new ScheduledWork(RestoreItem(thread, media), media)).ToArray();
            lock (_stateLock)
            {
                if (_stopping || _cancelledThreads.TryGetValue(thread, out _)) return;
                if (_workOwners.TryGetValue(thread, out WorkResource? old) && old.Generation != resource.Generation)
                    throw new InvalidOperationException("A bound watch instance cannot change generation.");
                if (_cleanupReservations.Any(reservation => reservation.Matches(thread)
                    || saved.Any(media => reservation.MatchesPath(media.DestinationPath)
                        || media.LegacyPath != null && reservation.MatchesPath(media.LegacyPath))))
                    throw new InvalidOperationException("The archived resource is reserved for cleanup.");
                _workOwners[thread] = current;
                foreach (ScheduledWork work in restored) _scheduled[Prepare(work.Item).Job] = work;
            }
        }
        // Provider notifications run outside the resource fence and queue lock.
        if (thread is Boards.Base.BaseThread baseThread) baseThread.RestoreDurableMedia(saved);
        lock (_stateLock) if (OwnerStillCurrentLocked(thread, resource)) SignalScheduleLocked();
    }

    public int RequestMediaRetry(IThread thread, string? mediaIdentifier = null)
        => RequestMediaRetryAdmitted(thread, mediaIdentifier, null);

    internal int RequestMediaRetryAdmitted(IThread thread, string? mediaIdentifier, ProfileAdmissionToken? admission)
    {
        ArgumentNullException.ThrowIfNull(thread);
        IWorkStateStore? store = _workStore;
        if (store == null) return 0;
        using Transition retryTransition = BeginTransition(admission: admission);
        admission = retryTransition.Admission;
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
            if (media.Status is MediaWorkStatus.Succeeded or MediaWorkStatus.Excluded || media.ManualAttempt) continue;
            if (RequestMediaRetryItem(thread, owner, store, media, admission)) count++;
        }
        if (count > 0) _changes.Signal();
        return count;
    }

    private bool RequestMediaRetryItem(IThread thread, WorkResource owner, IWorkStateStore store, MediaWork media,
        ProfileAdmissionToken? admission)
    {
        using IDisposable resourceFence = WorkStateAccess.For(store).Enter(owner.Url);
        MediaWork? current = WorkStateAccess.For(store).FindMedia(MediaWorkKey.Of(media));
        if (current == null || !WorkStateComparison.Equal(current, media) || current.ManualAttempt
            || current.Status is MediaWorkStatus.Succeeded or MediaWorkStatus.Excluded) return false;
        DownloadItem item = RestoreItem(thread, current, explicitRetry: true);
        using Transition retryItemTransition = BeginTransition(item, admission);
        PreparedDownload prepared = Prepare(item);
        lock (_stateLock)
        {
            if (!ItemOwnerCurrentLocked(item, owner) || CleanupWaitLocked(item) != null || _jobs.ContainsKey(prepared.Job)) return false;
        }
        MediaWork manual = current with { ManualAttempt = true, DueAt = _timeProvider.GetUtcNow(), Status = MediaWorkStatus.Ready };
        if (!WorkStateAccess.For(store).TryCommitMedia(manual, current, admission)) return false;
        lock (_stateLock)
        {
            if (!ItemOwnerCurrentLocked(item, owner)) return false;
            _quarantined.Remove(prepared.Job);
            _scheduled[prepared.Job] = new ScheduledWork(item, manual);
            SignalScheduleLocked();
        }
        return true;
    }

    private static DownloadItem RestoreItem(IThread thread, MediaWork media, bool explicitRetry = false)
    {
        DownloadItem item = new(new Uri(media.DownloadUrl), media.DestinationPath, thread.Board.ImageBoard, thread, media.MediaIdentifier,
            media.ExpectedSize, media.ExpectedMd5, media.OriginalFileName, media.LegacyPath, media.Variant)
        {
            ExpectedSha256 = media.ExpectedSha256, ExpectedSha1 = media.ExpectedSha1, ProviderSizeHint = media.ProviderSizeHint, AcquisitionRevisionKey = media.AcquisitionRevisionKey,
            AttachmentReferences = media.AttachmentReferences
        };
        if (thread is Boards.Base.BaseThread baseThread) item = baseThread.BindRestoredMediaFilter(item, explicitRetry);
        return item.WithWorkAuthority(media.ResourceUrl, media.Generation);
    }

    private void SignalScheduleLocked()
    {
        TaskCompletionSource old = _scheduleChanged;
        _scheduleChanged = CreateCompletionSource();
        old.TrySetResult();
        if (_scheduleTask == null && !_stopping && !_producerSuspended && _scheduleFailures < MaxLoopFailures)
            _scheduleTask = Task.Run(() => SuperviseLoopAsync(RunScheduleAsync, schedule: true));
    }

    // Deterministic schedule fault injection for tests; never invoked under the queue state lock.
    internal Action? BeforeSchedulePass { get; set; }

    private async Task RunScheduleAsync()
    {
        while (true)
        {
            try { await WaitForProducerAsync(_lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested || _stopping) { return; }
            BeforeSchedulePass?.Invoke();
            Transition? pass = null;
            ScheduledWork? due = null;
            Task changed;
            TimeSpan? delay = null;
            List<ScheduledWork> expired = new();
            try
            {
                lock (_stateLock)
                {
                    if (_stopping) return;
                    if (_producerSuspended) continue;
                    pass = BeginTransition();
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
                            TimeSpan untilDeadline = deadline - now;
                            if (delay == null || untilDeadline < delay) delay = untilDeadline;
                            if (at >= deadline) continue;
                        }
                        if (_targets.IsBlocked(TargetPath(candidate.Item))) continue;
                        if (at <= now && due == null) due = candidate;
                        else if (at > now && (delay == null || at - now < delay)) delay = at - now;
                    }
                    foreach (ScheduledWork item in expired) _cleanupTransits.Add(item.Item);
                    foreach (JobIdentity key in expiredKeys) _scheduled.Remove(key);
                    if (due != null)
                    {
                        _cleanupTransits.Add(due.Item);
                        _scheduled.Remove(Prepare(due.Item).Job);
                    }
                    changed = _scheduleChanged.Task;
                }
                foreach (ScheduledWork item in expired)
                {
                    try
                    {
                        ExpireScheduledWork(item, pass.Admission);
                    }
                    catch (Exception error) { Quarantine(item.Item, error); }
                    finally { lock (_stateLock) _cleanupTransits.Remove(item.Item); }
                }
                if (expired.Count > 0) _changes.Signal();
                if (due != null)
                {
                    try
                    {
                        if (!await _targets.CheckAsync(TargetPath(due.Item), due.Item.ExpectedSize, _lifetime.Token).ConfigureAwait(false))
                        {
                            lock (_stateLock) _scheduled.TryAdd(Prepare(due.Item).Job, due);
                            continue;
                        }
                        // Due work becomes logical data again before capacity/target waits.
                        // Its next execution obtains a fresh scope after any owner resume.
                        lock (_stateLock) _scheduled.TryAdd(Prepare(due.Item).Job, due);
                        pass.Dispose();
                        pass = null;
                        // Admission rechecks the exact scheduled row under its own resource fence.
                        await EnqueueCoreAsync(due.Item, _lifetime.Token, due.Work).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                    catch (Exception error) { Quarantine(due.Item, error); }
                    finally { lock (_stateLock) _cleanupTransits.Remove(due.Item); }
                    continue;
                }
                pass?.Dispose();
                pass = null;
                lock (_stateLock) _scheduleFailures = 0;
                if (delay == null) await changed.ConfigureAwait(false);
                else
                {
                    using CancellationTokenSource timer = new();
                    Task elapsed = Task.Delay(delay.Value, _timeProvider, timer.Token);
                    if (await Task.WhenAny(changed, elapsed).ConfigureAwait(false) == changed) timer.Cancel();
                }
            }
            finally { pass?.Dispose(); }
        }
    }

    private void ExpireScheduledWork(ScheduledWork item, ProfileAdmissionToken? admission)
    {
        Cleanup cleanup = new();
        using (EnterDurable(item.Item, admission, continuation: true))
        {
            lock (_stateLock)
                if (!_workOwners.TryGetValue(item.Item.Thread, out WorkResource? owner)
                    || !ItemOwnerCurrentLocked(item.Item, owner)) return;
            MediaWork? current = _workStore == null ? null : WorkStateAccess.For(_workStore).FindMedia(MediaWorkKey.Of(item.Work));
            if (WorkStateComparison.Equal(current, item.Work) && _workStore != null
                && WorkStateAccess.For(_workStore).TryCommitMedia(item.Work with { Status = MediaWorkStatus.Expired, DueAt = null }, current, admission))
            {
                lock (_stateLock)
                {
                    JobIdentity identity = Prepare(item.Item).Job;
                    if (_jobs.TryGetValue(identity, out QueuedDownload? job) && !job.IsActive
                        && ReferenceEquals(job.Item.Thread, item.Item.Thread) && SameRetrieval(job.Item, item.Item))
                    {
                        _pendingDownloads.Remove(job);
                        _interruptedDownloads.Remove(job);
                        _jobs.Remove(identity);
                        DetachFilter(job);
                        job.ThreadState.OutstandingJobs--;
                        RetireThreadIfIdleLocked(job.ThreadState, cleanup);
                        SignalIdleIfNeededLocked(cleanup);
                        PulseCapacityLocked();
                    }
                }
            }
        }
        RunCleanup(cleanup);
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
        lock (_stateLock)
        {
            if (CleanupWaitLocked(item) != null) return false;
        }
        try
        {
            using Transition transition = EnterDurable(item);
            if (!PrepareDurableAdmission(ref item, prepared, admission: transition.Admission)) return false;
            return TryEnqueuePrepared(item, prepared, out _, fromWaiter: false);
        }
        catch (ProducerSuspendedException) { return false; }
    }

    /// <summary>
    /// Waits for fair admission when capacity is full. False means duplicate or closed ownership,
    /// not a discarded job due to backpressure. Caller cancellation ends only this admission wait.
    /// </summary>
    public Task<bool> EnqueueAsync(DownloadItem item, CancellationToken cancellationToken = default)
        => EnqueueCoreAsync(item, cancellationToken);

    private async Task<bool> EnqueueCoreAsync(DownloadItem item, CancellationToken cancellationToken, MediaWork? expected = null)
    {
        using IDisposable admissionTransition = BeginTransition(item, executing: false);
        PreparedDownload prepared = Prepare(item);
        AdmissionWaiter waiter;
        LinkedListNode<AdmissionWaiter> node;
        while (true)
        {
            Task? cleanup;
            lock (_stateLock)
            {
                if (_stopping || _cancelledThreads.TryGetValue(item.Thread, out _)) return false;
                cleanup = CleanupWaitLocked(item);
            }
            if (cleanup == null)
            {
                try
                {
                    if (!await WaitForProducerAsync(cancellationToken, item.Thread).ConfigureAwait(false)) return false;
                }
                catch (QueueStoppingException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Stop can win before this caller has published an AdmissionWaiter.
                    // A closed queue still completes admission with false; it must not leak
                    // the internal stop sentinel to a caller that did not cancel its request.
                    return false;
                }
                bool admitted = false;
                AdmissionWaiter? created = null;
                LinkedListNode<AdmissionWaiter>? createdNode = null;
                try
                {
                using (Transition transition = EnterDurable(item))
                {
                    // Store I/O happens outside the state lock; ownership is re-validated before publication.
                    if (!PrepareDurableAdmission(ref item, prepared, expected, transition.Admission)) return false;
                    lock (_stateLock)
                    {
                        if (_stopping || _cancelledThreads.TryGetValue(item.Thread, out _)) return false;
                        cleanup = CleanupWaitLocked(item);
                        if (cleanup == null)
                        {
                            created = new AdmissionWaiter(item);
                            createdNode = _admissionWaiters.AddLast(created);
                            PulseCapacityLocked();
                            admitted = true;
                        }
                    }
                }
                }
                catch (ProducerSuspendedException) { continue; }
                if (admitted)
                {
                    waiter = created!;
                    node = createdNode!;
                    break;
                }
            }
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            try { await cleanup!.WaitAsync(waiting.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _lifetime.IsCancellationRequested)
            {
                return false;
            }
        }
        try
        {
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, waiter.Closed.Token);
            if (!await WaitForTargetAsync(item, linked.Token).ConfigureAwait(false)) return false;
            if (BeforeFinalAdmission != null) await BeforeFinalAdmission(item).ConfigureAwait(false);
            lock (_stateLock) { waiter.TargetReady = true; PulseCapacityLocked(); }
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForProducerAsync(linked.Token).ConfigureAwait(false);
                Task changed;
                bool selected;
                lock (_stateLock)
                {
                    if (_stopping || _cancelledThreads.TryGetValue(item.Thread, out _)) return false;
                    selected = ReferenceEquals(NextWaiterLocked(), waiter);
                    changed = _capacityChanged.Task;
                }
                if (selected)
                {
                    // The final boundary is resource fence -> store -> short state lock. An old
                    // waiter cannot overwrite a newer admitted descriptor while awaiting target/capacity.
                    try
                    {
                        bool accepted = TryAdmitWaiter(item, prepared, waiter, out bool full, ref changed);
                        if (!full) return accepted;
                    }
                    catch (ProducerSuspendedException) { continue; }
                }
                await changed.WaitAsync(linked.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested &&
            (waiter.Closed.IsCancellationRequested || _lifetime.IsCancellationRequested)) { return false; }
        finally
        {
            lock (_stateLock) { _admissionWaiters.Remove(node); PulseCapacityLocked(); }
            waiter.Closed.Dispose();
        }
    }

    private bool TryAdmitWaiter(DownloadItem item, PreparedDownload prepared, AdmissionWaiter waiter,
        out bool full, ref Task changed)
    {
        full = false;
        using Transition transition = EnterDurable(item);
        if (!CanRunDurableLocked(item, transition.Admission)) return false;
        lock (_stateLock) changed = _capacityChanged.Task;
        bool accepted = TryEnqueuePrepared(item, prepared, out full, fromWaiter: true, expectedWaiter: waiter);
        if (!full)
        {
            lock (_stateLock) _preferManualAdmission = GetPriorityLocked(waiter.Thread) != DownloadPriority.Manual;
        }
        return accepted;
    }

    private static PreparedDownload Prepare(DownloadItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        ThreadIdentity threadIdentity = GetThreadIdentity(item.Thread);
        string destinationPath = Path.GetFullPath(item.DestinationPath);
        if (OperatingSystem.IsWindows()) destinationPath = destinationPath.ToUpperInvariant();
        return new PreparedDownload(threadIdentity, new JobIdentity(threadIdentity, item.MediaIdentifier, item.Variant, destinationPath));
    }

    /// <summary>Caller holds the resource fence, never _stateLock during I/O.</summary>
    private bool PrepareDurableAdmission(ref DownloadItem item, PreparedDownload prepared, MediaWork? expected = null, ProfileAdmissionToken? admission = null)
    {
        IWorkStateStore? store = JournalFor(item);
        if (store == null) return true;
        WorkResource owner;
        lock (_stateLock)
        {
            if (_quarantined.Contains(prepared.Job) || CleanupWaitLocked(item) != null) return false;
            if (!_workOwners.TryGetValue(item.Thread, out WorkResource? found) || _cancelledThreads.TryGetValue(item.Thread, out _)) return false;
            owner = found;
            if (!ItemOwnerCurrentLocked(item, owner)) return false;
        }
        item = item.WithWorkAuthority(owner.Url, owner.Generation);
        MediaWork? previous = FindMedia(store, owner, item);
        if (expected != null && !WorkStateComparison.Equal(previous, expected)) return false;
        bool newRevision = previous != null && !SameRetrieval(previous, item);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        MediaWork next = previous == null || newRevision ? new MediaWork
        {
            ResourceUrl = owner.Url, Generation = owner.Generation, MediaIdentifier = item.MediaIdentifier, Variant = item.Variant,
            DestinationPath = item.DestinationPath, DownloadUrl = item.DownloadUri.AbsoluteUri,
            ExpectedSize = item.ExpectedSize, ExpectedMd5 = item.ExpectedMd5, ExpectedSha256 = item.ExpectedSha256, ExpectedSha1 = item.ExpectedSha1, ProviderSizeHint = item.ProviderSizeHint,
            AcquisitionRevisionKey = item.AcquisitionRevisionKey, AttachmentReferences = item.AttachmentReferences,
            OriginalFileName = item.OriginalFileName, LegacyPath = item.LegacyPath,
            Status = MediaWorkStatus.Ready
        } : previous with
        {
            DownloadUrl = item.DownloadUri.AbsoluteUri,
            OriginalFileName = item.OriginalFileName,
            LegacyPath = item.LegacyPath
        };
        if (next.Status is MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired or MediaWorkStatus.Excluded) return false;
        if (!next.ManualAttempt && owner.EndObservedAt is DateTimeOffset end && next.HttpStatus == 404 && now >= end.AddHours(12)) return false;
        if (!WorkStateAccess.For(store).TryCommitMedia(next, previous, admission)) return false;
        if (next.DueAt is DateTimeOffset due && due > now)
        {
            lock (_stateLock)
            {
                if (!OwnerStillCurrentLocked(item.Thread, owner)) return false;
                if (!_scheduled.TryGetValue(prepared.Job, out ScheduledWork? scheduled)
                    || scheduled.Work != next || scheduled.Item.DownloadUri != item.DownloadUri)
                {
                    _scheduled[prepared.Job] = new ScheduledWork(item, next);
                    SignalScheduleLocked();
                }
            }
            return false;
        }
        lock (_stateLock)
        {
            if (!ItemOwnerCurrentLocked(item, owner)) return false;
            _scheduled.Remove(prepared.Job);
        }
        return true;
    }

    /// <summary>True while the thread still owns the same durable resource generation and is not being cancelled.</summary>
    private bool OwnerStillCurrentLocked(IThread thread, WorkResource owner)
        => _workOwners.TryGetValue(thread, out WorkResource? current)
            && current.Generation == owner.Generation && string.Equals(current.Url, owner.Url, StringComparison.Ordinal)
            && !_stopping && current.Intent != WorkIntent.Suppressed
            && !_retiringThreads.TryGetValue(thread, out _) && !_cancelledThreads.TryGetValue(thread, out _);

    public void UpdateWorkOwner(IThread thread, WorkResource resource)
    {
        lock (_stateLock)
        {
            if (_workOwners.ContainsKey(thread)) { _workOwners[thread] = resource; SignalScheduleLocked(); }
        }
    }

    private bool TryEnqueuePrepared(DownloadItem item, PreparedDownload prepared, out bool full, bool fromWaiter, AdmissionWaiter? expectedWaiter = null)
    {
        full = false;
        ThreadIdentity threadIdentity = prepared.Thread;
        JobIdentity jobIdentity = prepared.Job;
        string url = item.DownloadUri.ToString();
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            if (expectedWaiter != null && !ReferenceEquals(NextWaiterLocked(), expectedWaiter)) { full = true; return false; }
            if (CleanupWaitLocked(item) != null) { full = true; return false; }
            if (_stopping || _cancelledThreads.TryGetValue(item.Thread, out _)
                || JournalFor(item) != null && (!_workOwners.TryGetValue(item.Thread, out WorkResource? itemOwner) || !ItemOwnerCurrentLocked(item, itemOwner)))
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
                        if (expectedWaiter != null) RecordUserReservationLocked(expectedWaiter.Thread, admission: true);
                    }
                    else if (!needsSuccessor && existing.Replacement != null)
                    { _reservedSuccessors--; PulseCapacityLocked(); }
                    existing.Replacement = needsSuccessor ? item : null;
                }
                else { DetachFilter(existing); existing.Item = item; AttachFilter(existing); }
                ready = new List<QueuedDownload>();
            }
            else
            {

                if ((!fromWaiter && (_admissionWaiters.Count > 0))
                    || _jobs.Count + _reservedSuccessors >= (long)_maxPendingDownloads + EffectiveParallelDownloadsLocked
                    || ((_paused || _activeDownloads >= EffectiveParallelDownloadsLocked) && _pendingDownloads.Count + _reservedSuccessors >= _maxPendingDownloads))
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
                AttachFilter(job);
                _pendingDownloads.AddLast(job);
                if (expectedWaiter != null) RecordUserReservationLocked(expectedWaiter.Thread, admission: true);
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
        AdmissionWaiter[] waiters;
        List<DownloadItem> cancelledExternal = new();
        lock (_stateLock)
        {
            _cancelledThreads.GetValue(thread, static (IThread _) => new object());
            waiters = _admissionWaiters.Where(waiter => ReferenceEquals(waiter.Thread, thread)).ToArray();
            _workOwners.Remove(thread);
            foreach (JobIdentity key in _scheduled.Where(entry => ReferenceEquals(entry.Value.Item.Thread, thread))
                .Select(entry => entry.Key).ToArray()) _scheduled.Remove(key);
            if (_workStore != null) SignalScheduleLocked();
            if (!_threads.TryGetValue(identity, out threadState) || !ReferenceEquals(threadState.Thread, thread))
            {
                PulseCapacityLocked();
                threadState = null;
                ready = new();
            }
            else
            {
                // Keep the CTS alive while cancellation callbacks execute outside the
                // lock, even if the final worker finishes at the same time.
                threadState.CancellationLeases++;
                cancelledExternal = RemovePendingJobsLocked(threadState);
                RetireThreadIfIdleLocked(threadState, cleanup);
                SignalIdleIfNeededLocked(cleanup);
                ready = ReserveWorkersLocked();
            }
        }

        // External owners have no chan notification path: notify them outside the state lock.
        foreach (DownloadItem item in cancelledExternal) NotifyExternalCancelled(item, ExternalMediaCancellation.Cancelled);
        RunCleanup(cleanup);
        Task waiterCancellation = CancelWaitersAsync(waiters);
        Task cancellation = threadState == null ? Task.CompletedTask : CancelThreadStateAsync(threadState);
        StartWorkers(ready);
        _changes.Signal();
        return Task.WhenAll(cancellation, waiterCancellation);
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
        AdmissionWaiter[] waiters;
        List<DownloadItem> stoppedExternal = new();
        lock (_stateLock)
        {
            if (_stopCompletion != null)
            {
                return _stopCompletion.Task;
            }

            _stopping = true;
            _producerClosed = true;
            _metadataAdmission.Close();
            waiters = _admissionWaiters.ToArray();
            _scheduleChanged.TrySetResult();
            _resumed.TrySetResult();
            _producerResumed.TrySetResult();
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
                DetachFilter(interrupted.Value);
                interrupted.Value.ThreadState.OutstandingJobs--;
                if (interrupted.Value.Item.Thread is IExternalMediaWorkOwner) stoppedExternal.Add(interrupted.Value.Item);
            }

            while (_pendingDownloads.First is LinkedListNode<QueuedDownload> node)
            {
                QueuedDownload job = node.Value;
                _pendingDownloads.RemoveFirst();
                _jobs.Remove(job.Identity);
                DetachFilter(job);
                job.ThreadState.OutstandingJobs--;
                if (job.Item.Thread is IExternalMediaWorkOwner) stoppedExternal.Add(job.Item);
            }

            foreach (ThreadState threadState in threads)
            {
                RetireThreadIfIdleLocked(threadState, cleanup);
            }

            SignalIdleIfNeededLocked(cleanup);
            PulseCapacityLocked();
        }

        Task waiterCancellation = CancelWaitersAsync(waiters);
        _lifetime.Cancel();
        // Queued and interrupted external jobs never reach a worker again. A stop is not an owner
        // decision, so they are told Stopped and keep their own durable request for recovery.
        foreach (DownloadItem item in stoppedExternal) NotifyExternalCancelled(item, ExternalMediaCancellation.Stopped);
        RunCleanup(cleanup);
        _changes.Signal();
        _ = CompleteStopAsync(threads, stopCompletion, waiterCancellation);
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
        AdmissionWaiter[] eligible = _admissionWaiters.Where(waiter => waiter.TargetReady
            && !_targets.IsBlocked(TargetPath(waiter.Item))).ToArray();
        UserDownloadPriority? group = PreferredUserGroupLocked(
            eligible.Any(waiter => GetUserPriorityLocked(waiter.Thread) == UserDownloadPriority.High),
            eligible.Any(waiter => GetUserPriorityLocked(waiter.Thread) == UserDownloadPriority.Normal), admission: true);
        if (group == null) return null;
        AdmissionWaiter? fallback = null;
        foreach (AdmissionWaiter waiter in eligible)
        {
            if (GetUserPriorityLocked(waiter.Thread) != group) continue;
            fallback ??= waiter;
            if (GetPriorityLocked(waiter.Thread) == preferred) return waiter;
        }
        return fallback;
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
        while (!_stopping && !_paused && !_producerSuspended && _activeDownloads < EffectiveParallelDownloadsLocked && (_pendingDownloads.Count > 0 || _interruptedDownloads.Count > 0))
        {
            DownloadPriority preferred = _preferManualDispatch ? DownloadPriority.Manual : DownloadPriority.BoardDiscovery;
            var eligible = new List<LinkedListNode<QueuedDownload>>();
            bool high = false, normal = false;
            foreach (LinkedList<QueuedDownload> list in new[] { _interruptedDownloads, _pendingDownloads })
            for (LinkedListNode<QueuedDownload>? node = list.First; node != null; node = node.Next)
            {
                QueuedDownload candidate = node.Value;
                if (CleanupWaitLocked(candidate.Item) != null || _targets.IsBlocked(TargetPath(candidate.Item))) continue;
                if (candidate.Item.Filter != null && (_activeFilteredDownloads >= Math.Max(1, Math.Min(2, EffectiveParallelDownloadsLocked - 1))
                    || !candidate.Item.Filter.CanStart)) continue;
                eligible.Add(node);
                if (GetUserPriorityLocked(candidate.Item.Thread) == UserDownloadPriority.High) high = true;
                else normal = true;
            }
            UserDownloadPriority? group = PreferredUserGroupLocked(high, normal, admission: false);
            LinkedListNode<QueuedDownload>? chosen = null;
            foreach (LinkedListNode<QueuedDownload> node in eligible)
            {
                if (GetUserPriorityLocked(node.Value.Item.Thread) != group) continue;
                bool matches = GetPriorityLocked(node.Value.Item.Thread) == preferred;
                bool chosenMatches = chosen != null && GetPriorityLocked(chosen.Value.Item.Thread) == preferred;
                // Preserve the existing acquisition-origin alternation for Normal work.
                // High watches share their boosted group by least-recent dispatch.
                bool better = chosen == null || (group == UserDownloadPriority.High
                    ? UserPriorityPolicy.Better(node.Value.ThreadState.LastDispatch, matches,
                        chosen!.Value.ThreadState.LastDispatch, chosenMatches)
                    : matches != chosenMatches ? matches
                        : node.Value.ThreadState.LastDispatch < chosen!.Value.ThreadState.LastDispatch);
                if (better) chosen = node;
            }
            if (chosen == null) break;
            QueuedDownload job = chosen.Value;
            // Admission can refuse (e.g. a standalone queue's first execution).
            // Acquire before altering logical reservations or active accounting.
            ProfileProducerEpoch.Use? producerUse = EnterProducerLocked();
            chosen.List!.Remove(chosen);
            RecordUserReservationLocked(job.Item.Thread, admission: false);
            if (job.Item.Filter != null) _activeFilteredDownloads++;
            _preferManualDispatch = GetPriorityLocked(job.Item.Thread) != DownloadPriority.Manual;
            job.ThreadState.LastDispatch = ++_dispatchSequence;
            job.IsActive = true;
            job.EpochToken = _transferEpoch.Token;
            job.ProducerUse = producerUse;
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

    private readonly System.Collections.Concurrent.ConcurrentDictionary<ResourceId, byte> _tagReconciledThreads = new();

    /// <summary>
    /// A re-check can give an attachment a new revision key (changed provider metadata). Once its new binding is
    /// committed, the private media tags of the earlier key for the same attachment and bytes carry over.
    /// This never fails or delays the download: a problem is logged and the tags simply stay where they were.
    /// Hands the media tags over to the keys this commit newly bound (from the manifest the commit already held),
    /// and once per thread and session completes any hand-over a stop between an earlier commit and its tag write
    /// may have lost. Failure only logs: the next start reconciles again.
    /// </summary>
    private async Task CarryMediaTagsAsync(DownloadItem item, MediaCommitResult? committed, ProfileAdmissionToken? admission)
    {
        if (admission == null || item.AttachmentReferences.Count == 0) return;
        try
        {
            ProfileLease? lease = ResolveExecutionProfileLease(item);
            if (lease == null) return;
            bool changed = committed != null && Tags.MediaTagKeyCarryOver.Apply(lease, admission, committed.TagHandovers, CancellationToken.None, PrivateTagLimits);
            if (changed) RaiseMediaTagsCarriedOver();
            ResourceId thread = item.AttachmentReferences[0].Resource;
            if (!_tagReconciledThreads.TryAdd(thread, 0)) return;
            Tags.PrivateTagStoreSnapshot tags = new Tags.PrivateTagStore(lease.ProfileDirectory, lease).ReadSnapshot();
            HashSet<string> keys = tags.MediaAssignments.Where(a => a.Target.Reference.Resource == thread)
                .Select(a => a.Target.Reference.RevisionKey).ToHashSet(StringComparer.Ordinal);
            if (keys.Count == 0) return;
            string directory = Path.GetDirectoryName(Path.GetFullPath(item.DestinationPath))!;
            IReadOnlyList<Tags.MediaTagKeyHandover> pending = await Tags.MediaTagKeyCarryOver
                .FindPendingForThreadAsync(thread, keys, (_, _) => Task.FromResult<IReadOnlyList<string>>(new[] { directory }), CancellationToken.None)
                .ConfigureAwait(false);
            if (Tags.MediaTagKeyCarryOver.Apply(lease, admission, pending, CancellationToken.None, PrivateTagLimits))
                RaiseMediaTagsCarriedOver();
        }
        catch (Exception failure)
        {
            _logger.Warning(failure, "Could not carry private media tags over to the new revision of {MediaId}", item.MediaIdentifier);
        }
    }

    private async Task ProcessDownloadItemAsync(QueuedDownload job)
    {
        using ProfileProducerEpoch.Use? producerUse = job.ProducerUse;
        ProfileAdmissionToken? producerAdmission = producerUse?.Admission;
        using var measurement = PerformanceDiagnostics.Measure("queue.worker", request: job.Item.WorkOperationId,
            watch: job.Item.Thread.ThreadUri.AbsoluteUri, asset: job.Item.AcquisitionRevisionKey ?? job.Item.MediaIdentifier + ":" + job.Item.Variant,
            epoch: job.Item.WorkGeneration ?? 0);
        bool succeeded = false;
        MediaCommitResult? committed = null;
        bool interrupted = false;
        bool targetBlocked = false;
        // An external owner has no journal: every job end that is neither a commit nor a reported
        // failure must reach it as a cancellation, or its completion would never terminate.
        bool externalReported = false;
        using AsyncCancellationLink cancellation = new(job.ThreadState.Token, job.EpochToken);
        try
        {
            job.EpochToken.ThrowIfCancellationRequested();
            cancellation.Token.ThrowIfCancellationRequested();
            // Freeze the consumer identity alongside the already-owned job Use,
            // before durable eligibility can itself finalize expired work.
            ProfileLease? expectedLease = ResolveExecutionProfileLease(job.Item);
            if (expectedLease != null)
                job.Item.WithProfileWriteAdmission(producerAdmission
                    ?? throw new InvalidOperationException("Profile media execution requires the queue job's producer admission."), expectedLease)
                    .RequireProfileWriteAdmission(expectedLease);
            else if (producerAdmission != null)
                throw new InvalidOperationException("Profile queue execution has no expected lease. Root must call BindProfileLease before installing the first producer epoch.");
            if (!CanRunDurable(job.Item, producerAdmission)) return;
            if (!await _targets.CheckAsync(TargetPath(job.Item), job.Item.ExpectedSize, cancellation.Token).ConfigureAwait(false))
            { targetBlocked = true; return; }
            DownloadWorker worker = new DownloadWorker(job.Item, _bandwidthLimiter, _mediaStore, _bodyIdleTimeout, _timeProvider,
                _logger, notifyThread: _workStore == null && job.Item.Thread is not IExternalMediaWorkOwner)
            {
                BytesReceived = bytes => Interlocked.Add(ref _receivedMediaBytes, bytes),
                AcquirePublicationFence = token => AcquirePublicationAsync(job.Item, token, producerAdmission)
            };
            // Use only an actual consumer/runtime lease, never a token-self check
            // or a lookup of whichever producer epoch happens to be installed now.
            await worker.ExecuteAdmittedAsync(expectedLease, producerAdmission, cancellation.Token).ConfigureAwait(false);
            committed = worker.CompletedResult ?? throw new InvalidDataException("Media worker returned without a committed asset receipt.");
            succeeded = true;
        }
        catch (StaleMediaWorkException)
        {
            PerformanceDiagnostics.Record("queue.worker.result", "superseded", request: job.Item.WorkOperationId,
                watch: job.Item.Thread.ThreadUri.AbsoluteUri, asset: job.Item.AcquisitionRevisionKey ?? job.Item.MediaIdentifier,
                epoch: job.Item.WorkGeneration ?? 0);
            // A newer revision owns the slot. The old acquisition has no result authority.
        }
        catch (OperationCanceledException) when (job.ThreadState.Token.IsCancellationRequested || job.EpochToken.IsCancellationRequested)
        {
            interrupted = job.EpochToken.IsCancellationRequested;
            _logger.Debug("Download for {DownloadUri} was cancelled.", job.Item.DownloadUri);
            // An external owner is notified in finally, once it is known whether the job was re-queued.
        }
        catch (MediaFilterOmissionException omission)
        {
            externalReported = true;
            try
            {
                if (RecordFilterOmission(job.Item, omission, producerAdmission))
                {
                    if (job.Item.Thread is Boards.Base.BaseThread thread)
                        thread.PublishMediaFilterOmission(job.Item.MediaIdentifier, job.Item.DestinationPath, omission.IsNoMatch, omission.ReasonCode, job.Item.Variant);
                    else if (!omission.IsNoMatch)
                        job.Item.Thread.ReportMediaFailure(job.Item.MediaIdentifier, omission, job.Item.DestinationPath);
                }
            }
            catch (Exception persistenceFailure) { Quarantine(job.Item, persistenceFailure); }
        }
        catch (Exception exception) when (DownloadTargetGate.IsTargetFailure(exception))
        {
            // Storage waits are not network attempts. Keep DueAt, 404 count and source-end deadline intact.
            targetBlocked = true;
            _targets.Block(TargetPath(job.Item), exception);
        }
        catch (Exception exception)
        {
            bool current = JournalFor(job.Item) == null;
            try { current |= RecordDurableResult(job.Item, exception, admission: producerAdmission); }
            catch (Exception persistenceFailure)
            {
                Quarantine(job.Item, persistenceFailure);
            }
            externalReported |= current;
            if (current) try
            {
                if (job.Item.Thread is Boards.Base.BaseThread thread)
                    thread.ReportMediaFailure(job.Item.MediaIdentifier, exception, job.Item.DestinationPath, job.Item.Variant);
                else job.Item.Thread.ReportMediaFailure(job.Item.MediaIdentifier, exception, job.Item.DestinationPath);
            }
            catch (Exception notificationException)
            {
                _logger.Error(notificationException, "Could not report media failure for {MediaId}", job.Item.MediaIdentifier);
            }
            _logger.Error(exception, "Error downloading {DownloadUri}", job.Item.DownloadUri);
        }
        finally
        {
            if (succeeded && job.Item.Thread is IExternalMediaWorkOwner external)
            {
                // The external owner holds its own durable admission; it never becomes a journal row.
                try { external.OnMediaCommitted(job.Item, committed!, producerAdmission); }
                catch (Exception ownerFailure)
                {
                    _logger.Error(ownerFailure, "External media owner could not record committed {MediaId}", job.Item.MediaIdentifier);
                }
            }
            else if (succeeded)
            {
                try
                {
                    if (RecordDurableResult(job.Item, null, committed, admission: producerAdmission))
                    {
                        if (job.Item.Thread is Boards.Base.BaseThread thread)
                            thread.MarkMediaAsDownloaded(job.Item.MediaIdentifier, job.Item.DestinationPath, job.Item.Variant);
                        else job.Item.Thread.MarkMediaAsDownloaded(job.Item.MediaIdentifier, job.Item.DestinationPath);
                    }
                }
                catch (Exception persistenceFailure)
                {
                    Quarantine(job.Item, persistenceFailure);
                }
                await CarryMediaTagsAsync(job.Item, committed, producerAdmission).ConfigureAwait(false);
            }
            Cleanup cleanup = new Cleanup();
            List<QueuedDownload> ready;
            // Read the durable 404 deadline before taking the state lock; it is only applied if still needed.
            MediaWork? preservedDeadline = targetBlocked ? LoadBlockedDeadline(job.Item, producerAdmission) : null;
            ExternalMediaCancellation? externalEnd = null;
            lock (_stateLock)
            {
                _activeDownloads--;
                if (job.Item.Filter != null) _activeFilteredDownloads--;
                if (_activeDownloads == 0) _workersDrained.TrySetResult();
                if ((interrupted || targetBlocked) && !_stopping && !job.ThreadState.Token.IsCancellationRequested
                    && !_cancelledThreads.TryGetValue(job.Item.Thread, out _))
                {
                    job.IsActive = false;
                    if (job.Replacement != null)
                    {
                        DetachFilter(job);
                        job.Item = job.Replacement;
                        AttachFilter(job);
                        job.Replacement = null;
                        _reservedSuccessors--;
                    }
                    _interruptedDownloads.AddLast(job);
                    if (preservedDeadline != null) PreserveBlockedDeadlineLocked(job.Item, preservedDeadline);
                    ready = ReserveWorkersLocked();
                }
                else
                {
                    _jobs.Remove(job.Identity);
                    DetachFilter(job);
                    job.ThreadState.OutstandingJobs--;
                    if (job.Replacement != null) _reservedSuccessors--;
                    // The job is gone without a commit or reported failure (cancel, stop, a pause or target
                    // wait overtaken by a stop). Only an explicit owner cancel is terminal for its request.
                    if (!succeeded && !externalReported && job.Item.Thread is IExternalMediaWorkOwner)
                        externalEnd = _cancelledThreads.TryGetValue(job.Item.Thread, out _)
                            ? ExternalMediaCancellation.Cancelled
                            : ExternalMediaCancellation.Stopped;
                    if (job.Replacement is DownloadItem replacement && !_stopping && !job.ThreadState.Token.IsCancellationRequested
                        && !_cancelledThreads.TryGetValue(replacement.Thread, out _))
                    {
                        QueuedDownload successor = new QueuedDownload(job.Identity, replacement.DownloadUri.ToString(), replacement, job.ThreadState);
                        _jobs.Add(job.Identity, successor);
                        AttachFilter(successor);
                        _pendingDownloads.AddLast(successor);
                        job.ThreadState.OutstandingJobs++;
                    }
                    RetireThreadIfIdleLocked(job.ThreadState, cleanup);
                    SignalIdleIfNeededLocked(cleanup);
                    ready = ReserveWorkersLocked();
                }
            }

            // Notify before thread retirement completes, so a finished cancel or stop implies a notified owner.
            if (externalEnd is ExternalMediaCancellation reason)
                NotifyExternalCancelled(job.Item, reason, producerAdmission);
            else if ((succeeded || externalReported) && job.Item.Thread is IExternalMediaWorkOwner released)
            {
                try { released.OnMediaReleased(job.Item); }
                catch (Exception ownerFailure)
                {
                    _logger.Error(ownerFailure, "External media owner could not release {MediaId}", job.Item.MediaIdentifier);
                }
            }
            RunCleanup(cleanup);
            StartWorkers(ready);
            _changes.Signal();
        }
    }

    private void NotifyExternalCancelled(DownloadItem item, ExternalMediaCancellation reason, ProfileAdmissionToken? admission = null)
    {
        if (item.Thread is not IExternalMediaWorkOwner external) return;
        try { external.OnMediaCancelled(item, reason, admission); }
        catch (Exception ownerFailure)
        {
            _logger.Error(ownerFailure, "External media owner could not record cancelled {MediaId}", item.MediaIdentifier);
        }
    }

    private MediaWork? LoadBlockedDeadline(DownloadItem item, ProfileAdmissionToken? admission = null)
    {
        IWorkStateStore? store = JournalFor(item);
        WorkResource owner;
        lock (_stateLock)
        {
            if (store == null || !_workOwners.TryGetValue(item.Thread, out WorkResource? found) || !ItemOwnerCurrentLocked(item, found)) return null;
            owner = found;
        }
        try
        {
            using (EnterDurable(item, admission, continuation: true))
            {
                MediaWork? work = FindMedia(store, owner, item);
                return work is { HttpStatus: 404, ManualAttempt: false } && SameRetrieval(work, item) ? work : null;
            }
        }
        catch (Exception failure) { Quarantine(item, failure); return null; }
    }

    private void PreserveBlockedDeadlineLocked(DownloadItem item, MediaWork work)
    {
        if (!SameRetrieval(work, item) || !_workOwners.TryGetValue(item.Thread, out WorkResource? owner)
            || !ItemOwnerCurrentLocked(item, owner)) return;
        _scheduled[Prepare(item).Job] = new ScheduledWork(item, work);
        SignalScheduleLocked();
    }

    private bool RecordFilterOmission(DownloadItem item, MediaFilterOmissionException omission, ProfileAdmissionToken? admission = null)
    {
        using (EnterDurable(item, admission, continuation: true))
        {
            IWorkStateStore? store = JournalFor(item);
            if (store == null) return true;
            WorkResource? owner;
            lock (_stateLock)
            {
                if (!_workOwners.TryGetValue(item.Thread, out owner) || !ItemOwnerCurrentLocked(item, owner)) return false;
            }
            MediaWork? previous = FindMedia(store, owner, item);
            if (previous == null || !SameRetrieval(previous, item)) return false;
            if (!WorkStateAccess.For(store).TryCommitMedia(previous with
            {
                Status = omission.IsNoMatch ? MediaWorkStatus.Excluded : MediaWorkStatus.NeedsIntervention,
                Failure = omission.ReasonCode, DueAt = null, ManualAttempt = false
            }, previous, admission)) return false;
            lock (_stateLock) if (!ItemOwnerCurrentLocked(item, owner)) return false;
        }
        _changes.Signal();
        return true;
    }

    private void Quarantine(DownloadItem item, Exception failure)
    {
        lock (_stateLock)
        {
            if (JournalFor(item) != null && (!_workOwners.TryGetValue(item.Thread, out WorkResource? owner) || !ItemOwnerCurrentLocked(item, owner))) return;
            if (_jobs.TryGetValue(Prepare(item).Job, out QueuedDownload? job) && job.Replacement != null && !SameRetrieval(job.Replacement, item)) return;
            _quarantined.Add(Prepare(item).Job); _scheduled.Remove(Prepare(item).Job);
        }
        string warning = PersistenceWarning(item.MediaIdentifier, failure);
        _logger.Error(failure, "{Warning}", warning);
        RaisePersistenceFailed(warning);
        _changes.Signal();
    }

    internal static string PersistenceWarning(string mediaIdentifier, Exception failure)
    {
        if (failure is SqliteException { SqliteErrorCode: 10 } sqlite)
            return $"Media work for {mediaIdentifier} could not be committed because the work journal reported SQLite I/O error 10/{sqlite.SqliteExtendedErrorCode}. Automatic retries are suspended to protect retry state. Check free space, profile folder write access, and whether its filesystem is available and writable.";
        return $"Media work for {mediaIdentifier} could not be committed: {failure.Message}. Automatic retries are suspended.";
    }

    // A throwing subscriber must not abort the worker, schedule or quarantine path that reports the failure.
    private void RaiseMediaTagsCarriedOver()
    {
        try { MediaTagsCarriedOver?.Invoke(); }
        catch (Exception observer) { _logger.Error(observer, "Download queue media tag observer failed"); }
    }

    private void RaisePersistenceFailed(string warning)
    {
        try { PersistenceFailed?.Invoke(warning); }
        catch (Exception observer) { _logger.Error(observer, "Download queue failure observer failed"); }
    }

    private bool CanRunDurable(DownloadItem item, ProfileAdmissionToken? admission = null)
    {
        using (EnterDurable(item, admission, continuation: true)) return CanRunDurableLocked(item, admission);
    }

    private bool CanRunDurableLocked(DownloadItem item, ProfileAdmissionToken? admission = null)
    {
        IWorkStateStore? store = JournalFor(item);
        if (store == null) return true;
        WorkResource owner;
        lock (_stateLock)
        {
            if (_quarantined.Contains(Prepare(item).Job)) return false;
            if (!_workOwners.TryGetValue(item.Thread, out WorkResource? found) || !ItemOwnerCurrentLocked(item, found)) return false;
            owner = found;
        }
        WorkResource? current = WorkStateAccess.For(store).FindResource(owner.Url);
        if (current == null || current.Generation != owner.Generation || current.Intent == WorkIntent.Suppressed) return false;
        MediaWork? media = FindMedia(store, owner, item);
        if (media == null || !SameRetrieval(media, item)) return false;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (!media.ManualAttempt && media.HttpStatus == 404 && current.EndObservedAt is DateTimeOffset end
            && now >= end.AddHours(12))
        {
            WorkStateAccess.For(store).TryCommitMedia(media with { Status = MediaWorkStatus.Expired, DueAt = null }, media, admission);
            _changes.Signal();
            return false;
        }
        lock (_stateLock) if (!ItemOwnerCurrentLocked(item, owner)) return false;
        return media.Status is not (MediaWorkStatus.NeedsIntervention or MediaWorkStatus.Expired or MediaWorkStatus.Excluded)
            && (media.DueAt == null || media.DueAt <= now);
    }

    private bool RecordDurableResult(DownloadItem item, Exception? failure, MediaCommitResult? committed = null, ProfileAdmissionToken? admission = null)
    {
        using (EnterDurable(item, admission, continuation: true)) return RecordDurableResultLocked(item, failure, committed, admission);
    }

    private bool RecordDurableResultLocked(DownloadItem item, Exception? failure, MediaCommitResult? committed, ProfileAdmissionToken? admission = null)
    {
        IWorkStateStore? store = JournalFor(item);
        if (store == null) return false;
        WorkResource owner;
        lock (_stateLock)
        {
            if (!_workOwners.TryGetValue(item.Thread, out WorkResource? found) || !ItemOwnerCurrentLocked(item, found)) return false;
            owner = found;
        }
        WorkResource? resource = WorkStateAccess.For(store).FindResource(owner.Url);
        if (resource == null || resource.Generation != owner.Generation || resource.Intent == WorkIntent.Suppressed) return false;
        MediaWork? previous = FindMedia(store, owner, item);
        if (previous == null || !SameRetrieval(previous, item)) return false;
        DateTimeOffset now = _timeProvider.GetUtcNow();
        int attempt = previous.Attempts + 1;
        int? status = (failure as HttpRequestException)?.StatusCode is HttpStatusCode http ? (int)http : null;
        int missing = status == 404 ? previous.ConsecutiveNotFound + 1 : 0;
        MediaWork next;
        if (failure == null)
        {
            if (committed?.AssetId == null || committed.AcquisitionRevisionKey != item.AcquisitionRevisionKey
                || !SameReferences(committed.AttachmentRevisionKeys, item.AttachmentReferences.Select(reference => reference.RevisionKey)))
                throw new InvalidDataException("Media completion lacks the exact committed revision receipt.");
            next = previous with { Status = MediaWorkStatus.Succeeded, Attempts = 0, ConsecutiveNotFound = 0,
                LastAttemptAt = now, DueAt = null, Failure = null, HttpStatus = null, ManualAttempt = false, CommittedAssetId = committed.AssetId };
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
            bool expired = status == 404 && resource.EndObservedAt is DateTimeOffset end && now >= end.AddHours(12);
            next = previous with { Status = intervention ? MediaWorkStatus.NeedsIntervention
                    : expired ? MediaWorkStatus.Expired : MediaWorkStatus.Waiting,
                Attempts = attempt, ConsecutiveNotFound = missing, LastAttemptAt = now,
                DueAt = expired ? null : due, Failure = failure.Message, HttpStatus = status, ManualAttempt = false };
        }
        if (!WorkStateAccess.For(store).TryCommitMedia(next, previous, admission)) return false;
        PerformanceDiagnostics.Record("queue.worker.result", next.Status.ToString(), request: item.WorkOperationId,
            watch: owner.Url, asset: item.AcquisitionRevisionKey ?? item.MediaIdentifier + ":" + item.Variant, epoch: owner.Generation);
        if (next.Status == MediaWorkStatus.Waiting && next.DueAt != null)
        {
            lock (_stateLock)
            {
                // The commit ran outside the state lock; a removed or replaced owner must not receive a schedule entry.
                if (OwnerStillCurrentLocked(item.Thread, owner)) { _scheduled[Prepare(item).Job] = new ScheduledWork(item, next); SignalScheduleLocked(); }
            }
        }
        lock (_stateLock) if (!ItemOwnerCurrentLocked(item, owner)) return false;
        _changes.Signal();
        return true;
    }

    /// <summary>Removes queued jobs of the thread; returns external items the caller notifies outside the lock.</summary>
    private List<DownloadItem> RemovePendingJobsLocked(ThreadState threadState)
    {
        List<DownloadItem> cancelledExternal = new();
        foreach (QueuedDownload interrupted in _interruptedDownloads.Where(job => ReferenceEquals(job.ThreadState, threadState)).ToArray())
        {
            _interruptedDownloads.Remove(interrupted);
            _jobs.Remove(interrupted.Identity);
            DetachFilter(interrupted);
            threadState.OutstandingJobs--;
            if (interrupted.Item.Thread is IExternalMediaWorkOwner) cancelledExternal.Add(interrupted.Item);
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
                DetachFilter(job);
                threadState.OutstandingJobs--;
                if (job.Item.Thread is IExternalMediaWorkOwner) cancelledExternal.Add(job.Item);
            }
            node = next;
        }
        PulseCapacityLocked();
        // A removed job never reaches a worker, so it would never report a cancellation itself.
        return cancelledExternal;
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

    private async Task CompleteStopAsync(List<ThreadState> threads, TaskCompletionSource completion, Task waiterCancellation)
    {
        try
        {
            await waiterCancellation.ConfigureAwait(false);
            List<Task> cancellations = new List<Task>(threads.Count);
            foreach (ThreadState threadState in threads)
            {
                cancellations.Add(CancelThreadStateAsync(threadState));
            }

            await Task.WhenAll(cancellations).ConfigureAwait(false);
            await WaitForIdleAsync().ConfigureAwait(false);
            if (_scheduleTask != null) await _scheduleTask.ConfigureAwait(false);
            Task durableDrained;
            lock (_stateLock) durableDrained = _durableDrained.Task;
            await durableDrained.ConfigureAwait(false);
            if (_targetPolling != null) await _targetPolling.ConfigureAwait(false);
            if (_ownsProducerEpoch && _producerEpoch is { } epoch)
            {
                await epoch.Close().ConfigureAwait(false);
                epoch.Dispose();
                _producerEpoch = null;
            }
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

    private static bool SameMediaWork(MediaWork work, DownloadItem item) =>
        work.MediaIdentifier == item.MediaIdentifier && work.Variant == item.Variant && work.DestinationPath == item.DestinationPath;

    private static bool SameMediaWork(MediaWork first, MediaWork second) =>
        first.MediaIdentifier == second.MediaIdentifier && first.Variant == second.Variant && first.DestinationPath == second.DestinationPath;

    private static bool SameRetrieval(DownloadItem first, DownloadItem second) =>
        first.ExpectedSize == second.ExpectedSize
        && string.Equals(first.ExpectedMd5, second.ExpectedMd5, StringComparison.Ordinal)
        && first.ExpectedSha256 == second.ExpectedSha256 && first.ExpectedSha1 == second.ExpectedSha1 && first.ProviderSizeHint == second.ProviderSizeHint && first.DownloadUri == second.DownloadUri
        && first.OriginalFileName == second.OriginalFileName && first.AcquisitionRevisionKey == second.AcquisitionRevisionKey
        && SameReferences(first.AttachmentReferences.Select(reference => reference.RevisionKey), second.AttachmentReferences.Select(reference => reference.RevisionKey));

    private static bool SameRetrieval(MediaWork first, DownloadItem second) => first.ExpectedSize == second.ExpectedSize
        && first.ExpectedMd5 == second.ExpectedMd5 && first.ExpectedSha256 == second.ExpectedSha256 && first.ExpectedSha1 == second.ExpectedSha1 && first.ProviderSizeHint == second.ProviderSizeHint
        && first.DownloadUrl == second.DownloadUri.AbsoluteUri && first.OriginalFileName == second.OriginalFileName
        && first.AcquisitionRevisionKey == second.AcquisitionRevisionKey
        && SameReferences(first.AttachmentReferences.Select(reference => reference.RevisionKey), second.AttachmentReferences.Select(reference => reference.RevisionKey));

    private static bool SameReferences(IEnumerable<string> first, IEnumerable<string> second) =>
        first.OrderBy(key => key, StringComparer.Ordinal).SequenceEqual(second.OrderBy(key => key, StringComparer.Ordinal));

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
    private sealed class AdmissionWaiter(DownloadItem item)
    {
        public DownloadItem Item { get; } = item;
        public IThread Thread => Item.Thread;
        public bool TargetReady { get; set; }
        public CancellationTokenSource Closed { get; } = new();
    }
    private readonly record struct PreparedDownload(ThreadIdentity Thread, JobIdentity Job);

    private readonly record struct ThreadIdentity(string Source, string Board, string ThreadId);
    private readonly record struct JobIdentity(ThreadIdentity Thread, string MediaId, string Variant, string DestinationPath);

    private sealed class QueuedDownload
    {
        public JobIdentity Identity { get; }
        public string Url => Item.DownloadUri.ToString();
        public DownloadItem Item { get; set; }
        public DownloadItem? Replacement { get; set; }
        public ThreadState ThreadState { get; }
        public bool IsActive { get; set; }
        public IOriginalMediaFilter? SubscribedFilter { get; set; }
        public CancellationToken EpochToken { get; set; }
        public ProfileProducerEpoch.Use? ProducerUse { get; set; }
        public ProfileAdmissionToken? ProducerAdmission => ProducerUse?.Admission;

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
