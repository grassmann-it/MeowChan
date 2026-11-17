using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>One running operation and one replaceable desired operation. Cancellation
/// never releases the worker until the actual operation has exited.</summary>
internal sealed class GalleryDecodeQueue : IDisposable
{
    private sealed record Work(Func<CancellationToken, Task> Run, bool Debounce,
        TaskCompletionSource Completion);
    private Work? _desired;
    private CancellationTokenSource? _active;
    private bool _running, _disposed;
    internal Task Completion { get; private set; } = Task.CompletedTask;
    private readonly Func<CancellationToken, Task> _delay;

    internal GalleryDecodeQueue(Func<CancellationToken, Task>? delay = null) =>
        _delay = delay ?? (token => Task.Delay(150, token));

    internal Task Replace(Func<CancellationToken, Task> run, bool debounce)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel();
        Work work = new(run, debounce, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _desired = work;
        if (!_running) Completion = PumpAsync();
        return work.Completion.Task;
    }

    internal void Cancel()
    {
        _desired?.Completion.TrySetResult();
        _desired = null;
        _active?.Cancel();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cancel(); // The pump's using owns _active until even non-cooperative work exits.
    }

    private async Task PumpAsync()
    {
        _running = true;
        try
        {
            while (_desired is { } work)
            {
                _desired = null;
                using CancellationTokenSource active = new();
                _active = active;
                try
                {
                    if (work.Debounce) await _delay(active.Token);
                    active.Token.ThrowIfCancellationRequested();
                    await work.Run(active.Token);
                    work.Completion.TrySetResult();
                }
                catch (OperationCanceledException) when (active.IsCancellationRequested)
                { work.Completion.TrySetResult(); }
                catch (Exception error) { work.Completion.TrySetException(error); }
                finally { _active = null; }
            }
        }
        finally { _running = false; }
    }
}
