using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>
/// Managed-only asynchronous mutex. Retired video commands must still serialize and reject their
/// generation, even after terminal cleanup, so the gate deliberately outlives that cleanup.
/// Unlike SemaphoreSlim this has no disposable wait handle to retain or retire under late callers.
/// </summary>
internal sealed class VideoOperationGate
{
    private readonly object _state = new();
    private readonly Queue<TaskCompletionSource> _waiting = new();
    private bool _held;

    internal Task WaitAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_state)
        {
            if (!_held) { _held = true; return Task.CompletedTask; }
            TaskCompletionSource waiter = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiting.Enqueue(waiter);
            return WaitQueuedAsync(waiter.Task, cancellationToken);
        }
    }

    private async Task WaitQueuedAsync(Task admitted, CancellationToken token)
    {
        try { await admitted.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Cancellation cannot release the current native operation. Return this waiter's
            // baton only when its predecessor really exits, without retaining a token registration.
            _ = ReleaseCancelledAsync(admitted);
            throw;
        }
    }

    private async Task ReleaseCancelledAsync(Task admitted)
    {
        await admitted.ConfigureAwait(false);
        Release();
    }

    internal void Release()
    {
        TaskCompletionSource? next;
        lock (_state)
        {
            if (!_held) throw new InvalidOperationException("The video gate is not held.");
            if (!_waiting.TryDequeue(out next)) _held = false;
        }
        next?.SetResult();
    }
}
