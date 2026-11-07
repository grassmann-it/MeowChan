using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Persistence;

internal enum ProfileAdmissionPhase
{
    Starting,
    Ready,
    RecoveryFailed,
    Stopping,
    Stopped
}

internal enum ProfileAdmissionKind
{
    Startup,
    Ordinary,
    Exclusive
}

/// <summary>
/// In-memory cooperative admission kernel. The future lease owner must keep exactly
/// one instance and close/drain it before releasing the lease. This kernel alone is
/// neither a live lease check nor permission to apply, recover or mutate a store.
/// </summary>
internal sealed class ProfileAdmission : IDisposable
{
    private readonly object _sync = new();
    private readonly HashSet<ProfileAdmissionToken> _active = new();
    private readonly Dictionary<ProfileAdmissionToken, TokenUse> _uses = new();
    private ProfileAdmissionPhase _phase = ProfileAdmissionPhase.Starting;
    private bool _closed;
    private bool _exclusiveActive;
    private ExclusiveWaiter? _pending;
    private TaskCompletionSource<bool>? _drained;

    internal ProfileAdmission() { }

    internal ProfileAdmissionPhase Phase
    {
        get { lock (_sync) return _phase; }
    }

    internal ProfileAdmissionToken EnterStartupWriter(CancellationToken token = default)
    {
        lock (_sync)
        {
            EnsureOpen();
            RequirePhase(ProfileAdmissionPhase.Starting);
            token.ThrowIfCancellationRequested();
            return AcceptToken(ProfileAdmissionKind.Startup);
        }
    }

    internal ProfileAdmissionToken EnterOrdinaryWriter(CancellationToken token = default)
    {
        lock (_sync)
        {
            EnsureOpen();
            RequirePhase(ProfileAdmissionPhase.Ready);
            token.ThrowIfCancellationRequested();
            if (_pending is not null || _exclusiveActive)
                throw new InvalidOperationException("Exclusive profile admission is reserved.");
            return AcceptToken(ProfileAdmissionKind.Ordinary);
        }
    }

    // Compatibility entry for individual standalone store calls. The phase
    // choice and reservation are one operation, never a Phase/read-then-enter race.
    internal ProfileAdmissionToken EnterStoreWriter(CancellationToken token = default)
    {
        lock (_sync)
        {
            EnsureOpen();
            token.ThrowIfCancellationRequested();
            if (_phase == ProfileAdmissionPhase.Starting)
                return AcceptToken(ProfileAdmissionKind.Startup);
            RequirePhase(ProfileAdmissionPhase.Ready);
            if (_pending is not null || _exclusiveActive)
                throw new InvalidOperationException("Exclusive profile admission is reserved.");
            return AcceptToken(ProfileAdmissionKind.Ordinary);
        }
    }

    // Pin an already accepted reservation through synchronous consumer I/O.
    // No delegate runs under _sync, and pending Exclusive/shutdown do not revoke
    // accepted work. A release request rejects new uses but drains existing ones.
    internal T RunWithToken<T>(ProfileAdmissionToken admission, Func<T> operation,
        params ProfileAdmissionKind[] allowedKinds)
    {
        ArgumentNullException.ThrowIfNull(operation);
        lock (_sync)
        {
            if (admission is null || !_uses.TryGetValue(admission, out TokenUse? use)
                || use.ReleaseRequested || allowedKinds is null
                || Array.IndexOf(allowedKinds, admission.Kind) < 0)
                throw new InvalidOperationException("The profile admission token cannot be used for this operation.");
            use.Count = checked(use.Count + 1);
        }
        try { return operation(); }
        finally { EndUse(admission); }
    }

    /// <summary>
    /// Reserves the only exclusive slot. The caller must observe the returned
    /// ValueTask exactly once and dispose every granted token, even after shutdown
    /// or cancellation. To abandon a still-pending request, cancel its supplied
    /// token and observe completion; dropping the ValueTask does not cancel it.
    /// </summary>
    internal ValueTask<ProfileAdmissionToken> EnterExclusiveAsync(CancellationToken token = default)
    {
        ExclusiveWaiter waiter;
        lock (_sync)
        {
            EnsureOpen();
            RequirePhase(ProfileAdmissionPhase.Ready);
            token.ThrowIfCancellationRequested();
            if (_pending is not null || _exclusiveActive)
                throw new InvalidOperationException("Exclusive profile admission is reserved.");
            if (_active.Count == 0)
                return ValueTask.FromResult(AcceptToken(ProfileAdmissionKind.Exclusive));
            waiter = new ExclusiveWaiter(this, token);
            _pending = waiter;
        }

        // Register outside the monitor: registration of an already-cancelled token
        // invokes its callback synchronously. Grant/shutdown may already have won.
        return WaitForExclusiveAsync(waiter);
    }

