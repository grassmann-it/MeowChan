using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Boards.Base;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan;

public sealed partial class Registry
{
    private ProfileLease? _producerLease;
    private ProfileProducerEpoch? _producerEpoch;
    private readonly SemaphoreSlim _producerTransitions = new(1, 1);
    private IProfileFinalWriteParticipant? _finalWriteParticipant;
    internal void RegisterProfileFinalWriteParticipant(IProfileFinalWriteParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        lock (_state)
        {
            if (!_producerPrepared || _restored || _finalWriteParticipant != null)
                throw new InvalidOperationException("Final-write participant must be registered once before startup.");
            _finalWriteParticipant = participant;
        }
    }
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
            if (_producerPrepared || _producerEpoch != null || _restored || _producerStopRequested || _stopping)
                throw new InvalidOperationException("Registry already prepared, activated or stopping.");
            ValidateStartupComposition(lease);
            _queue.ValidateProfileLeaseBinding(lease);
            _booruCatalog ??= new BooruCatalogStore(lease.ProfileDirectory, lease);
            lease.ClaimStartupOwner(this);
            _queue.BindProfileLease(lease);
            _producerLease = lease;
            _producerPrepared = true;
            _producerSuspended = true;
            _producerIngress = false;
            _queue.PrepareProfileProducers();
        }
    }

    private ProfileLease? BoundProfileLease => _producerLease ?? (_workStore as WorkStateStore)?.ProfileLease
        ?? (_sessionStore as SessionStore)?.ProfileLease ?? _boardAdmission?.ProfileLease
        ?? _contentRuntime?.ProfileLease ?? _booruCatalog?.ProfileLease;

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
            if (_standaloneStartup != null && !_standaloneStartupSucceeded)
                throw new InvalidOperationException("Await Registry.StartAsync before writing to this session.");
            _producerLease ??= BoundProfileLease;
            if (_producerLease != null) _queue.BindProfileLease(_producerLease);
            if (!_producerPrepared && !_producerSuspended && _producerEpoch == null)
            {
                // Diagnose the public migration without changing the prepared host path.
                if (_producerLease?.Admission.Phase == ProfileAdmissionPhase.Starting)
                    throw new InvalidOperationException("Await Registry.StartAsync(lease) before restoring or writing to a new profile-bound session.");
                // Lazy standalone producers also require Ready; a Startup command
                // must never lend its role to a monitor or physical media job.
                ProfileProducerEpoch epoch = new(_producerLease?.Admission.EnterOrdinaryWriter());
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
    internal Func<Task>? BeforeProfileProducerFailureDrain { get; set; }
    // Instance-only observation after kernel/Host sealing, before exclusive release.
    // Invoked outside all monitors; does not supply an outcome or new authority.
    internal Action<ProfileAdmissionToken>? AfterProfileExclusiveRecoverySealedForTests { get; set; }
    // Observe a granted owner before cancellation/Stop checks and delegate entry.
    internal Action<ProfileAdmissionToken>? BeforeProfileExclusiveOperationForTests { get; set; }

    internal async Task ActivatePreparedProfileProducersAsync(Action? sealBeforeFailureDrain = null,
        Action<Exception>? notifyBeforeFailureDrain = null)
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
        catch (Exception activationError)
        {
            // B2: observers must learn about a known failure BEFORE any rollback
            // await. Both hooks run outside _gate/_state; errors cannot skip drain.
            List<Exception> errors = new() { activationError };
            try { sealBeforeFailureDrain?.Invoke(); } catch (Exception error) { errors.Add(error); }
            try { notifyBeforeFailureDrain?.Invoke(activationError); } catch (Exception error) { errors.Add(error); }
            try
            {
                if (BeforeProfileProducerFailureDrain is { } beforeDrain) await beforeDrain().ConfigureAwait(false);
            }
            catch (Exception error) { errors.Add(error); }
            try { await _queue.SuspendProfileProducersAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
            // Start every stop even if another stop faults synchronously.
            Task[] stops = failedWatches.Select(async watch =>
            { await StopWatchProducerAsync(watch).ConfigureAwait(false); }).ToArray();
            try { await Task.WhenAll(stops).ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
            if (errors.Count == 1) throw;
            throw new AggregateException("Producer activation and rollback reported failures.", errors);
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
            // Only bound content threads need the admitted BaseThread entry.
            // Preserve IThread lifecycle dispatch for non-content providers.
            if (_contentRuntime != null && watch.Thread is BaseThread { SupportsContent: true } contentThread
                && use?.Admission is { } admission)
                contentThread.StartMonitoringAdmitted(admission, _queue, watch.OutputRoot, _threadInterval);
            else if (watch.Thread != null) watch.Thread.StartMonitoring(_queue, watch.OutputRoot, _threadInterval);
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

    internal async Task ReleaseStoppedHostFinalWritesAsync()
    {
        await _producerTransitions.WaitAsync().ConfigureAwait(false);
        try { _finalWriteParticipant?.ReleaseStoppedEpoch(); }
        finally { _producerTransitions.Release(); }
    }

    // This drain NEVER waits for Registry.StopAsync: its final epoch release needs
    // this same lane. A save failure is not a producer-lifetime completion.
    private async Task DrainProfileEpochAsync(ProfileProducerEpoch? epoch)
    {
        if (epoch == null) return;
        await _queue.SuspendProfileProducersAsync().ConfigureAwait(false);
        Watch[] watches;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { lock (_state) watches = _watches.Values.Where(watch => !watch.Stopping).ToArray(); }
        finally { _gate.Release(); }
        await Task.WhenAll(watches.Select(StopWatchProducerAsync)).ConfigureAwait(false);
        await epoch.Close().ConfigureAwait(false);
        await DrainBackgroundTasksAsync().ConfigureAwait(false);
        await TryCompleteAsync(epoch.Admission).ConfigureAwait(false);
    }
    private void ReleaseDrainedProfileEpoch(ProfileProducerEpoch? epoch)
    {
        if (epoch == null) return;
        lock (_state)
        {
            if (!ReferenceEquals(_producerEpoch, epoch)) throw new InvalidOperationException("Registry epoch changed during drain.");
        }
        _queue.DetachProfileEpoch(epoch);
        epoch.Dispose();
        lock (_state) _producerEpoch = null;
    }

    private async Task ResumeProfileFamiliesAsync()
    {
        try
        {
            if (BeforeProfileProducerResume is { } beforeResume) await beforeResume().ConfigureAwait(false);
            _finalWriteParticipant?.InstallReadyEpoch();
            InstallReadyProfileEpoch();
            await ActivatePreparedProfileProducersAsync(notifyBeforeFailureDrain: _finalWriteParticipant is { } participant ? participant.FailTransition : null).ConfigureAwait(false);
            _finalWriteParticipant?.OpenAfterProducerActivation();
        }
        catch (Exception resumeError)
        {
            // Installation failures do not enter Activate's pre-rollback catch.
            // Notify here before our own cleanup too; repeat notification is harmless.
            List<Exception> errors = new() { resumeError };
            try { _finalWriteParticipant?.FailTransition(resumeError); } catch (Exception error) { errors.Add(error); }
            Task hostDrain = Task.CompletedTask;
            bool hostCut = false;
            if (!_producerStopRequested && _finalWriteParticipant != null)
            {
                try { hostDrain = _finalWriteParticipant.CutAndDrainAsync(); hostCut = true; }
                catch (Exception error) { errors.Add(error); }
            }
            ProfileProducerEpoch? fresh;
            lock (_state) { fresh = _producerEpoch; _producerIngress = false; _producerSuspended = true; }
            bool drained = false;
            try { await Task.WhenAll(hostDrain, DrainProfileEpochAsync(fresh)).ConfigureAwait(false); drained = true; }
            catch (Exception error) { errors.Add(error); }
            if (drained)
            {
                if (hostCut) _finalWriteParticipant!.ReleaseDrainedEpoch();
                ReleaseDrainedProfileEpoch(fresh);
            }
            try { _finalWriteParticipant?.FailTransition(resumeError); } catch (Exception error) { errors.Add(error); }
            if (errors.Count == 1) throw;
            throw new AggregateException("Profile family resume failed.", errors);
        }
    }

    // One existing lane, ONE reservation for both families. No host Ordinary or
    // _commands wrapper. Every granted token is observed and disposed exactly once.
    // No untyped success lane: default outcome and every unclassified exception
    // after delegate entry are uncertain, even when the kernel is still Ready.
    internal async Task<ProfileExclusiveOutcome<T>> RunWithProfileExclusiveAsync<T>(
        Func<ProfileAdmissionToken, Task<ProfileExclusiveOutcome<T>>> operation, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await _producerTransitions.WaitAsync(token).ConfigureAwait(false);
        using CancellationTokenSource reservationCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<ProfileAdmissionToken>? reservation = null;
        ProfileAdmissionToken? exclusive = null;
        ProfileProducerEpoch? epoch = null;
        bool cut = false, quiescent = false, delegateEntered = false, safeToResume = false;
        Exception? recoveryError = null;
        try
        {
            _finalWriteParticipant?.CloseTransitionCommandIngress();
            lock (_state)
            {
                if (_producerStopRequested || _stopping) throw new InvalidOperationException("Registry is stopping.");
                epoch = _producerEpoch;
                if (epoch == null && !_producerSuspended) throw new InvalidOperationException("No live producer epoch.");
                _producerLease ??= BoundProfileLease;
                reservation = (_producerLease ?? throw new InvalidOperationException("No profile."))
                    .Admission.EnterExclusiveAsync(reservationCancellation.Token).AsTask();
            }
            // The hook's synchronous cut can refuse Stop-before-cut without closing
            // TerminalOpen. No registry family is changed on a pre-cut refusal.
            Task hostDrain = _finalWriteParticipant?.CutAndDrainAsync() ?? Task.CompletedTask;
            cut = true;
            lock (_state) { _producerIngress = false; _producerSuspended = true; }
            await Task.WhenAll(hostDrain, DrainProfileEpochAsync(epoch)).ConfigureAwait(false);
            _finalWriteParticipant?.ReleaseDrainedEpoch();
            ReleaseDrainedProfileEpoch(epoch);
            quiescent = true;
            Task<ProfileAdmissionToken> acceptedReservation = reservation;
            reservation = null;
            exclusive = await acceptedReservation.ConfigureAwait(false);
            BeforeProfileExclusiveOperationForTests?.Invoke(exclusive);
            token.ThrowIfCancellationRequested();
            if (_producerStopRequested) throw new InvalidOperationException("Registry is stopping.");
            delegateEntered = true;
            safeToResume = false;
            ProfileExclusiveOutcome<T> outcome = await operation(exclusive).ConfigureAwait(false);
            safeToResume = outcome.Disposition == ProfileExclusiveDisposition.SafeToResume;
            return outcome;
        }
        catch (Exception error)
        {
            if (delegateEntered) recoveryError = error;
            // Cancel before any delegate effect is independently known safe only
            // after the actual complete drain. Other transition faults are not a
            // negative-effect proof; Stop is still checked again before resume.
            else if (quiescent && error is OperationCanceledException) safeToResume = true;
            if (cut && !quiescent) _finalWriteParticipant?.FailTransition(error);
            throw;
        }
        finally
        {
            try
            {
                try
                {
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
                finally
                {
                    try
                    {
                        if (delegateEntered && !safeToResume)
                        {
                            // Both fences and observer settlement precede release.
                            // Even a failing notification cannot skip token disposal;
                            // no recovery fence revokes still-counted consumer pins.
                            _producerLease!.Admission.FailRecovery();
                            _finalWriteParticipant?.RequireRecovery(recoveryError ??
                                new InvalidOperationException("Exclusive profile outcome requires recovery; writers remain suspended."));
                            AfterProfileExclusiveRecoverySealedForTests?.Invoke(exclusive!);
                        }
                    }
                    finally { exclusive?.Dispose(); }
                }
                if (!cut) _finalWriteParticipant?.AbortBeforeCut();
                if (safeToResume && quiescent && !_producerStopRequested && _producerLease?.Admission.Phase == ProfileAdmissionPhase.Ready)
                    await ResumeProfileFamiliesAsync().ConfigureAwait(false);
            }
            finally { _producerTransitions.Release(); }
        }
    }
}
