using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Desktop;

/// <summary>
/// One load at a time in which the newest request wins (the favorites overview's scope loads): starting a request cancels the running one, waits for
/// it to unwind and only then starts its own work. The wait matters because the host accepts one favorites resolution at a time and refuses a second
/// as "busy"; a cancelled load that is still unwinding would otherwise turn the newer scope into an error. A request that was superseded or whose
/// lifetime ended returns <c>null</c> and never reports a failure, so only the newest request can publish a result or an error and an older scope's
/// list can never end up under the newer scope's title. Safe to call from any thread; <c>work</c> continues in the caller's context.
/// </summary>
internal sealed class LatestWinsLoad
{
    private readonly object _gate = new();
    private readonly CancellationToken _lifetime;
    private CancellationTokenSource? _current;
    private Task _previous = Task.CompletedTask;
    private int _generation;

    /// <param name="lifetime">Ends every request (the dialog closed, the page ended).</param>
    internal LatestWinsLoad(CancellationToken lifetime) => _lifetime = lifetime;

    /// <summary>Runs <paramref name="work"/> as the newest request: its result, or null when a newer request or the lifetime superseded it.</summary>
    internal async Task<T?> RunAsync<T>(Func<CancellationToken, Task<T>> work) where T : class
    {
        ArgumentNullException.ThrowIfNull(work);
        TaskCompletionSource unwound = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource mine = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        CancellationTokenSource? older;
        Task previous;
        int request;
        lock (_gate)
        {
            request = ++_generation;
            older = _current;
            _current = mine;
            previous = _previous;
            _previous = unwound.Task;
        }
        // Outside the lock: cancelling runs the older request's registrations. It may have finished and disposed itself meanwhile.
        try { older?.Cancel(); } catch (ObjectDisposedException) { }
        try
        {
            // The older request unwinds first (it observes its cancellation and returns), so the host never sees two at once.
            await previous;
            if (IsSuperseded(request, mine)) return null;
            T result = await work(mine.Token);
            return IsSuperseded(request, mine) ? null : result;
        }
        catch (OperationCanceledException) when (IsSuperseded(request, mine)) { return null; }
        catch (Exception) when (Volatile.Read(ref _generation) != request) { return null; }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_current, mine)) _current = null;
                mine.Dispose();
            }
            unwound.TrySetResult();
        }
    }

    private bool IsSuperseded(int request, CancellationTokenSource mine) =>
        Volatile.Read(ref _generation) != request || mine.IsCancellationRequested;
}
