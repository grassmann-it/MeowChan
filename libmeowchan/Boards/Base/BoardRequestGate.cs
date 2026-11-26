using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Boards.Base;

// Boards and providers have no disposal lifetime. Only the active request owns
// a completion signal, and cancelling a waiter leaves that request alone.
internal sealed class BoardRequestGate
{
    private readonly object _sync = new();
    private TaskCompletionSource? _active;

    internal async Task EnterAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task active;
            lock (_sync)
            {
                if (_active == null)
                {
                    _active = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    return;
                }
                active = _active.Task;
            }
            await active.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal void Exit()
    {
        lock (_sync)
        {
            _active!.SetResult();
            _active = null;
        }
    }
}
