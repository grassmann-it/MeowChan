using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan;

public sealed partial class Registry
{
    private ProfileLease? _producerLease;
    private ProfileProducerEpoch? _producerEpoch;
    private readonly SemaphoreSlim _producerTransitions = new(1, 1);
    private bool _producerPrepared;
    private bool _producerSuspended;
    private bool _producerIngress = true;
    private volatile bool _producerStopRequested;
    private bool ProducersMayRun { get { lock (_state) return !_producerSuspended && !_producerStopRequested && !_stopping && !_queue.IsPaused; } }

    // Host explicitly binds even foreign stores. Standalone composition discovers
    // only real built-in leases, never paths/decorators or an ambient token.
    internal void PrepareProfileProducers(ProfileLease lease)
    {
        lock (_state)
        {
            if (_producerEpoch != null || _restored) throw new InvalidOperationException("Registry already activated.");
            _producerLease = lease;
            _producerPrepared = true;
            _producerSuspended = true;
            _producerIngress = false;
            _queue.PrepareProfileProducers();
        }
    }

    private ProfileLease? BoundProfileLease => _producerLease ?? (_workStore as WorkStateStore)?.ProfileLease
        ?? (_sessionStore as SessionStore)?.ProfileLease ?? _boardAdmission?.ProfileLease;

    private void ValidateRegistryAdmission(ProfileAdmissionToken? admission)
    {
        ProfileLease? lease = BoundProfileLease;
        if (lease != null)
            lease.Admission.RunWithToken(admission!, () => true, ProfileAdmissionKind.Startup, ProfileAdmissionKind.Ordinary);
    }

    private ProfileAdmissionToken? EnterRegistryWriter()
    {
        lock (_state)
        {
            ObjectDisposedException.ThrowIf(_producerStopRequested || _stopping, this);
            _producerLease ??= BoundProfileLease;
            if (!_producerPrepared && !_producerSuspended && _producerEpoch == null)
            {
                ProfileProducerEpoch epoch = new(_producerLease?.Admission.EnterStoreWriter());
                try { _queue.InstallProfileEpoch(epoch); _producerEpoch = epoch; }
                catch { epoch.Close(); epoch.Dispose(); throw; }
            }
            return _producerLease?.Admission.EnterStoreWriter();
        }
    }

    // Synchronous installation is ordered with Host lifecycle/seal. No producer is
    // opened here; asynchronous activation must still win against Stop.
    internal void InstallReadyProfileEpoch()
    {
        lock (_state)
        {
            if (_producerStopRequested || _stopping) throw new InvalidOperationException("Registry is stopping.");
            if (_producerEpoch != null) throw new InvalidOperationException("Registry epoch already installed.");
            ProfileAdmissionToken admission = (_producerLease ?? throw new InvalidOperationException("No bound profile."))
                .Admission.EnterOrdinaryWriter();
            ProfileProducerEpoch epoch = new(admission);
            try { _queue.InstallProfileEpoch(epoch); _producerEpoch = epoch; }
            catch { epoch.Close(); epoch.Dispose(); throw; }
        }
    }

    // Deterministic lifecycle seams, invoked outside the lifecycle/state monitors.
    internal Func<Task>? BeforeProfileProducerResume { get; set; }

