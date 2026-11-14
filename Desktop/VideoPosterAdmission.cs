using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>
/// Bounded admission for poster requests: at most <see cref="ActiveLimit"/> requests do verification/decode work at once, at most
/// <see cref="QueueLimit"/> further requests wait for a slot (FIFO as provided by <see cref="SemaphoreSlim"/>), and anything beyond that
/// is refused immediately. Waiting observes only the caller's token, never an internal watchdog, so a burst of visible tiles queues up
/// instead of losing its posters, while the amount of queued work stays bounded.
/// </summary>
internal sealed class VideoPosterAdmission
{
    private readonly SemaphoreSlim _active;
    private int _waiting;

    internal VideoPosterAdmission(int activeLimit, int queueLimit)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(activeLimit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(queueLimit);
        ActiveLimit = activeLimit;
        QueueLimit = queueLimit;
        _active = new SemaphoreSlim(activeLimit, activeLimit);
    }

    internal int ActiveLimit { get; }
    internal int QueueLimit { get; }
    /// <summary>Requests currently waiting for a slot (tests, diagnostics).</summary>
    internal int Waiting => Volatile.Read(ref _waiting);
    /// <summary>Free work slots (tests, diagnostics).</summary>
    internal int Available => _active.CurrentCount;

    /// <summary>
    /// True once a slot is held (release it with <see cref="Exit"/> exactly once); false when the bounded queue is full.
    /// Caller cancellation while waiting throws <see cref="OperationCanceledException"/> and holds no slot.
    /// </summary>
    internal async ValueTask<bool> TryEnterAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (_active.Wait(0)) return true;
        if (Interlocked.Increment(ref _waiting) > QueueLimit)
        {
            Interlocked.Decrement(ref _waiting);
            return false;
        }
        try
        {
            await _active.WaitAsync(token).ConfigureAwait(false);
            return true;
        }
        finally { Interlocked.Decrement(ref _waiting); }
    }

    internal void Exit() => _active.Release();
}
