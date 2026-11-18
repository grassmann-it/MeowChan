using MeowChan.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

internal sealed partial class DesktopSessionHost
{
    // One startup lifetime per host/lease, acquired before constructing profile owners.
    // Access is serialized by _lifecycle (or by the not-yet-published constructor).
    private ProfileAdmissionToken? _profileStartupAdmission;

    private void ReleaseProfileStartupAdmission()
    {
        ProfileAdmissionToken? admission = _profileStartupAdmission;
        _profileStartupAdmission = null;
        admission?.Dispose();
    }

    private void FinishProfileStartup(bool succeeded)
    {
        // Called at the restoration boundary, before publishing host Ready/RecoveryFailed.
        // Failure disables new writers before releasing the startup work's ownership.
        if (succeeded)
        {
            ReleaseProfileStartupAdmission();
            _profileLease.Admission.CompleteStartup();
        }
        else
        {
            try { _profileLease.Admission.FailStartup(); }
            finally { ReleaseProfileStartupAdmission(); }
        }
    }

    // Direct owner lane: deliberately not dispatched through an Ordinary command
    // and never takes _commands while waiting for previously accepted commands.
    // No Func<..., Task> compatibility overload: every caller, including a
    // non-transfer owner, must explicitly attest its reconciled safe result.
    internal Task<ProfileExclusiveOutcome<T>> RunWithProfileProducerExclusiveAsync<T>(
        Func<ProfileAdmissionToken, Task<ProfileExclusiveOutcome<T>>> operation, CancellationToken token = default)
    {
        EnsureReady();
        return _registry.RunWithProfileExclusiveAsync(operation, token);
    }

    private void RequireProfileRecovery(Exception error)
    {
        lock (_lifecycle)
        {
            if (_state is DesktopSessionState.Starting or DesktopSessionState.Ready)
            {
                _startupError = error.Message;
                _state = DesktopSessionState.RecoveryFailed;
            }
        }
        // No outer lifecycle/kernel monitor around settlement. Stop may already
        // own the terminal plan; FailFinalWriteTransition must not steal it.
        FailFinalWriteTransition(error);
        // Publish the state immediately, but never wait for UI observers here:
        // an observer may call Stop, whose final release needs the Registry lane.
        PublishLifecycleState(notifyOnWorker: true);
    }

    private void BeginProfileShutdown()
    {
        // Caller holds _lifecycle and has closed host command admission. Startup
        // cannot publish Ready after this point, even if restoration later succeeds.
        if (_profileLease.Admission.Phase == ProfileAdmissionPhase.Starting)
            _profileLease.Admission.FailStartup();
        // With StartAsync scheduled, its worker owns the token until restoration
        // returns. Stop-before-Start has no worker and must release it here instead.
        if (_startTask == null) ReleaseProfileStartupAdmission();
        _profileLease.Admission.BeginShutdown();
    }

