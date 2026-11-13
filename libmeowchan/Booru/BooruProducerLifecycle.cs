using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Persistence;

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
    private BooruProducerPhase _phase = BooruProducerPhase.Prepared;
    private ProfileProducerEpoch? _epoch;
    private CancellationTokenSource? _cut;
    private long _generation;
    private TaskCompletionSource _changed = NewSignal();

    internal BooruProducerLifecycle(ProfileAdmission admission) =>
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));

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
        CancellationTokenSource? cut;
        lock (_sync)
        {
            if (_phase is BooruProducerPhase.Prepared or BooruProducerPhase.Open or BooruProducerPhase.Transitioning)
                _phase = BooruProducerPhase.Suspended;
            cut = _cut;
            drained = _epoch?.Close() ?? Task.CompletedTask;
        }
        // The state above is already synchronous for every caller; callbacks of the cut run asynchronously so that
        // no check continuation executes on the participant's thread.
        Task cancelled = cut is null ? Task.CompletedTask : CancelAsync(cut);
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
            if (_phase is BooruProducerPhase.Open or BooruProducerPhase.Transitioning)
                throw new InvalidOperationException("An open Danbooru producer generation cannot be released.");
            if (!epoch.Close().IsCompletedSuccessfully)
                throw new InvalidOperationException("The Danbooru producer generation has not drained.");
            _epoch = null;
            cut = _cut;
            _cut = null;
        }
        // Outside our lock: the kernel completes a pending exclusive waiter on release.
        epoch.Dispose();
        cut?.Dispose();
        RaiseChanged();
    }

    /// <summary>
    /// The exclusive outcome requires recovery: the family stays closed for this session. A still counted generation
    /// keeps its ownership until <see cref="StopAsync"/> drains and releases it.
    /// </summary>
    internal void RequireRecovery()
    {
        CancellationTokenSource? cut;
        lock (_sync)
        {
            if (_phase == BooruProducerPhase.Stopped) return;
            _phase = BooruProducerPhase.RecoveryRequired;
            cut = _cut;
            _epoch?.Close();
        }
        if (cut != null) _ = CancelAsync(cut);
        RaiseChanged();
    }

    /// <summary>Synchronous terminal mark of the session stop: nothing new starts, running uses are cancelled.</summary>
    internal void BeginStop()
    {
        CancellationTokenSource? cut;
        lock (_sync)
        {
            if (_phase == BooruProducerPhase.Stopped && _epoch is null) return;
            _phase = BooruProducerPhase.Stopped;
            cut = _cut;
            _epoch?.Close();
        }
        if (cut != null) _ = CancelAsync(cut);
        RaiseChanged();
    }

    /// <summary>Terminal stop: cut, wait for every counted use and release the last generation's admission.</summary>
    internal async Task StopAsync()
    {
        BeginStop();
        Task drained;
        lock (_sync) drained = _epoch?.Close() ?? Task.CompletedTask;
        await drained.ConfigureAwait(false);
        ReleaseDrained();
    }

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
