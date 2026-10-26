using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>
/// Dispatcher-owned asynchronous work: one active operation and one replaceable latest request.
/// An invalidation does not cancel useful active work. Cancel clears the pending request and signals
/// the active token once, retaining ownership until even non-cooperative I/O actually returns.
/// All entry points and the operation's continuations belong to the same UI synchronization context.
/// </summary>
internal sealed class LatestUiWork<T>(Func<T, CancellationToken, Task> run, CancellationToken lifetime = default) where T : class
{
    private T? _pending;
    private CancellationTokenSource? _active;
    private TaskCompletionSource? _completion;
    internal bool IsRunning => _completion != null;
    internal bool HasPending => _pending != null;
    internal long Starts { get; private set; }
    internal long Cancellations { get; private set; }
    internal long Replacements { get; private set; }

    internal Task Request(T request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (lifetime.IsCancellationRequested) return Task.CompletedTask;
        if (_pending != null) Replacements++;
        _pending = request;
        if (_completion != null) return _completion.Task;
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        _ = DrainAsync(completion);
        return completion.Task;
    }

    internal void Cancel()
    {
        _pending = null;
        if (_active is not { IsCancellationRequested: false }) return;
        Cancellations++;
        _active.Cancel();
    }

    private async Task DrainAsync(TaskCompletionSource completion)
    {
        Exception? failure = null;
        try
        {
            while (_pending is T request && !lifetime.IsCancellationRequested)
            {
                _pending = null;
                using CancellationTokenSource active = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                _active = active;
                Starts++;
                try { await run(request, active.Token); }
                catch (OperationCanceledException) when (active.IsCancellationRequested) { }
                catch (Exception error) { failure ??= error; } // Drain the latest request even after failure.
                finally { _active = null; }
            }
        }
        finally
        {
            _pending = null;
            _completion = null;
            if (failure == null) completion.TrySetResult();
            else completion.TrySetException(failure);
        }
    }
}
