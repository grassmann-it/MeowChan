using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Archive;
using Serilog;

namespace MeowChan.Booru;

/// <summary>
/// Lifecycle owner for Danbooru watchers in a running profile. Composition (B6/root):
/// create after <c>Registry.StartAsync</c> completed (profile admission Ready, shared queue running) and
/// the Danbooru worker is available behind the search port; call <see cref="StartAsync"/>; call
/// <see cref="StopAsync"/> before the Registry/queue stop and before the profile lease is released.
/// <para>
/// Start order (preflight G14): load and recover every stored watcher first (a crash-left Checking
/// watcher returns to its resume target with its cursor), then begin re-admitting pending originals from
/// local data (B4 <see cref="BooruOriginalQueue.RecoverPendingAsync"/>, no provider request), then start
/// the loop. The re-admission runs as an observed background task with its own cancellation: the shared
/// queue may hold an admission back (paused producers, free-space gate), and the host start must not wait
/// for that. A pending original the loop finds again meanwhile is deduplicated by the queue adapter (one
/// owner per post). A failed recovery is logged and leaves every request pending for the next start.
/// Stop order: cancel the recovery and the running check, persist the check's consistent state while the
/// profile still accepts writes, and wait for both; queued downloads stay with the queue's own
/// pause/restart contract.
/// </para>
/// <para>
/// Profile transitions (F2): with a <see cref="BooruProducerLifecycle"/> the load waits for an open generation, every
/// recovery run is a counted use of its generation (the cut cancels it, so a recovery parked at a suspended queue
/// never pins the drain) and each newly opened generation starts a fresh recovery run for the requests that are
/// still pending. The host stop is terminal; the lifecycle's own cut/resume is the reversible seam, and the session
/// owner stops the lifecycle after this host stopped.
/// </para>
/// </summary>
internal sealed class DanbooruWatcherHost : IAsyncDisposable
{
    private const string CatalogUnreadableFault = "catalog_unreadable";
    private readonly BooruOriginalQueue _originals;
    private readonly BooruProducerLifecycle? _lifecycle;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    // Guards the recovery run bookkeeping against the lifecycle's Changed callback, which can run on any thread.
    private readonly object _recoverySync = new();
    private CancellationTokenSource? _recoveryCancellation;
    private Task<IReadOnlyList<BooruOriginalAdmission>> _recovery = Task.FromResult<IReadOnlyList<BooruOriginalAdmission>>([]);
    private long _recoveryGeneration = -1;
    private bool _recoveryEnabled;
    // Also cancels a StartAsync parked in EnterAsync before it can release _lifecycleGate. Null is the
    // terminal stop fence. Accessed under _recoverySync so no new start can link to a disposed source.
    private CancellationTokenSource? _startupStop = new();
    private bool _started;
    private bool _stopped;

