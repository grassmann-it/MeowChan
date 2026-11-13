using System;
using System.Threading;
using System.Threading.Tasks;
using MeowChan.Booru;
using Serilog;

namespace MeowChan.Desktop.Booru;

/// <summary>
/// Revokes escaped UI handles, not downloads already owned by the shared queue. A lease belongs to exactly one
/// enabled generation. Close is synchronous; cancellation callbacks run outside the lock. Open never revives a lease.
/// The optional predicate also fails closed before the runtime's availability notification reaches composition.
/// </summary>
internal sealed class BooruPortAvailabilityGate
{
    private readonly object _sync = new();
    private readonly Func<bool> _isEnabled;
    private CancellationTokenSource? _generation;
    private bool _stopped;
    private int _leases;
    private TaskCompletionSource _idle = CompletedSignal();

    private static TaskCompletionSource CompletedSignal()
    {
        TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        signal.SetResult();
        return signal;
    }

    internal BooruPortAvailabilityGate(Func<bool>? isEnabled = null)
    {
        _isEnabled = isEnabled ?? (() => true);
        if (_isEnabled()) _generation = new CancellationTokenSource();
    }

    internal bool IsStopped { get { lock (_sync) return _stopped; } }

    internal bool IsAvailable
    {
        get { lock (_sync) return !_stopped && _generation != null && _isEnabled(); }
    }

    internal Lease? TryEnter(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            if (_stopped || _generation == null || !_isEnabled()) return null;
            Lease lease = new(this, _generation, cancellationToken);
            if (_leases++ == 0) _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return lease;
        }
    }

    internal void Close(bool terminal = false)
    {
        CancellationTokenSource? old;
        lock (_sync)
        {
            _stopped |= terminal;
            old = _generation;
            _generation = null;
        }
        // No token is handed to an accepted download's lifetime. This cancels only the admission wait.
        if (old == null) return;
        try { old.Cancel(); }
        catch (AggregateException exception)
        {
            // A consumer's cancellation callback cannot prevent composition from stopping the old scheduler.
            Log.Warning(exception, "Danbooru availability cancellation observer failed");
        }
        finally { old.Dispose(); }
    }

    internal Task DrainAsync()
    {
        lock (_sync) return _idle.Task;
    }

    internal void Open()
    {
        lock (_sync)
            if (!_stopped && _generation == null && _leases == 0 && _isEnabled())
                _generation = new CancellationTokenSource();
    }

    internal sealed class Lease : IDisposable
    {
        private readonly BooruPortAvailabilityGate _owner;
        private readonly CancellationTokenSource _generation;
        private readonly CancellationTokenSource _linked;
        private int _disposed;

        internal Lease(BooruPortAvailabilityGate owner, CancellationTokenSource generation, CancellationToken caller)
        {
            _owner = owner;
            _generation = generation;
            _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, generation.Token);
        }

        internal CancellationToken Token => _linked.Token;
        private bool CurrentLocked => !_owner._stopped && ReferenceEquals(_owner._generation, _generation)
            && _owner._isEnabled() && !Token.IsCancellationRequested;
        internal bool IsCurrent { get { lock (_owner._sync) return CurrentLocked; } }

        /// <summary>Linearizes a synchronous mutation / asynchronous operation's start against Close.</summary>
        internal bool TryStart<T>(Func<T> start, out T result)
        {
            lock (_owner._sync)
            {
                if (!CurrentLocked) { result = default!; return false; }
                result = start();
                return true;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _linked.Dispose();
            lock (_owner._sync)
                if (--_owner._leases == 0) _owner._idle.TrySetResult();
        }
    }
}

/// <summary>
/// A check keeps one availability generation as well as its existing bounded B1 search scope. A worker ignoring
/// cancellation is physically drained by the watcher host; its late page never reaches scheduler observation/admission.
/// </summary>
internal sealed class BooruAvailabilityWatcherSearchPort(IDanbooruWatcherSearchPort inner, BooruPortAvailabilityGate gate)
    : IDanbooruWatcherSearchPort
{
    public int PageSize => inner.PageSize;

    public DateTimeOffset? GetProviderCooldownUntilUtc() => inner.GetProviderCooldownUntilUtc();

    public IDanbooruWatcherSearchSession OpenCheck() => new Session(inner, gate.TryEnter(CancellationToken.None));

    private sealed class Session(IDanbooruWatcherSearchPort inner, BooruPortAvailabilityGate.Lease? lease)
        : IDanbooruWatcherSearchSession
    {
        private IDanbooruWatcherSearchSession? _session;

        public async Task<DanbooruWatcherSearchOutcome> SearchAsync(string providerQuery, string? beforePostId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lease == null) return new DanbooruWatcherSearchUnavailable(BooruCodes.SourceUnavailable);
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Token);
            if (!lease.TryStart(() => (_session ??= inner.OpenCheck()).SearchAsync(providerQuery, beforePostId, linked.Token),
                out Task<DanbooruWatcherSearchOutcome> pending))
                return new DanbooruWatcherSearchUnavailable(BooruCodes.SourceUnavailable);
            DanbooruWatcherSearchOutcome result = await pending.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return lease.IsCurrent ? result : new DanbooruWatcherSearchUnavailable(BooruCodes.SourceUnavailable);
        }

        public void Dispose()
        {
            try { _session?.Dispose(); }
            finally { lease?.Dispose(); }
        }
    }
}
