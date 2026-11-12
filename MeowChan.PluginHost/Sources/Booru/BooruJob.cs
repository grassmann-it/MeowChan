using System.Collections.Concurrent;
using MeowChan.PluginHost.Contracts;

namespace MeowChan.PluginHost.Sources.Booru;

/// <summary>
/// One bounded, host-owned lifetime for a booru search or post read.
/// <para>
/// The job owns a cancellation token that is linked to the optional operation lifetime
/// (caller cancellation, worker deadline, worker death), fires at the 120-second job
/// deadline and is cancelled by <see cref="Close"/>. Every wait and every transport
/// attempt of the job observes this token, so closing the job aborts waiting send-gate
/// admissions, retry delays, response-header waits and body reads, and no new send can
/// be reserved afterwards.
/// </para>
/// </summary>
internal sealed class BooruJob
{
    internal const string ResourcePrefix = "booru-job:";
    internal const int MaximumSends = 20;
    internal static readonly TimeSpan MaximumLifetime = TimeSpan.FromSeconds(120);

    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly DateTimeOffset _deadline;
    private readonly CancellationTokenSource _closeSource = new();
    private readonly CancellationTokenSource _deadlineSource;
    private readonly CancellationTokenSource _lifetime;
    private int _sends;
    private bool _closed;

    internal BooruJob(TimeProvider clock, CancellationToken operation = default)
    {
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        ResourceId = ResourcePrefix + Guid.NewGuid().ToString("N");
        Operation = new PluginOperationContext(ResourceId, 1);
        _deadline = clock.GetUtcNow() + MaximumLifetime;
        _deadlineSource = new CancellationTokenSource(MaximumLifetime, clock);
        _lifetime = operation.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(_closeSource.Token, _deadlineSource.Token, operation)
            : CancellationTokenSource.CreateLinkedTokenSource(_closeSource.Token, _deadlineSource.Token);
        // Cache the struct: it stays observable after Close disposed the sources.
        Token = _lifetime.Token;
    }

    internal string ResourceId { get; }
    internal PluginOperationContext Operation { get; }
    internal DateTimeOffset Deadline => _deadline;

    /// <summary>Cancelled by <see cref="Close"/>, at the job deadline, or by the linked operation lifetime.</summary>
    internal CancellationToken Token { get; }

    internal bool IsOpen
    {
        get { lock (_gate) return !_closed; }
    }

    internal bool IsExpired => _clock.GetUtcNow() >= _deadline;

    /// <summary>Accepted sends so far (initial attempts and retries).</summary>
    internal int Sends
    {
        get { lock (_gate) return _sends; }
    }

    internal bool TryReserveSend()
    {
        lock (_gate)
        {
            if (_closed || Token.IsCancellationRequested || _clock.GetUtcNow() >= _deadline || _sends >= MaximumSends)
                return false;
            ++_sends;
            return true;
        }
    }

    internal void Close()
    {
        lock (_gate)
        {
            if (_closed) return;
            _closed = true;
        }
        // Cancel outside the gate: registered callbacks abort in-flight waits and
        // transport calls synchronously and must not run under the reservation lock.
        try { _closeSource.Cancel(); }
        finally
        {
            _lifetime.Dispose();
            _deadlineSource.Dispose();
            _closeSource.Dispose();
        }
    }
}

/// <summary>Correlates only currently live host-created booru jobs.</summary>
internal sealed class BooruJobRegistry
{
    private readonly ConcurrentDictionary<string, BooruJob> _jobs = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    internal BooruJobRegistry(TimeProvider clock) => _clock = clock;

    internal BooruJob Open(CancellationToken operation = default)
    {
        BooruJob job;
        do
        {
            job = new BooruJob(_clock, operation);
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
