using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan;

public sealed partial class Registry
{
    // Dispose can race a previously accepted operation. Waiters and holders keep the semaphore
    // alive until they cancel or release it, while disposal closes the gate to new operations.
    private sealed class OperationGate : IDisposable
    {
        private readonly object _sync = new();
        private readonly SemaphoreSlim _semaphore = new(1, 1);
        private int _users;
        private bool _closing;

        internal Task WaitAsync(CancellationToken token = default)
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_closing, typeof(Registry));
                Task wait = _semaphore.WaitAsync(token);
                _users++;
                return wait.IsCompletedSuccessfully ? wait : FinishWaitAsync(wait);
            }
        }

        private async Task FinishWaitAsync(Task wait)
        {
            try { await wait.ConfigureAwait(false); }
            catch
            {
                lock (_sync) RetireUser();
                throw;
            }
        }

        internal void Release()
        {
            lock (_sync)
            {
                _semaphore.Release();
                RetireUser();
            }
        }

        private void RetireUser()
        {
            if (--_users == 0 && _closing) _semaphore.Dispose();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_closing) return;
                _closing = true;
                if (_users == 0) _semaphore.Dispose();
            }
        }
    }
}