    private HostFinalWriteParticipant _hostFinalWriteParticipant = null!;
    private sealed class HostFinalWriteParticipant(DesktopSessionHost host) : IProfileFinalWriteParticipant
    {
        public void CloseTransitionCommandIngress()
        {
            host.EnsureReady();
            host.CloseTransitionCommandIngress();
            // F2: nothing new starts in the Danbooru family from here; a kernel refusal its running work meets after
            // the reservation is attributed to this transition, not to local storage.
            host._booruProducers.BeginTransition();
        }
        public Task CutAndDrainAsync()
        {
            // The final-write cut may refuse (Stop before cut) and then nothing was cut. Both cuts are synchronous
            // before this returns; the Registry drains its own families concurrently.
            Task finalWrites = host.CutAndDrainFinalWrites();
            Task booru = host._booruProducers.CutAndDrainAsync();
            return Task.WhenAll(finalWrites, booru);
        }
        public void ReleaseDrainedEpoch()
        {
            host.ReleaseFinalWriteEpoch(stopped: false);
            host._booruProducers.ReleaseDrained();
        }
        public void InstallReadyEpoch() => host.InstallReadyFinalWriteEpoch();
        public void OpenAfterProducerActivation()
        {
            // SafeToResume only (the Registry never resumes after RecoveryRequired or Stop): a fresh Danbooru
            // generation after the queue producers are active again, then the final-write family.
            host.OpenBooruProducers();
            host.OpenFinalWriteEpoch();
        }
        // Activation rollback is still retryable after a complete drain. A post-cut drain failure uses
        // RequireRecovery below instead; it must not leave the Booru family merely Suspended.
        public void FailTransition(Exception error) => host.FailFinalWriteTransition(error);
        public void RequireRecovery(Exception error)
        {
            // Seal kernel ingress before terminal family cleanup. Accepted uses keep their exact ownership until
            // the lifecycle's terminal drain has completed; this call never revokes them early.
            host._profileLease.Admission.FailRecovery();
            host._booruProducers.RequireRecovery();
            host.RequireProfileRecovery(error);
        }
        public void AbortBeforeCut()
        {
            host._booruProducers.AbortTransition();
            lock (host._finalWrites) { if (!host.FinalWriteAuthorityOpen) return; }
            host.ReopenTransitionCommandIngress();
        }
        public void ReleaseStoppedEpoch() => host.ReleaseFinalWriteEpoch(stopped: true);
    }

    private void InstallReadyFinalWriteEpoch()
    {
        lock (_lifecycle)
        lock (_finalWrites)
        {
            if (_state is not (DesktopSessionState.Starting or DesktopSessionState.Ready))
                throw new InvalidOperationException("Session stopping before final-write installation.");
            if (_finalWriteEpoch != null) throw new InvalidOperationException("Final-write epoch already installed.");
            var admission = _profileLease.Admission.EnterOrdinaryWriter();
            _finalWriteEpoch = new ProfileProducerEpoch(admission);
            _cutFinalWriteEpoch = null;
            _finalWriteFamilyState = FinalWriteFamilyState.Prepared;
        }
    }
    private void OpenFinalWriteEpoch()
    {
        lock (_lifecycle)
        lock (_finalWrites)
        {
            if (_state is not (DesktopSessionState.Starting or DesktopSessionState.Ready)
                || _finalWriteFamilyState != FinalWriteFamilyState.Prepared || _finalWriteEpoch == null)
                throw new InvalidOperationException("Session stopped before final-write activation.");
            _finalWriteFamilyState = FinalWriteFamilyState.Open;
            _finalWriteSuspensionError = null;
            foreach (string unit in _parkedFinalWrites.Keys.ToArray())
            {
                FinalWriteBatch parked = _parkedFinalWrites[unit];
                // A failed original observer stays failed. A later real retry has a new promise.
                FinalWriteBatch pending = parked.Completion.Task.IsCompleted ? new() : parked;
                if (!ReferenceEquals(pending, parked)) CopyDraft(parked, pending);
                if (_failedFinalWrites.Remove(unit, out var failed)) CopyDraft(failed, pending);
                _pendingFinalWrites.Add(unit, pending);
            }
            _parkedFinalWrites.Clear();
            foreach (var pair in _pendingFinalWrites)
            {
                pair.Value.Use = _finalWriteEpoch.Enter();
                StartFinalWriteLoop(pair.Key);
            }
        }
        ReopenTransitionCommandIngress();
    }

