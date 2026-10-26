namespace MeowChan.PluginHost.Filters;

/// <summary>One lock for stage promises, accepted worker workspace quotas and observed leftovers.</summary>
internal sealed class FilterTemporaryBudget(long limit)
{
    internal object Sync { get; } = new();
    private long _limit = limit;
    private readonly Dictionary<object, long> _stages = new();
    private readonly Dictionary<object, long> _workers = new();
    private readonly Dictionary<object, long> _quotas = new();
    private long _stageBytes, _workerBytes, _observedWorkerBytes;
    internal long LimitBytes { get { lock (Sync) return _limit; } }
    internal void Limit(long value) { lock (Sync) _limit = Math.Min(_limit, value); }
    internal long WorkerBytes { get { lock (Sync) return _workerBytes; } }
    internal long ObservedWorkerBytes { get { lock (Sync) return _observedWorkerBytes; } }
    internal long Total { get { lock (Sync) return _stageBytes > long.MaxValue - _workerBytes ? long.MaxValue : _stageBytes + _workerBytes; } }
    internal bool Fits(long additional = 0) { lock (Sync) return additional >= 0 && Total <= _limit - additional; }
    internal void Stage(object owner, long bytes)
    {
        lock (Sync)
        {
            Set(_stages, owner, bytes); _stageBytes = 0;
            foreach (long charge in _stages.Values)
                _stageBytes = charge > long.MaxValue - _stageBytes ? long.MaxValue : _stageBytes + charge;
        }
    }
    internal bool AcceptWorker(object owner, long quota)
    {
        lock (Sync)
        {
            long before = Charge(owner);
            if (quota < 1 || !Fits(Math.Max(before, quota) - before)) return false;
            Set(_quotas, owner, Math.Max(_quotas.GetValueOrDefault(owner), quota));
            _workerBytes += Charge(owner) - before;
            return true;
        }
    }
    // Observations can only increase a live worker's charge above its accepted quota.
    // Empty scans, cancellation, completed calls and detached clients never free that quota.
    internal bool ObserveWorker(object owner, long bytes)
    {
        lock (Sync)
        {
            long before = Charge(owner);
            _observedWorkerBytes += Set(_workers, owner, bytes);
            _workerBytes += Charge(owner) - before;
            return Fits();
        }
    }
    internal void CompleteWorker(object owner, long remainingBytes, bool stoppedAndWorkspaceRemoved)
    {
        lock (Sync)
        {
            long before = Charge(owner);
            if (stoppedAndWorkspaceRemoved) Set(_quotas, owner, 0);
            _observedWorkerBytes += Set(_workers, owner, remainingBytes);
            _workerBytes += Charge(owner) - before;
        }
    }
    private long Charge(object owner) => Math.Max(_quotas.GetValueOrDefault(owner), _workers.GetValueOrDefault(owner));
    private static long Set(Dictionary<object, long> values, object owner, long bytes)
    {
        bytes = Math.Max(0, bytes);
        long delta = bytes - values.GetValueOrDefault(owner);
        if (bytes == 0) values.Remove(owner); else values[owner] = bytes;
        return delta;
    }
}