    internal void CompleteStartup()
    {
        lock (_sync)
        {
            EnsureOpen();
            RequirePhase(ProfileAdmissionPhase.Starting);
            if (_active.Count != 0)
                throw new InvalidOperationException("Startup writers are still active.");
            _phase = ProfileAdmissionPhase.Ready;
        }
    }

    internal void FailStartup()
    {
        lock (_sync)
        {
            EnsureOpen();
            if (_phase == ProfileAdmissionPhase.RecoveryFailed)
                return;
            RequirePhase(ProfileAdmissionPhase.Starting);
            _phase = ProfileAdmissionPhase.RecoveryFailed;
        }
    }

    // Irreversible live-recovery fence. This seals ingress, not accepted ownership:
    // active tokens and synchronous pins keep their normal counted lifetime.
    // Stop/Close always win, including a late failure notification after Stop.
    internal void FailRecovery()
    {
        ExclusiveWaiter? waiter;
        lock (_sync)
        {
            if (_phase is ProfileAdmissionPhase.Stopping or ProfileAdmissionPhase.Stopped)
                return;
            _phase = ProfileAdmissionPhase.RecoveryFailed;
            waiter = _pending;
            _pending = null;
        }
        // Never run a waiter continuation or an observer under the monitor.
        waiter?.Completion.TrySetException(new InvalidOperationException("Profile recovery is required."));
    }

    internal void BeginShutdown()
    {
        ExclusiveWaiter? waiter;
        lock (_sync)
            waiter = Seal();
        RejectForShutdown(waiter);
    }

    /// <summary>
    /// Drain is meaningful only after sealing. Cancelling a drain cancels just that
    /// observer, never an accepted token or another observer's drain.
    /// </summary>
    internal Task DrainAsync(CancellationToken token = default)
    {
        Task drain;
        lock (_sync)
        {
            if (_phase != ProfileAdmissionPhase.Stopping && _phase != ProfileAdmissionPhase.Stopped)
                throw new InvalidOperationException("Profile admission must be stopping before drain.");
            token.ThrowIfCancellationRequested();
            if (_active.Count == 0)
                return Task.CompletedTask;
            _drained ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            drain = _drained.Task;
        }
        return token.CanBeCanceled ? drain.WaitAsync(token) : drain;
    }

    internal void CompleteShutdown()
    {
        lock (_sync)
        {
            RequirePhase(ProfileAdmissionPhase.Stopping);
            if (_active.Count != 0)
                throw new InvalidOperationException("Accepted profile admissions are still active.");
            _phase = ProfileAdmissionPhase.Stopped;
        }
    }

    /// <summary>Seal only; accepted tokens still own their release and must be drained.</summary>
    void IDisposable.Dispose() => Close();

    internal void Close()
    {
        ExclusiveWaiter? waiter;
        lock (_sync)
        {
            _closed = true;
            waiter = Seal();
        }
        RejectForShutdown(waiter);
    }

    // Called only under _sync; an issued token is counted before it can escape.
    private ProfileAdmissionToken AcceptToken(ProfileAdmissionKind kind)
    {
        ProfileAdmissionToken accepted = ProfileAdmissionToken.Create(this, kind);
        _active.Add(accepted);
        _uses.Add(accepted, new TokenUse());
        if (kind == ProfileAdmissionKind.Exclusive)
            _exclusiveActive = true;
        return accepted;
    }

    internal void Release(ProfileAdmissionToken token)
    {
        TokenCompletion completion = default;
        lock (_sync)
        {
            // Dispose and use acceptance linearize under the same monitor.
            // Foreign/unissued/retired tokens cannot release another reservation.
            if (!_uses.TryGetValue(token, out TokenUse? use) || use.ReleaseRequested)
                return;
            use.ReleaseRequested = true;
            if (use.Count == 0)
                completion = RetireTokenUnderLock(token);
        }
        completion.Complete();
    }

