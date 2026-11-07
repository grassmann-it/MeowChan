using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using MeowChan.Persistence;

namespace MeowChan;

public sealed partial class Registry
{
    private Task? _standaloneStartup;
    private ProfileLease? _standaloneStartupLease;
    private TaskCompletionSource? _standaloneStartupSettled;
    private CancellationTokenSource? _standaloneStartupCancellation;
    private bool _standaloneStartupSucceeded;

    // Observe real work outside monitors; these hooks cannot supply authority.
    internal Func<Task>? BeforeStandaloneStartupReadyForTests { get; set; }
    internal Func<Task>? BeforeStandaloneStartupActivationForTests { get; set; }
    internal Action? BeforeStandaloneStartupCancellationForTests { get; set; }

    /// <summary>
    /// Starts a new profile-bound session: verifies transfer recovery, restores with
    /// producers suspended, then activates them. Call after provider/store setup.
    /// The first accepted call owns cancellation; repeated calls for the same lease
    /// return its task. Failure after claiming startup is terminal for this instance.
    /// The caller retains ownership of the lease and supplied stores/runtime.
    /// </summary>
    public Task StartAsync(ProfileLease lease, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lease);
        TaskCompletionSource completion;
        ProfileAdmissionToken? startup = null;
        Exception? preparationError = null;
        CancellationTokenSource cancellation;
        lock (_state)
        {
            if (_standaloneStartup != null)
            {
                if (!ReferenceEquals(lease, _standaloneStartupLease))
                    throw new InvalidOperationException("The startup belongs to a different profile lease.");
                return _standaloneStartup;
            }
            cancellationToken.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_producerStopRequested || _stopping, this);
            if (_producerPrepared || _producerEpoch != null || _restored || _hasMutations || _watches.Count != 0)
                throw new InvalidOperationException("Start a new standalone Registry before restoring or admitting watches.");
            ValidateStartupComposition(lease);
            // No queue mutation before the claim. A rejected competitor must stay
            // reusable, and a known identity mismatch must not seal this profile.
            _queue.ValidateProfileLeaseBinding(lease);
            lease.ClaimStartupOwner(this);
            _standaloneStartupLease = lease;
            _standaloneStartupCancellation = cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _standaloneStartupSettled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _standaloneStartup = completion.Task;
            try
            {
                PrepareProfileProducers(lease);
                startup = lease.Admission.EnterStartupWriter();
            }
            catch (Exception error) { preparationError = error; }
        }
        // Registration/async work never runs under the Registry state monitor.
        _ = Task.Run(() => StartStandaloneCoreAsync(lease, startup, cancellation, preparationError, completion));
        return completion.Task;
    }

    private void ValidateStartupComposition(ProfileLease lease)
    {
        lease.RunExclusive(() => true);
        foreach (ProfileLease? bound in new[] { _producerLease,
                     (_workStore as WorkStateStore)?.ProfileLease,
                     (_sessionStore as SessionStore)?.ProfileLease,
                     _boardAdmission?.ProfileLease, _contentRuntime?.ProfileLease })
            if (bound != null && !ReferenceEquals(bound, lease))
                throw new InvalidOperationException("Registry components must use the exact same profile lease.");
    }

    private async Task StartStandaloneCoreAsync(ProfileLease lease, ProfileAdmissionToken? startup,
        CancellationTokenSource cancellation, Exception? preparationError, TaskCompletionSource completion)
    {
        Exception? failure = null;
        using CancellationTokenRegistration registration = cancellation.Token.Register(SealStandaloneStartup);
        try
        {
            if (preparationError != null) throw preparationError;
            CancellationToken token = cancellation.Token;
            token.ThrowIfCancellationRequested();
            if (_contentRuntime != null)
            {
                await _contentRuntime.InitializeAdmittedAsync(startup!, token).ConfigureAwait(false);
                await _contentRuntime.Catalog.EnsureNativeMediaRecoveryClearAsync(token).ConfigureAwait(false);
            }
            else
            {
                // Recovery applies even to media-only consumers. Deliberately open
                // and migrate archive.db, without attaching content capture.
                await using ArchiveCatalog catalog = await ArchiveCatalog.OpenAdmittedAsync(
                    lease.ProfileDirectory, lease, startup!, token).ConfigureAwait(false);
                await catalog.EnsureNativeMediaRecoveryClearAsync(token).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            await RestoreAdmittedAsync(startup).ConfigureAwait(false);
            if (BeforeStandaloneStartupReadyForTests is { } beforeReady)
                await beforeReady().ConfigureAwait(false);
            lock (_state)
            {
                ThrowIfStandaloneStartupStopped(token);
                startup!.Dispose();
                startup = null;
                lease.Admission.CompleteStartup();
                InstallReadyProfileEpoch();
            }
            if (BeforeStandaloneStartupActivationForTests is { } beforeActivation)
                await beforeActivation().ConfigureAwait(false);
            lock (_state) ThrowIfStandaloneStartupStopped(token);
            await ActivatePreparedProfileProducersAsync(SealStandaloneStartup).ConfigureAwait(false);
            lock (_state)
            {
                ThrowIfStandaloneStartupStopped(token);
                _standaloneStartupSucceeded = true;
                completion.TrySetResult();
            }
        }
        catch (Exception error)
        {
            failure = error;
            SealStandaloneStartup();
        }
        finally
        {
            // Actual initialization/restore/activation has returned; only now may
            // Stop take its watch snapshot and release producer ownership.
            startup?.Dispose();
            _standaloneStartupSettled!.TrySetResult();
        }
        if (failure != null)
        {
            try { await StopAsync().ConfigureAwait(false); }
            catch (Exception cleanupError)
            {
                failure = new AggregateException("Standalone startup and cleanup failed.", failure, cleanupError);
            }
            if (failure is OperationCanceledException canceled)
                completion.TrySetCanceled(canceled.CancellationToken);
            else
                completion.TrySetException(failure);
        }
    }

    private void ThrowIfStandaloneStartupStopped(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_producerStopRequested || _stopping)
            throw new OperationCanceledException("The Registry was stopped during startup.", token);
    }

    private void SealStandaloneStartup()
    {
        lock (_state)
        {
            if (_standaloneStartupLease == null || _standaloneStartupSucceeded) return;
            ProfileAdmission admission = _standaloneStartupLease.Admission;
            try
            {
                if (admission.Phase == ProfileAdmissionPhase.Starting) admission.FailStartup();
                else if (admission.Phase == ProfileAdmissionPhase.Ready) admission.BeginShutdown();
            }
            catch (InvalidOperationException) when (admission.Phase != ProfileAdmissionPhase.Starting)
            {
                // A caller may dispose/seal its lease concurrently. Never let
                // that monotonic close escape a cancellation callback or skip drain.
                admission.BeginShutdown();
            }
            _producerStopRequested = true;
            _producerIngress = false;
            _producerSuspended = true;
        }
        _queue.CloseProfileProducerIngress();
    }

    private void RequestStandaloneStartupStop()
    {
        CancellationTokenSource? cancellation;
        lock (_state)
            cancellation = _standaloneStartupSucceeded ? null : _standaloneStartupCancellation;
        if (cancellation == null) return;
        SealStandaloneStartup();
        BeforeStandaloneStartupCancellationForTests?.Invoke();
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) when (IsStandaloneStartupCancellationRetired(cancellation))
        {
            // Another DisposeAsync already joined Stop and retired this owned
            // source. Preserve Stop's cached outcome rather than a late CTS race.
        }
    }

    private bool IsStandaloneStartupCancellationRetired(CancellationTokenSource cancellation)
    {
        lock (_state) return !ReferenceEquals(_standaloneStartupCancellation, cancellation);
    }

    private Task WaitForStandaloneStartupWorkAsync()
    {
        lock (_state) return _standaloneStartupSettled?.Task ?? Task.CompletedTask;
    }
}
