using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;
using Serilog;

namespace MeowChan.Booru;

/// <summary>Phase of the Danbooru producer family (watcher checks, watcher commands, pending-original recovery).</summary>
internal enum BooruProducerPhase
{
    /// <summary>Constructed; no generation was opened yet (profile not Ready). Nothing may start.</summary>
    Prepared,
    /// <summary>A generation is open: its counted uses may run and write with its Ordinary admission.</summary>
    Open,
    /// <summary>
    /// A profile-wide exclusive transition was announced but not cut yet. Nothing new may start; running uses keep
    /// their generation. <see cref="BooruProducerLifecycle.AbortTransition"/> returns to <see cref="Open"/>.
    /// </summary>
    Transitioning,
    /// <summary>The generation was cut (reversible). A safe resume opens a fresh generation.</summary>
    Suspended,
    /// <summary>An exclusive outcome requires recovery. Never reopened in this session.</summary>
    RecoveryRequired,
    /// <summary>Terminal session stop.</summary>
    Stopped
}

/// <summary>
/// Reversible Suspend → Drain → Resume seam of the Danbooru producer family for profile-wide exclusive operations
/// (F2). One instance per running profile session, owned by the session host and independent of whether a Danbooru
/// composition exists. It follows the existing <see cref="ProfileProducerEpoch"/> pattern of the other producer
/// families: one Ordinary admission per generation, counted <see cref="BooruProducerUse"/>s for every physical
/// watcher check, watcher command and recovery run, a synchronous cut, an actual drain of those uses and only then the
/// release of the generation's admission.
/// <para>
/// Ownership rules: a use is entered only while <see cref="BooruProducerPhase.Open"/>, i.e. never after a cut, so no
/// fresh authority replaces producer ownership. A use that was counted before the cut may still finish its terminal
/// write with its own (still unreleased) generation admission; the cut cancels <see cref="BooruProducerUse.Cut"/> so
/// every wait of that use (provider request, queue admission park) ends instead of pinning the drain. A new generation
/// opens only after the previous one was released, so no result of an older generation can overlap a newer one.
/// <see cref="RequireRecovery"/> and <see cref="StopAsync"/> are never reopened.
/// </para>
/// <para>
/// Not a kernel: the profile admission still decides every write. This type never grants a write the kernel would
/// refuse; it only decides when the family may start new work and keeps its single Ordinary reservation counted.
/// </para>
/// </summary>
internal sealed class BooruProducerLifecycle
{
    private readonly object _sync = new();
    private readonly ProfileAdmission _admission;
    // Internal fault seam used by core tests to exercise a detached terminal-release cleanup failure.
    // Production composition leaves it null; real drain/admission ownership is unchanged.
    private readonly Action? _terminalReleaseDisposeHook;
    private BooruProducerPhase _phase = BooruProducerPhase.Prepared;
    private ProfileProducerEpoch? _epoch;
    private CancellationTokenSource? _cut;
    private Task? _cutCancellation;
    private Task? _terminalReleaseTask;
    private long _generation;
    private TaskCompletionSource _changed = NewSignal();

