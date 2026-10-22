namespace MeowChan.PluginHost.Runtime;

/// <summary>Bounded FIFO admission. Cancellation removes its node immediately.</summary>
internal sealed class AsyncAdmission
{
    private readonly object _sync = new();
    private readonly int _maximum;
    private readonly int _queueLimit;
    private int _active;
    private readonly LinkedList<Waiter> _waiting = new();
    public AsyncAdmission(int maximum, int queueLimit) { _maximum = maximum; _queueLimit = queueLimit; }
    public Task<IDisposable> EnterAsync(CancellationToken token)
    {
        lock (_sync)
        {
            token.ThrowIfCancellationRequested();
            if (_active < _maximum) { _active++; return Task.FromResult<IDisposable>(new Lease(this)); }
            if (_waiting.Count >= _queueLimit) throw new Contracts.PluginWorkerException("capacity", "The bounded plugin admission queue is full.");
            Waiter waiter = new();
            LinkedListNode<Waiter> node = _waiting.AddLast(waiter);
            waiter.Registration = token.Register(() => Cancel(node, token));
            return waiter.Completion.Task;
        }
    }
    private void Cancel(LinkedListNode<Waiter> node, CancellationToken token)
    {
        lock (_sync)
        {
            if (node.List == null) return;
            _waiting.Remove(node);
            node.Value.Completion.TrySetCanceled(token);
        }
    }
    private void Release()
    {
        Waiter? next = null;
        lock (_sync)
        {
            if (_waiting.First is { } node) { _waiting.RemoveFirst(); next = node.Value; }
            else _active--;
        }
        if (next != null) { next.Registration.Dispose(); next.Completion.TrySetResult(new Lease(this)); }
    }
    private sealed class Waiter
    {
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationTokenRegistration Registration { get; set; }
    }
    private sealed class Lease(AsyncAdmission owner) : IDisposable
    {
        private AsyncAdmission? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
