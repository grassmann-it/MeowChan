using System.Collections.Concurrent;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.PluginHost.Sources.Booru;

/// <summary>One bounded, host-owned lifetime for a booru search or post read.</summary>
internal sealed class BooruJob
{
    internal const string ResourcePrefix = "booru-job:";
    internal const int MaximumSends = 20;
    internal static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(120);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _deadline;
    private int _sends;
    private bool _closed;

    internal BooruJob(TimeProvider clock)
    {
        _clock = clock;
        ResourceId = ResourcePrefix + Guid.NewGuid().ToString("N");
        Operation = new PluginOperationContext(ResourceId, 1);
        _deadline = clock.GetUtcNow() + MaximumLifetime;
    }

    internal string ResourceId { get; }
    internal PluginOperationContext Operation { get; }
    internal DateTimeOffset Deadline => _deadline;

    internal bool IsOpen
    {
        get { lock (_gate) return !_closed; }
    }

    internal bool IsExpired => _clock.GetUtcNow() >= _deadline;

    internal bool TryReserveSend()
    {
        lock (_gate)
        {
            if (_closed || _clock.GetUtcNow() >= _deadline || _sends >= MaximumSends) return false;
            ++_sends;
            return true;
        }
    }

    internal void Close()
    {
        lock (_gate) _closed = true;
    }
}

/// <summary>Correlates only currently live host-created booru jobs.</summary>
internal sealed class BooruJobRegistry
{
    private readonly ConcurrentDictionary<string, BooruJob> _jobs = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    internal BooruJobRegistry(TimeProvider clock) => _clock = clock;

    internal BooruJob Open()
    {
        BooruJob job;
        do
        {
            job = new BooruJob(_clock);
        }
        while (!_jobs.TryAdd(job.ResourceId, job));
        return job;
    }

    internal bool TryGet(string resourceId, out BooruJob? job)
    {
        if (_jobs.TryGetValue(resourceId, out job) && job.IsOpen) return true;
        job = null;
        return false;
    }

    internal void Close(BooruJob job)
    {
        job.Close();
        _jobs.TryRemove(new KeyValuePair<string, BooruJob>(job.ResourceId, job));
    }
}