    internal DanbooruWatcherHost(BooruCatalogStore catalog, BooruOriginalQueue originals,
        IDanbooruWatcherSearchPort search, TimeProvider? timeProvider = null,
        DanbooruWatcherSchedulerOptions? options = null, BooruProducerLifecycle? lifecycle = null,
        string sourceId = DanbooruWatcher.ProviderId)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _originals = originals ?? throw new ArgumentNullException(nameof(originals));
        _lifecycle = lifecycle;
        Scheduler = new DanbooruWatcherScheduler(catalog, search,
            new DanbooruWatcherQueueAdmission(originals, catalog, timeProvider), timeProvider, options, lifecycle, sourceId);
    }

    /// <summary>The bindable service surface for the UI (status, check now, pause, resume, remove).</summary>
    internal DanbooruWatcherScheduler Scheduler { get; }

    /// <summary>
    /// The background re-admission of pending originals begun by the last successful start or, with a lifecycle, by
    /// the last opened generation. A stop or cut cancels it; it then yields the admissions already handed to the
    /// queue. An expected failure (unreadable catalog, profile closing) is logged and yields an empty list; only a
    /// programming error faults it.
    /// </summary>
    internal Task<IReadOnlyList<BooruOriginalAdmission>> Recovery => Volatile.Read(ref _recovery);

    /// <summary>The transient global session pause gate of the scheduler (F3); see <see cref="DanbooruWatcherScheduler.SetSessionPaused"/>.</summary>
    internal void SetSessionPaused(bool paused) => Scheduler.SetSessionPaused(paused);

    internal async Task StartAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenSource startup;
        lock (_recoverySync)
        {
            if (_startupStop is null) return;
            startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _startupStop.Token);
        }
        using CancellationTokenSource start = startup;
        CancellationToken startToken = start.Token;
        await _lifecycleGate.WaitAsync(startToken).ConfigureAwait(false);
        try
        {
            if (_started || _stopped) return;
            // Only mark started once the whole start order succeeded. A cancelled or failed start
            // must stay retryable; otherwise a transient failure permanently disables the watchers.
            // With a lifecycle the load (its restart-recovery writes) is a counted use of an open generation; an
            // exclusive operation in progress delays the start instead of failing it.
            BooruProducerUse? load = _lifecycle is null ? null : await _lifecycle.EnterAsync(startToken).ConfigureAwait(false);
            try { Scheduler.Load(startToken, load); }
            finally { load?.Dispose(); }
            startToken.ThrowIfCancellationRequested();
            // An unreadable catalog is already the scheduler's visible load fault; a recovery would only fail on the
            // same read and report it a second time.
            bool recover = Scheduler.GetSnapshot().FaultCode != CatalogUnreadableFault;
            if (_lifecycle != null) _lifecycle.Changed += OnLifecycleChanged;
            lock (_recoverySync)
            {
                _recoveryEnabled = recover;
                if (recover) StartRecoveryLocked();
            }
            try { await Scheduler.StartAsync(startToken).ConfigureAwait(false); }
            catch
            {
                await EndRecoveryAsync().ConfigureAwait(false);
                throw;
            }
            _started = true;
        }
        finally { _lifecycleGate.Release(); }
    }

    private void OnLifecycleChanged()
    {
        // A fresh generation re-admits what is still pending; the previous run was cut and drained before it opened.
        lock (_recoverySync)
            if (_recoveryEnabled) StartRecoveryLocked();
    }

    /// <summary>Caller holds <see cref="_recoverySync"/>. Starts one run per generation, never two at once.</summary>
    private void StartRecoveryLocked()
    {
        BooruProducerUse? use = null;
        if (_lifecycle != null)
        {
            if (_lifecycle.Generation == _recoveryGeneration) return;
            use = _lifecycle.TryEnter();
            if (use is null) return;
            if (use.Generation == _recoveryGeneration)
            {
                use.Dispose();
                return;
            }
            _recoveryGeneration = use.Generation;
        }
        else if (_recoveryCancellation != null) return;
        CancellationTokenSource recovery = use is null ? new() : CancellationTokenSource.CreateLinkedTokenSource(use.Cut);
        CancellationTokenSource? previous = _recoveryCancellation;
        _recoveryCancellation = recovery;
        Task<IReadOnlyList<BooruOriginalAdmission>> started = Task.Run(() => RecoverAsync(recovery.Token, use), CancellationToken.None);
        Volatile.Write(ref _recovery, started);
        // The previous run belonged to a drained generation, so it has already ended.
        previous?.Dispose();
    }

    private async Task<IReadOnlyList<BooruOriginalAdmission>> RecoverAsync(CancellationToken cancellationToken,
        BooruProducerUse? use)
    {
        try { return await _originals.RecoverPendingAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return []; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException
            or InvalidOperationException or ObjectDisposedException)
        {
            // Nothing was lost: every request that was not re-admitted is still pending in the catalog. An unreadable
            // catalog is already visible as the scheduler's load fault.
            Log.Warning(exception, "Danbooru pending originals could not be re-admitted; they stay pending for the next start");
            return [];
        }
        finally
        {
            // Last: the generation's drain waits for the recovery to have really ended.
            use?.Dispose();
        }
    }

    /// <summary>Stops starting recovery runs, cancels the current one and waits for it.</summary>
    private Task EndRecoveryAsync() => AwaitRecoveryAsync(CancelRecovery());

    /// <summary>Synchronously disables new recovery runs and cancels the current one.</summary>
    private (CancellationTokenSource? Cancellation, Task<IReadOnlyList<BooruOriginalAdmission>> Run) CancelRecovery()
    {
        if (_lifecycle != null) _lifecycle.Changed -= OnLifecycleChanged;
        CancellationTokenSource? recovery;
        Task<IReadOnlyList<BooruOriginalAdmission>> running;
        lock (_recoverySync)
        {
            _recoveryEnabled = false;
            _recoveryGeneration = -1;
            recovery = _recoveryCancellation;
            _recoveryCancellation = null;
            running = Recovery;
        }
        try { recovery?.Cancel(); }
        catch (ObjectDisposedException) { }
        return (recovery, running);
    }

    private static async Task AwaitRecoveryAsync(
        (CancellationTokenSource? Cancellation, Task<IReadOnlyList<BooruOriginalAdmission>> Run) recovery)
    {
        // A recovery admission waiting at the queue observes the cancellation; one already handed over
        // keeps its own queue job. Only a programming error faults RecoverAsync.
        try { await recovery.Run.ConfigureAwait(false); }
        finally { recovery.Cancellation?.Dispose(); }
    }

    internal async Task StopAsync()
    {
        CancellationTokenSource? startupStop;
        lock (_recoverySync)
        {
            startupStop = _startupStop;
            _startupStop = null;
        }
        // Signal before waiting for the gate: StartAsync may hold it while the profile is suspended.
        // Only the caller that removed this source cancels/disposes it; concurrent stops still drain below.
        if (startupStop != null)
        {
            try { startupStop.Cancel(); }
            finally { startupStop.Dispose(); }
        }
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stopped) return;
            _stopped = true;
            var recovery = CancelRecovery();
            try { await Scheduler.StopAsync().ConfigureAwait(false); }
            finally { await AwaitRecoveryAsync(recovery).ConfigureAwait(false); }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