    // Non-async entry makes the cut synchronous before the Registry can start its drain.
    private Task CutAndDrainFinalWrites()
    {
        Task uses; Task[] loops; FinalWriteFlushRegistration[] markers;
        lock (_lifecycle)
        lock (_finalWrites)
        {
            if (_finalWriteFamilyState is FinalWriteFamilyState.TerminalOpen or FinalWriteFamilyState.Sealed or FinalWriteFamilyState.Released
                || _state != DesktopSessionState.Ready)
                throw new InvalidOperationException("Session stopping before final-write cut.");
            if (_finalWriteFamilyState == FinalWriteFamilyState.Open)
            {
                RetryFailedFinalWrites();
                FreezeWaitingFinalWrites();
            }
            var watermark = SnapshotFinalWriteWatermark();
            markers = _cutFlushRegistrations;
            foreach (var marker in markers) marker.Watermark ??= watermark;
            _cutFinalWriteEpoch = _finalWriteEpoch;
            _finalWriteFamilyState = FinalWriteFamilyState.CutDraining;
            uses = _finalWriteEpoch?.Close() ?? Task.CompletedTask;
            loops = _finalWriteLoops.Values.ToArray();
        }
        foreach (var marker in markers) ObserveCommandFinalWriteFence(marker);
        // Save faults are in the immutable watermark, not evidence of a live owner.
        // Loop/use lifetime, unlike a Save task, must actually end before release.
        return Task.WhenAll(loops.Append(uses));
    }

    private void FailFinalWriteTransition(Exception error)
    {
        List<TaskCompletionSource> rejected = new(2);
        lock (_lifecycle)
        lock (_finalWrites)
        {
            if (_finalWriteFamilyState is FinalWriteFamilyState.TerminalOpen or FinalWriteFamilyState.TerminalNoAuthority
                or FinalWriteFamilyState.Sealed or FinalWriteFamilyState.Released) return;
            _finalWriteFamilyState = FinalWriteFamilyState.Suspended;
            _finalWriteSuspensionError = new FinalWriteUnavailableException("Family suspended after transition failure: " + error.Message);
            foreach (var draft in _parkedFinalWrites.Values) rejected.Add(draft.Completion);
            if (_state == DesktopSessionState.Starting)
            {
                _finalWriteStartupState = 2;
                foreach (var pair in _pendingFinalWrites)
                {
                    rejected.Add(pair.Value.Completion);
                    MergeFailedFinalWrite(pair.Key, pair.Value.Preferences, pair.Value.Position, pair.Value.Generation, pair.Value.Revision);
                }
                _pendingFinalWrites.Clear();
                _terminalRejectedFinalWrites = rejected.Select(c => c.Task).ToArray();
            }
        }
        // Admission lock is never nested with product locks.
        lock (_admission) _finalWriteFlushCommandsClosed = true;
        foreach (var completion in rejected) completion.TrySetException(new FinalWriteUnavailableException("Transition failed: " + error.Message));
    }

