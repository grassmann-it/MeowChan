using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
using MeowChan.Interfaces;

namespace MeowChan.Downloader;

public partial class DownloadQueue
{
    private ProfileProducerEpoch? _producerEpoch;
    private bool _ownsProducerEpoch;
    private bool _producerSuspended;
    private bool _producerClosed;
    private TaskCompletionSource _producerResumed = CreateCompletedSource();

    internal void PrepareProfileProducers()
    {
        lock (_stateLock)
        {
            if (_producerClosed || _stopping) throw new InvalidOperationException("Queue is stopping.");
            if (_producerEpoch != null) throw new InvalidOperationException("Queue producers already activated.");
            _producerSuspended = true;
            _producerResumed = CreateCompletionSource();
        }
    }

    internal void InstallProfileEpoch(ProfileProducerEpoch epoch)
    {
        lock (_stateLock)
        {
            if (_stopping || _producerClosed) throw new InvalidOperationException("Queue is stopping.");
            if (_producerEpoch != null && !ReferenceEquals(_producerEpoch, epoch))
                throw new InvalidOperationException("The old queue epoch must drain first.");
            _producerEpoch = epoch;
        }
    }

    internal void RevalidateProfileWork()
    {
        KeyValuePair<MeowChan.Interfaces.IThread, WorkResource>[] owners;
        lock (_stateLock) owners = new List<KeyValuePair<MeowChan.Interfaces.IThread, WorkResource>>(_workOwners).ToArray();
        foreach (var owner in owners) BindWork(owner.Key, owner.Value);
    }

    internal void ResumeProfileProducers()
    {
        List<QueuedDownload> ready;
        lock (_stateLock)
        {
            if (_stopping || _producerClosed) throw new InvalidOperationException("Queue is stopping.");
            _producerSuspended = false;
            _transferEpoch = new CancellationTokenSource();
            _producerResumed.TrySetResult();
            PulseCapacityLocked();
            SignalScheduleLocked();
            if (_targetPolling == null && _targets.NextCheckAt != null) _targetPolling = Task.Run(PollTargetsAsync);
            ready = ReserveWorkersLocked();
        }
        StartWorkers(ready);
    }

    // Close execution atomically with worker/scheduler reservation. Cancellation and
    // monitor drain happen outside state locks. Logical capacity waiters stay parked.
    internal void CloseProfileProducerIngress()
    {
        lock (_stateLock)
        {
            _producerClosed = true; // Permanent Host/Registry close must win a late activation.
            if (!_producerSuspended) _producerResumed = CreateCompletionSource();
            _producerSuspended = true;
            _scheduleChanged.TrySetResult();
            _targetChanged.TrySetResult();
            PulseCapacityLocked();
        }
    }

    internal async Task SuspendProfileProducersAsync()
    {
        await _pauseTransitions.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource? transfer;
            lock (_stateLock)
            {
                if (!_producerSuspended) _producerResumed = CreateCompletionSource();
                _producerSuspended = true;
                transfer = _paused ? null : _transferEpoch;
                _scheduleChanged.TrySetResult();
                _targetChanged.TrySetResult();
                PulseCapacityLocked();
            }
            if (transfer != null) await transfer.CancelAsync().ConfigureAwait(false);
        }
        finally { _pauseTransitions.Release(); }
        // The shared epoch drain, NOT _durableDrained/_workersDrained, includes
        // physical-worker finally/notifications and excludes parked enqueue callers.
    }

    internal void DetachProfileEpoch(ProfileProducerEpoch epoch)
    {
        lock (_stateLock)
        {
            if (!ReferenceEquals(_producerEpoch, epoch)) throw new InvalidOperationException("Queue epoch mismatch.");
            _producerEpoch = null;
        }
    }

    private ProfileProducerEpoch.Use? EnterProducerLocked()
    {
        if (_producerSuspended || _producerClosed || _stopping) throw new ProducerSuspendedException();
        if (_producerEpoch == null && _workStore is WorkStateStore sqlite)
        {
            _producerEpoch = new(sqlite.ProfileLease.Admission.EnterStoreWriter());
            _ownsProducerEpoch = true;
        }
        return _producerEpoch?.Enter();
    }

    // Deterministic park observations; never invoked under the queue state lock.
    internal Action? BeforeProducerPark { get; set; }
    internal Action? BeforeThreadProducerPark { get; set; }

    private async Task<bool> WaitForProducerAsync(CancellationToken token, IThread? thread = null)
    {
        while (true)
        {
            // The first enqueue park has no AdmissionWaiter/Closed token yet.
            // Observe instance closure and its capacity pulse under the same lock;
            // no executing epoch use or durable admission is held across the park.
            if (thread != null) token.ThrowIfCancellationRequested();
            Task resumed;
            Task? changed;
            lock (_stateLock)
            {
                if (_stopping) throw new QueueStoppingException(token);
                if (thread != null && _cancelledThreads.TryGetValue(thread, out _)) return false;
                if (!_producerSuspended) return true;
                resumed = _producerResumed.Task;
                changed = thread != null ? _capacityChanged.Task : null;
            }
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
            BeforeProducerPark?.Invoke();
            if (thread != null) BeforeThreadProducerPark?.Invoke();
            await (changed == null ? resumed : Task.WhenAny(resumed, changed)).WaitAsync(linked.Token).ConfigureAwait(false);
        }
    }
    private sealed class ProducerSuspendedException() : InvalidOperationException("Queue producer execution is suspended.");
}