    internal async Task ActivatePreparedProfileProducersAsync(Action? sealBeforeFailureDrain = null)
    {
        Watch[] failedWatches = Array.Empty<Watch>();
        try
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (_state)
                {
                    if (_producerStopRequested || _stopping) throw new InvalidOperationException("Registry is stopping.");
                    _producerSuspended = false;
                    _producerIngress = true;
                    _producerPrepared = false;
                }
                WorkStateAccess.For(_workStore!).InvalidateAll();
                RefreshWorkCache();
                // Replay durable Ready/Waiting descriptors whose canceled monitor or
                // parked admission did not yet publish a logical queue reservation.
                _queue.RevalidateProfileWork();
                // Callback ownership must advance even for user-paused watches.
                // Old captured closures still fail the exact-generation Track check.
                foreach (var entry in _watches.ToArray())
                    if (entry.Value.Ready && !entry.Value.Stopping)
                        BindWatchOwner(entry.Key, entry.Value, _producerEpoch);
                // Dispatch opens only after the new epoch has been installed.
                _queue.ResumeProfileProducers();
                foreach (var entry in _watches.ToArray())
                    if (entry.Value.Ready && !entry.Value.Stopping && ProducersMayRun)
                        StartWatchProducer(entry.Key, entry.Value);
            }
            catch
            {
                // A partial activation must not leave fresh monitors/dispatch running.
                // Keep the counted epoch until actual descendant drain (or Stop).
                lock (_state)
                {
                    _producerSuspended = true;
                    _producerIngress = false;
                    failedWatches = _watches.Values.ToArray();
                }
                throw;
            }
            finally { _gate.Release(); }
        }
        catch
        {
            // Startup's host must seal new profile writers before any rollback
            // cancellation/drain can block. Owner-resume deliberately supplies no
            // seal: its temporarily suspended family remains retryable.
            sealBeforeFailureDrain?.Invoke();
            // Provider callbacks can need _gate: cancellation/drain is outside it.
            await _queue.SuspendProfileProducersAsync().ConfigureAwait(false);
            await Task.WhenAll(failedWatches.Select(StopWatchProducerAsync)).ConfigureAwait(false);
            throw;
        }
        SignalChanged();
    }

    private IDisposable RegisterWatchOwner(Resource resource, Watch watch, ProfileProducerEpoch? epoch)
        => resource.Thread != null
            ? resource.Thread.RegisterRemovalSink(removed =>
            {
                if (Interlocked.Exchange(ref watch.RemovalRequested, 1) != 0) return;
                if (!Track(async admission =>
                {
                    try { await RemoveThreadAdmittedAsync(removed, admission).ConfigureAwait(false); }
                    finally { Volatile.Write(ref watch.RemovalRequested, 0); }
                }, epoch)) Volatile.Write(ref watch.RemovalRequested, 0);
            })
            : resource.Board!.RegisterDiscoverySink((thread, token) =>
                new ValueTask(AdmitDiscoveryAsync(watch, thread, token, epoch?.Admission)));

    private void BindWatchOwner(ResourceKey key, Watch watch, ProfileProducerEpoch? epoch)
    {
        watch.OwnerRegistration?.Dispose();
        Resource resource = new(key, watch.Thread?.ThreadUri.AbsoluteUri ?? watch.Board!.BoardUri.AbsoluteUri, watch.Thread, watch.Board);
        watch.OwnerRegistration = RegisterWatchOwner(resource, watch, epoch);
    }

    // Caller holds _gate. Each callback closes over THIS generation, not the next
    // value of _producerEpoch. The monitor lifetime remains counted through Stop.
    private void StartWatchProducer(ResourceKey key, Watch watch)
    {
        ProfileProducerEpoch? epoch;
        ProfileProducerEpoch.Use? use;
        lock (_state)
        {
            if (!ProducersMayRun || watch.ProducerLifetime != null) return;
            epoch = _producerEpoch;
            if (epoch == null && BoundProfileLease != null) throw new InvalidOperationException("No profile producer epoch installed.");
            use = epoch?.Enter();
            watch.ProducerLifetime = use;
        }
        // No profile-bound epoch means an explicitly volatile standalone Registry;
        // retain its historical provider lifecycle without inventing authority.
        try
        {
            BindWatchOwner(key, watch, epoch);
            if (watch.Thread is BaseThread thread)
                thread.SetLifecycleSinks((state, observed, snapshot, token) =>
                    CommitSourceEndAsync(key, thread, state, observed, snapshot, token, use?.Admission),
                    () => CommitCaptureSettledAsync(key, thread, use?.Admission));
            if (watch.Thread != null) watch.Thread.StartMonitoring(_queue, watch.OutputRoot, _threadInterval);
            else watch.Board!.StartMonitoring((int)_boardInterval.TotalSeconds);
        }
        catch { throw; }
    }

    private async Task StopWatchProducerAsync(Watch watch)
    {
        ProfileProducerEpoch.Use? lifetime = Volatile.Read(ref watch.ProducerLifetime);
        if (watch.Thread != null) await watch.Thread.StopMonitoringAsync().ConfigureAwait(false);
        else await watch.Board!.StopMonitoringAsync().ConfigureAwait(false);
        // Two accepted pause/owner-stop operations can await the same old monitor.
        // A late continuation must never detach a successor generation's lifetime.
        // Only successful drain is evidence that this generation's callbacks ended.
        if (lifetime != null && ReferenceEquals(Interlocked.CompareExchange(ref watch.ProducerLifetime, null, lifetime), lifetime))
            lifetime.Dispose();
    }

    internal void CloseProducerIngress()
    {
        lock (_state)
        {
            _producerStopRequested = true;
            _producerIngress = false;
            _producerSuspended = true;
        }
        _queue.CloseProfileProducerIngress();
    }

    private async Task ReleaseStoppedEpochAsync()
    {
        // Stop can overlap suspension. The owner operation observes/relinquishes
        // any reserved Exclusive before Stop releases this family's final epoch.
        await _producerTransitions.WaitAsync().ConfigureAwait(false);
        try
        {
            ProfileProducerEpoch? epoch;
            lock (_state) epoch = _producerEpoch;
            if (epoch == null) return;
            await epoch.Close().ConfigureAwait(false);
            _queue.DetachProfileEpoch(epoch);
            epoch.Dispose();
            lock (_state) _producerEpoch = null;
        }
        finally { _producerTransitions.Release(); }
    }

    // Owner-only test/coordinator entry. Never invoke from an ordinary command or
    // while holding Host._commands: the reservation waits for earlier commands.
    // This is not a private Apply API or a consent capability.
    internal async Task RunWithProfileExclusiveAsync(Func<ProfileAdmissionToken, Task> operation,
        CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _producerTransitions.WaitAsync(token).ConfigureAwait(false);
        using CancellationTokenSource reservationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ProfileAdmissionToken>? reservation = null;
        ProfileAdmissionToken? exclusive = null;
        ProfileProducerEpoch? epoch = null;
        bool quiescent = false;
        try
        {
            lock (_state)
            {
                if (_producerStopRequested || _stopping) throw new InvalidOperationException("Registry is stopping.");
                epoch = _producerEpoch;
                // A failed fresh-Ordinary acquisition leaves a genuinely quiescent,
                // retained family. Retrying this same owner lane must be possible.
                if (epoch == null && !_producerSuspended)
                    throw new InvalidOperationException("No live producer epoch.");
                _producerLease ??= BoundProfileLease;
                // Reservation refusal leaves the live generation completely intact.
                reservation = (_producerLease ?? BoundProfileLease ?? throw new InvalidOperationException("No profile."))
                    .Admission.EnterExclusiveAsync(reservationCancellation.Token).AsTask();
                _producerIngress = false;
                _producerSuspended = true;
            }
            if (epoch != null)
            {
                await _queue.SuspendProfileProducersAsync().ConfigureAwait(false);
                Watch[] watches;
                await _gate.WaitAsync().ConfigureAwait(false);
                try { lock (_state) watches = _watches.Values.Where(watch => !watch.Stopping).ToArray(); }
                finally { _gate.Release(); }
                await Task.WhenAll(watches.Select(StopWatchProducerAsync)).ConfigureAwait(false);
                await epoch.Close().ConfigureAwait(false);
                await DrainBackgroundTasksAsync().ConfigureAwait(false);
                // Registration is sealed, queue callbacks drained. One final pass owns
                // the old reservation; late invalidations remain dirty for resumption.
                await TryCompleteAsync(epoch.Admission).ConfigureAwait(false);
                _queue.DetachProfileEpoch(epoch);
                epoch.Dispose();
                lock (_state) _producerEpoch = null;
            }
            quiescent = true;
            Task<ProfileAdmissionToken> acceptedReservation = reservation;
            reservation = null;
            exclusive = await acceptedReservation.ConfigureAwait(false); // Exactly one observation.
            token.ThrowIfCancellationRequested();
            if (_producerStopRequested) throw new InvalidOperationException("Registry is stopping.");
            await operation(exclusive).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                try
                {
                    // On drain failure retain the old epoch; no false Exclusive success.
                    // Observe the pending reservation even if cancellation/shutdown itself fails.
                    if (reservation != null)
                    {
                        try { await reservationCancellation.CancelAsync().ConfigureAwait(false); }
                        finally
                        {
                            try { exclusive = await reservation.ConfigureAwait(false); }
                            catch (OperationCanceledException) { }
                            catch (InvalidOperationException) when (_producerStopRequested) { }
                        }
                    }
                }
                finally { exclusive?.Dispose(); }
                if (quiescent && !_producerStopRequested && _producerLease?.Admission.Phase == ProfileAdmissionPhase.Ready)
                {
                    if (BeforeProfileProducerResume is { } beforeResume)
                        await beforeResume().ConfigureAwait(false);
                    InstallReadyProfileEpoch(); // Losing to another Exclusive stays suspended, and retryable.
                    await ActivatePreparedProfileProducersAsync().ConfigureAwait(false);
                }
            }
            finally { _producerTransitions.Release(); }
        }
    }
}
