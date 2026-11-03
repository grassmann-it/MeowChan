using System;
using System.Threading;
using System.Threading.Tasks;

namespace MeowChan.Persistence;

// Counted ownership of one producer generation, not a store-default admission.
// The owner closes ingress, drains monitors plus these accepted executions, then
// releases the reservation. Parked queue descriptors never own a Use.
internal sealed class ProfileProducerEpoch(ProfileAdmissionToken? admission) : IDisposable
{
    private readonly object _sync = new();
    private bool _closed;
    private int _executions;
    private TaskCompletionSource _drained = Completed();
    internal ProfileAdmissionToken? Admission { get; } = admission;

    internal Use Enter()
    {
        lock (_sync)
        {
            if (_closed) throw new InvalidOperationException("Producer epoch ingress is closed.");
            if (_executions++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Use(this);
        }
    }
    internal Task Close()
    {
        lock (_sync) { _closed = true; return _drained.Task; }
    }
    private void Leave()
    {
        lock (_sync) if (--_executions == 0) _drained.TrySetResult();
    }
    public void Dispose()
    {
        lock (_sync)
        {
            if (!_closed || _executions != 0) throw new InvalidOperationException("Producer epoch has not drained.");
        }
        Admission?.Dispose();
    }
    private static TaskCompletionSource Completed()
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
    internal sealed class Use(ProfileProducerEpoch owner) : IDisposable
    {
        private int _released;
        internal ProfileAdmissionToken? Admission => owner.Admission;
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) owner.Leave(); }
    }
}