    internal BooruProducerLifecycle(ProfileAdmission admission, Action? terminalReleaseDisposeHook = null)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _terminalReleaseDisposeHook = terminalReleaseDisposeHook;
    }

    /// <summary>Raised outside every lock after each phase or generation change.</summary>
    internal event Action? Changed;

    internal BooruProducerPhase Phase { get { lock (_sync) return _phase; } }

    /// <summary>The generation number of the last opened generation (0 before the first open).</summary>
    internal long Generation { get { lock (_sync) return _generation; } }

    /// <summary>True while a generation is open and no transition was announced.</summary>
    internal bool IsOpen { get { lock (_sync) return _phase == BooruProducerPhase.Open; } }

    /// <summary>True for <see cref="BooruProducerPhase.RecoveryRequired"/> and <see cref="BooruProducerPhase.Stopped"/>.</summary>
    internal bool IsClosedForGood
    {
        get { lock (_sync) return _phase is BooruProducerPhase.RecoveryRequired or BooruProducerPhase.Stopped; }
    }

    /// <summary>
    /// Opens a fresh generation: the first one when the profile became Ready, later ones after a SafeToResume
    /// exclusive outcome (after the participant's producer activation). Requires a released previous generation and a
    /// Ready profile without a reserved exclusive admission (the kernel refuses otherwise).
    /// </summary>
    internal void Open()
    {
        lock (_sync)
        {
            if (_phase is BooruProducerPhase.RecoveryRequired or BooruProducerPhase.Stopped)
                throw new InvalidOperationException("The Danbooru producer family is closed for this session.");
            if (_phase is BooruProducerPhase.Open or BooruProducerPhase.Transitioning)
                throw new InvalidOperationException("A Danbooru producer generation is already open.");
            if (_epoch != null) throw new InvalidOperationException("The previous Danbooru producer generation has not been released.");
            ProfileProducerEpoch epoch = new(_admission.EnterOrdinaryWriter());
            _epoch = epoch;
            _cut = new CancellationTokenSource();
            _generation++;
            _phase = BooruProducerPhase.Open;
        }
        RaiseChanged();
    }

    /// <summary>A counted use of the open generation, or null when nothing may start now.</summary>
    internal BooruProducerUse? TryEnter()
    {
        lock (_sync)
        {
            if (_phase != BooruProducerPhase.Open || _epoch is null || _cut is null) return null;
            return new BooruProducerUse(this, _generation, _epoch.Enter(), _cut.Token);
        }
    }

    /// <summary>
    /// Waits until a generation is open and enters it. Throws <see cref="InvalidOperationException"/> once the family
    /// is closed for good (recovery required, stopped); the caller's token cancels only this wait.
    /// </summary>
    internal async Task<BooruProducerUse> EnterAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task changed;
            lock (_sync)
            {
                if (_phase is BooruProducerPhase.RecoveryRequired or BooruProducerPhase.Stopped)
                    throw new InvalidOperationException("The Danbooru producer family is closed for this session.");
                if (_phase == BooruProducerPhase.Open && _epoch is not null && _cut is not null)
                    return new BooruProducerUse(this, _generation, _epoch.Enter(), _cut.Token);
                changed = _changed.Task;
            }
            await changed.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Announces a profile-wide exclusive transition before its admission reservation (participant
    /// CloseTransitionCommandIngress): nothing new starts, running uses keep their generation and are cut later.
    /// A refusal from the kernel that a use meets from now on is treated as caused by the transition, not as a
    /// local storage failure.
    /// </summary>
    internal void BeginTransition()
    {
        lock (_sync)
        {
            if (_phase != BooruProducerPhase.Open) return;
            _phase = BooruProducerPhase.Transitioning;
        }
        RaiseChanged();
    }

    /// <summary>The transition was refused before its cut: the same generation stays open.</summary>
    internal void AbortTransition()
    {
        lock (_sync)
        {
            if (_phase != BooruProducerPhase.Transitioning) return;
            _phase = BooruProducerPhase.Open;
        }
        RaiseChanged();
    }

    /// <summary>
    /// Synchronous cut, asynchronous drain. Nothing can enter after this returns; every use of the cut generation has
    /// its <see cref="BooruProducerUse.Cut"/> cancelled. The task completes once all of them were disposed. Call
    /// <see cref="ReleaseDrained"/> afterwards. A family without an open generation has nothing to drain.
    /// </summary>
    internal Task CutAndDrainAsync()
    {
        Task drained;
        Task cancelled;
        lock (_sync)
        {
            if (_phase is BooruProducerPhase.Prepared or BooruProducerPhase.Open or BooruProducerPhase.Transitioning)
                _phase = BooruProducerPhase.Suspended;
            drained = _epoch?.Close() ?? Task.CompletedTask;
            // A failed cancellation callback belongs to this cut and must be observed by its caller. Do not issue a
            // second cancellation from RequireRecovery/Stop: CancellationTokenSource cancellation callbacks are not
            // required to be idempotent, and a second call could create an unobserved failure.
            cancelled = EnsureCutCancellationLocked();
        }
        // The state above is already synchronous for every caller; callbacks of the cut run asynchronously so that
        // no check continuation executes on the participant's thread.
        RaiseChanged();
        return Task.WhenAll(cancelled, drained);
    }

    /// <summary>
    /// Releases the drained generation's Ordinary admission (the exclusive grant may follow). Throws while uses are
    /// still counted; never releases an open generation.
    /// </summary>
    internal void ReleaseDrained()
    {
        ProfileProducerEpoch? epoch;
        CancellationTokenSource? cut;
        lock (_sync)
        {
            epoch = _epoch;
            if (epoch is null) return;
            if (_terminalReleaseTask is not null)
                throw new InvalidOperationException("The Danbooru producer generation is being released by a terminal transition.");
            if (_phase is BooruProducerPhase.Open or BooruProducerPhase.Transitioning)
                throw new InvalidOperationException("An open Danbooru producer generation cannot be released.");
            if (!epoch.Close().IsCompletedSuccessfully)
                throw new InvalidOperationException("The Danbooru producer generation has not drained.");
            _epoch = null;
            cut = _cut;
            _cut = null;
            _cutCancellation = null;
        }
        // Outside our lock: the kernel completes a pending exclusive waiter on release.
        epoch.Dispose();
        cut?.Dispose();
        RaiseChanged();
    }

    /// <summary>
    /// The exclusive outcome or a failed peer drain requires recovery: the family stays closed for this session.
    /// A still counted generation keeps its ownership until its terminal drain releases it; <see cref="StopAsync"/>
    /// joins that same release when the session is stopping.
    /// </summary>
    internal void RequireRecovery()
    {
        TerminalRelease? release;
        lock (_sync)
        {
            if (_phase == BooruProducerPhase.Stopped) return;
            _phase = BooruProducerPhase.RecoveryRequired;
            release = PrepareTerminalReleaseLocked();
        }
        StartTerminalRelease(release);
        RaiseChanged();
    }

    /// <summary>
    /// Synchronous terminal fence of the session stop: nothing new starts, the generation is closed and running uses
    /// are cancelled. Its admission remains owned until the explicit <see cref="StopAsync"/> joins the terminal drain.
    /// </summary>
    internal void BeginStop()
    {
        lock (_sync)
        {
            if (_phase == BooruProducerPhase.Stopped && _epoch is null) return;
            _phase = BooruProducerPhase.Stopped;
            _epoch?.Close();
            EnsureCutCancellationLocked();
        }
        RaiseChanged();
    }

    /// <summary>Terminal stop: cut, wait for every counted use and release the last generation's admission.</summary>
    internal async Task StopAsync()
    {
        BeginStop();
        TerminalRelease? releaseToStart;
        Task release;
        lock (_sync)
        {
            // Recovery may already own this task. Stop joins it instead of starting a second cleanup or disposing
            // the same generation twice. A normal stop creates the task only now, after BeginStop's fence.
            releaseToStart = PrepareTerminalReleaseLocked();
            release = _terminalReleaseTask ?? Task.CompletedTask;
        }
        StartTerminalRelease(releaseToStart);
        await release.ConfigureAwait(false);
    }

    // Terminal recovery/stop owns the old generation until both its cancellation request and every counted use have
    // settled. A failed peer drain must not leave this Ordinary admission stranded, but it also must not release it
    // while a Booru use can still perform its terminal write. The detached task is observed even when the caller only
    // marks recovery and does not stop immediately; StopAsync awaits the same task and preserves any cancellation fault.
    private TerminalRelease? PrepareTerminalReleaseLocked()
    {
        if (_epoch is null || _terminalReleaseTask is not null) return null;
        Task cancellation = EnsureCutCancellationLocked();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _terminalReleaseTask = completion.Task;
        return new TerminalRelease(_epoch, _epoch.Close(), cancellation, completion);
    }

    private void StartTerminalRelease(TerminalRelease? release)
    {
        if (release is null) return;
        _ = CompleteTerminalReleaseAsync(release);
        // Detached recovery cleanup must remain observable for callers that do not immediately await StopAsync; the
        // terminal phase remains fail-closed rather than turning a cleanup fault into success.
        ObserveFault(release.Completion.Task, "terminal_release");
    }

    private async Task CompleteTerminalReleaseAsync(TerminalRelease release)
    {
        List<Exception>? errors = null;
        try { await release.Cancellation.ConfigureAwait(false); }
        catch (Exception error) { (errors ??= new()).Add(error); }
        try { await release.Drain.ConfigureAwait(false); }
        catch (Exception error) { (errors ??= new()).Add(error); }

        CancellationTokenSource? cut = null;
        bool canRelease = release.Drain.IsCompletedSuccessfully;
        if (canRelease)
        {
            lock (_sync)
            {
                // Only the terminal task created for this exact epoch may detach it. Stop and recovery share the
                // same task, so neither path can dispose the admission twice or release a successor generation.
                if (ReferenceEquals(_epoch, release.Epoch))
                {
                    _epoch = null;
                    cut = _cut;
                    _cut = null;
                    _cutCancellation = null;
                }
                else canRelease = false;
            }
        }
        if (canRelease)
        {
            try
            {
                release.Epoch.Dispose();
                cut?.Dispose();
                _terminalReleaseDisposeHook?.Invoke();
            }
            catch (Exception error) { (errors ??= new()).Add(error); }
        }

        if (errors is null && canRelease) release.Completion.TrySetResult();
        else
        {
            errors ??= new() { new InvalidOperationException("The Danbooru producer generation could not be released.") };
            release.Completion.TrySetException(errors.Count == 1 ? errors[0] : new AggregateException(
                "Terminal Danbooru producer cleanup reported failures.", errors));
        }
        RaiseChanged();
    }

    private sealed record TerminalRelease(ProfileProducerEpoch Epoch, Task Drain, Task Cancellation,
        TaskCompletionSource Completion);

    private Task EnsureCutCancellationLocked()
    {
        if (_cutCancellation is not null) return _cutCancellation;
        Task cancellation = _cut is null ? Task.CompletedTask : CancelAsync(_cut);
        _cutCancellation = cancellation;
        // BeginStop is intentionally a synchronous fence and has no task to return. Observe a callback failure even
        // during the interval before its owner calls StopAsync; that call still awaits this exact task and preserves
        // the original fault.
        ObserveFault(cancellation, "cut_cancellation");
        return cancellation;
    }

    private static void ObserveFault(Task task, string reason) => _ = task.ContinueWith(static (completed, state) =>
    {
        Exception? failure = completed.Exception?.GetBaseException();
        Log.Warning(
            "Danbooru producer lifecycle cleanup fault; reason={Reason} exceptionType={ExceptionType}",
            state, failure?.GetType().Name ?? "Unknown");
    }, reason, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    internal bool IsCurrentGeneration(long generation)
    {
        lock (_sync)
            return generation == _generation && _phase is BooruProducerPhase.Open or BooruProducerPhase.Transitioning;
    }

    internal bool IsUncutGeneration(long generation)
    {
        lock (_sync) return generation == _generation && _phase == BooruProducerPhase.Open;
    }

    private static async Task CancelAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync().ConfigureAwait(false); }
        catch (ObjectDisposedException) { }
    }

    private void RaiseChanged()
    {
        TaskCompletionSource changed;
        lock (_sync)
        {
            changed = _changed;
            _changed = NewSignal();
        }
        changed.TrySetResult();
        try { Changed?.Invoke(); }
        catch (Exception) { /* Observers never change the lifecycle. */ }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>
/// One counted execution in one generation. Dispose exactly once when the work (including its terminal write and
/// the disposal of its provider scope) has ended; the generation's drain waits for it.
/// </summary>
internal sealed class BooruProducerUse : IDisposable
{
    private readonly BooruProducerLifecycle _owner;
    private readonly ProfileProducerEpoch.Use _use;

    internal BooruProducerUse(BooruProducerLifecycle owner, long generation, ProfileProducerEpoch.Use use,
        CancellationToken cut)
    {
        _owner = owner;
        Generation = generation;
        _use = use;
        Cut = cut;
    }

    internal long Generation { get; }

    /// <summary>The generation's Ordinary admission; valid for writes until the generation is released.</summary>
    internal ProfileAdmissionToken Admission => _use.Admission
        ?? throw new InvalidOperationException("The Danbooru producer generation has no profile admission.");

    /// <summary>Cancelled by the cut, the recovery fence and the session stop.</summary>
    internal CancellationToken Cut { get; }

    /// <summary>
    /// False once this generation was cut, fenced or stopped (or a newer one exists): only a terminal transition of
    /// already accepted work may still be written then, never new progress.
    /// </summary>
    internal bool IsCurrent => _owner.IsCurrentGeneration(Generation);

    /// <summary>
    /// False as soon as a transition was announced (even before the cut): a kernel refusal is then attributed to
    /// the transition instead of to local storage.
    /// </summary>
    internal bool IsUncut => _owner.IsUncutGeneration(Generation);

    public void Dispose() => _use.Dispose();
}