    private FinalWriteShutdownPlan BeginFinalWriteShutdown(Task restoration)
    {
        lock (_finalWrites)
        {
            if (_finalWriteFamilyState == FinalWriteFamilyState.Open)
            {
                _terminalFinalWriteUse = _finalWriteEpoch!.Enter();
                _finalWriteFamilyState = FinalWriteFamilyState.TerminalOpen;
                return new(restoration, Array.Empty<TaskCompletionSource>());
            }
            _finalWriteFamilyState = FinalWriteFamilyState.TerminalNoAuthority;
            var rejected = _parkedFinalWrites.Values.Concat(_pendingFinalWrites.Values.Where(b => b.Use == null))
                .Select(b => b.Completion).ToArray();
            foreach (var pair in _pendingFinalWrites.Where(p => p.Value.Use == null).ToArray())
            {
                MergeFailedFinalWrite(pair.Key, pair.Value.Preferences, pair.Value.Position, pair.Value.Generation, pair.Value.Revision);
                _pendingFinalWrites.Remove(pair.Key);
            }
            if (_finalWriteStartupState == 0) _finalWriteStartupState = 2;
            _terminalRejectedFinalWrites = _terminalRejectedFinalWrites.Concat(rejected.Select(c => c.Task)).Distinct().ToArray();
            return new(restoration, rejected);
        }
    }
    private void SettleTerminalFinalWriteObservers(FinalWriteShutdownPlan plan)
    {
        BeforeTerminalFinalWriteSettlementForTests?.Invoke();
        foreach (var completion in plan.Rejected)
        {
            AtTerminalFinalWriteSettlementForTests?.Invoke();
            completion.TrySetException(new FinalWriteUnavailableException("Stopping without authority."));
        }
    }
    private async Task DrainFailedStartupFinalWritesAsync()
    {
        ProfileProducerEpoch? cleanupEpoch;
        Task uses; Task[] loops; bool terminalOwned;
        lock (_lifecycle)
        lock (_finalWrites)
        {
            cleanupEpoch = _finalWriteEpoch;
            terminalOwned = _terminalFinalWriteUse != null;
            if (terminalOwned)
            {
                // Token identity binds the actual reserved Use to this exact epoch,
                // even if Stop has already changed TerminalOpen to Sealed. Startup
                // is the Restoration task awaited by that terminal owner: never
                // Close/wait its reservation or release its family from this task.
                if (cleanupEpoch == null || !ReferenceEquals(_terminalFinalWriteUse!.Admission, cleanupEpoch.Admission))
                    throw new InvalidOperationException("Startup terminal ownership does not match its final-write epoch.");
                cleanupEpoch = null;
            }
            else if (_finalWriteFamilyState == FinalWriteFamilyState.Open)
            {
                // The startup unwind wins instead. Prevent a later BeginShutdown
                // from reserving a Terminal-Use after we have closed this epoch.
                _finalWriteFamilyState = FinalWriteFamilyState.Suspended;
            }
            uses = cleanupEpoch?.Close() ?? Task.CompletedTask;
            // A handoff excludes ONLY the terminal reservation, not actual writers.
            // Their loops include real I/O, publication, use release and cleanup.
            loops = _finalWriteLoops.Values.ToArray();
        }
        AfterStartupFinalWriteCleanupClaimForTests?.Invoke(terminalOwned);
        await Task.WhenAll(loops.Append(uses)).ConfigureAwait(false);
        if (cleanupEpoch != null)
        {
            lock (_finalWrites)
                if (_finalWriteEpoch != null && !ReferenceEquals(_finalWriteEpoch, cleanupEpoch))
                    throw new InvalidOperationException("Startup cannot release a successor final-write epoch.");
            // Registry.StopAsync has really returned before this helper is entered:
            // its producer lane is free, and no successor can be installed. Release
            // the claimed, genuinely drained generation through that same lane.
            await _registry.ReleaseStoppedHostFinalWritesAsync().ConfigureAwait(false);
        }
    }
    private async Task CloseFinalWriteEpochAsync()
    {
        Task uses; Task[] loops;
        lock (_finalWrites)
        {
            uses = _finalWriteEpoch?.Close() ?? Task.CompletedTask;
            loops = _finalWriteLoops.Values.ToArray();
        }
        await Task.WhenAll(loops.Append(uses)).ConfigureAwait(false);
    }
    private void ReleaseFinalWriteEpoch(bool stopped)
    {
        ProfileProducerEpoch? epoch;
        lock (_finalWrites)
        {
            epoch = _finalWriteEpoch;
            if (epoch == null) return;
            if (!stopped && !ReferenceEquals(epoch, _cutFinalWriteEpoch))
                throw new InvalidOperationException("Cannot release a successor final-write epoch.");
            if (_finalWriteFamilyState is FinalWriteFamilyState.Open or FinalWriteFamilyState.TerminalOpen
                || !epoch.Close().IsCompletedSuccessfully || _activeFinalWrites.Count != 0 || _pendingFinalWrites.Values.Any(b => b.Use != null)
                || _frozenFinalWrites.Values.Any(a => a.Any(b => b != null)))
                throw new InvalidOperationException("Final-write ownership has not drained.");
            _finalWriteEpoch = null;
            _cutFinalWriteEpoch = null;
            if (stopped || _finalWriteFamilyState is FinalWriteFamilyState.TerminalNoAuthority or FinalWriteFamilyState.Sealed)
                _finalWriteFamilyState = FinalWriteFamilyState.Released;
            else _finalWriteFamilyState = FinalWriteFamilyState.Suspended;
        }
        epoch.Dispose();
    }

}