    private void EndUse(ProfileAdmissionToken token)
    {
        TokenCompletion completion = default;
        lock (_sync)
        {
            TokenUse use = _uses[token];
            use.Count--;
            if (use.Count == 0 && use.ReleaseRequested)
                completion = RetireTokenUnderLock(token);
        }
        completion.Complete();
    }

    // Caller owns _sync. Only the last pin can retire a release-requested token.
    private TokenCompletion RetireTokenUnderLock(ProfileAdmissionToken token)
    {
        _uses.Remove(token);
        _active.Remove(token);
        if (token.Kind == ProfileAdmissionKind.Exclusive)
            _exclusiveActive = false;
        ExclusiveWaiter? waiter = null;
        ProfileAdmissionToken? granted = null;
        if (_active.Count == 0 && _pending is not null)
        {
            waiter = _pending;
            _pending = null;
            // Linearize cancellation vs grant here, not when the await resumes.
            if (!waiter.Token.IsCancellationRequested)
                granted = AcceptToken(ProfileAdmissionKind.Exclusive);
        }
        return new TokenCompletion(waiter, granted, _active.Count == 0 ? _drained : null);
    }

    private sealed class TokenUse
    {
        internal int Count;
        internal bool ReleaseRequested;
    }

    private readonly record struct TokenCompletion(ExclusiveWaiter? Waiter,
        ProfileAdmissionToken? Granted, TaskCompletionSource<bool>? Drained)
    {
        internal void Complete()
        {
            if (Granted is not null)
                Waiter!.Completion.TrySetResult(Granted);
            else if (Waiter is not null)
                Waiter.Completion.TrySetCanceled(Waiter.Token);
            Drained?.TrySetResult(true);
        }
    }

    private static async ValueTask<ProfileAdmissionToken> WaitForExclusiveAsync(ExclusiveWaiter waiter)
    {
        using CancellationTokenRegistration registration = waiter.Token.UnsafeRegister(
            static state =>
            {
                ExclusiveWaiter pending = (ExclusiveWaiter)state!;
                pending.Owner.CancelExclusive(pending);
            }, waiter);
        return await waiter.Completion.Task.ConfigureAwait(false);
    }

    private void CancelExclusive(ExclusiveWaiter waiter)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(_pending, waiter))
                return;
            _pending = null;
        }
        waiter.Completion.TrySetCanceled(waiter.Token);
    }

    // Caller holds _sync. No token is revoked, and no waiter is completed here.
    private ExclusiveWaiter? Seal()
    {
        if (_phase != ProfileAdmissionPhase.Stopped)
            _phase = ProfileAdmissionPhase.Stopping;
        ExclusiveWaiter? waiter = _pending;
        _pending = null;
        return waiter;
    }

    private static void RejectForShutdown(ExclusiveWaiter? waiter) =>
        waiter?.Completion.TrySetException(new InvalidOperationException("Profile admission is stopping."));

    private void EnsureOpen() => ObjectDisposedException.ThrowIf(_closed, this);

    private void RequirePhase(ProfileAdmissionPhase phase)
    {
        if (_phase != phase)
            throw new InvalidOperationException("Profile admission is not in the required phase.");
    }

    private sealed class ExclusiveWaiter
    {
        internal ExclusiveWaiter(ProfileAdmission owner, CancellationToken token)
        {
            Owner = owner;
            Token = token;
        }

        internal ProfileAdmission Owner { get; }
        internal CancellationToken Token { get; }
        internal TaskCompletionSource<ProfileAdmissionToken> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

/// <summary>
/// Bound to exactly one admission instance. Cancellation after acceptance does not
/// release it: the accepted operation's owner must dispose it, including on failure.
/// </summary>
internal sealed class ProfileAdmissionToken : IDisposable
{
    private readonly ProfileAdmission _owner;

    private ProfileAdmissionToken(ProfileAdmission owner, ProfileAdmissionKind kind)
    {
        _owner = owner;
        Kind = kind;
    }

    internal ProfileAdmissionKind Kind { get; }

    // Allocation alone grants nothing; only ProfileAdmission's exact active set does.
    internal static ProfileAdmissionToken Create(ProfileAdmission owner, ProfileAdmissionKind kind) => new(owner, kind);

    public void Dispose() => _owner.Release(this);
}
