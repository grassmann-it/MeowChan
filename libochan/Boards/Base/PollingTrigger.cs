using System;
using System.Threading;
using System.Threading.Tasks;
using oChan.Interfaces;

namespace oChan.Boards.Base;

/// <summary>Coalesces explicit checks without creating a second provider polling loop.</summary>
internal sealed class PollingTrigger
{
    private readonly object _gate = new object();
    private bool _pending;
    private TaskCompletionSource _requested = NewSignal();
    private static TaskCompletionSource NewSignal() => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    internal CheckRequestStatus Request()
    {
        lock (_gate)
        {
            if (_pending) return CheckRequestStatus.AlreadyScheduled;
            _pending = true;
            _requested.TrySetResult();
            return CheckRequestStatus.Scheduled;
        }
    }

    internal void Consume()
    {
        lock (_gate)
        {
            _pending = false;
            _requested = NewSignal();
        }
    }

    internal Task WaitForRequestAsync(CancellationToken cancellationToken)
    {
        lock (_gate) { return _requested.Task.WaitAsync(cancellationToken); }
    }

    // Always drain the losing delay; provider/test clocks must not retain an orphan waiter.
    internal async Task<bool> WaitOrDelayAsync(Func<CancellationToken, Task> delay, CancellationToken cancellationToken)
    {
        Task requested;
        lock (_gate)
        {
            if (_pending) return true;
            requested = _requested.Task;
        }
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task delayed = delay(waiting.Token);
        Task winner = await Task.WhenAny(requested, delayed).ConfigureAwait(false);
        if (winner == delayed)
        {
            await delayed.ConfigureAwait(false);
            return false;
        }
        waiting.Cancel();
        try { await delayed.ConfigureAwait(false); }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested) { }
        cancellationToken.ThrowIfCancellationRequested();
        return true;
    }
}
