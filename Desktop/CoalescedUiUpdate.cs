using System;
using System.Threading;
using Avalonia.Threading;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

/// <summary>
/// One current UI application, optionally spaced for progress. Requests during an application retain
/// one follow-up. Timer and dispatcher callbacks carry a ticket: superseded callbacks cannot consume
/// a later request, even when cancellation raced with delivery.
/// </summary>
internal sealed class CoalescedUiUpdate : IDisposable
{
    private readonly Action _update;
    private readonly TimeSpan _minimumInterval;
    private readonly TimeProvider _clock;
    private readonly Action<Action, DispatcherPriority> _post;
    private readonly object _sync = new();
    private ITimer? _timer;
    private bool _pending, _waiting, _running, _urgent, _disposed, _hasApplied;
    private readonly long _diagnosticId = PerformanceDiagnostics.NextId();
    private long _ticket, _lastAppliedAt;
    private long _applications, _requests, _suppressedRequests, _longestApplicationTicks;

    public CoalescedUiUpdate(Action update) : this(update, TimeSpan.Zero) { }

    public CoalescedUiUpdate(Action update, TimeSpan minimumInterval, TimeProvider? clock = null,
        Action<Action, DispatcherPriority>? post = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (minimumInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));
        _update = update;
        _minimumInterval = minimumInterval;
        _clock = clock ?? TimeProvider.System;
        _post = post ?? ((action, priority) => Dispatcher.UIThread.Post(action, priority));
    }

    public long Applications => Interlocked.Read(ref _applications);
    public long Requests => Interlocked.Read(ref _requests);
    public long SuppressedRequests => Interlocked.Read(ref _suppressedRequests);
    public TimeSpan LongestApplication => TimeSpan.FromTicks(Interlocked.Read(ref _longestApplicationTicks));

    public void Request() => Schedule(immediate: false);
    public void RequestImmediate() => Schedule(immediate: true);

    private void Schedule(bool immediate)
    {
        long ticket;
        DispatcherPriority priority;
        lock (_sync)
        {
            if (_disposed) return;
            _requests++;
            if (_pending)
            {
                _suppressedRequests++;
                // Promote even an already posted background callback. Its ticket becomes stale.
                if (!immediate || _urgent) return;
            }
            _pending = true;
            _urgent |= immediate;
            if (_running) return;
            if (!PrepareLocked(out ticket, out priority)) return;
        }
        Post(ticket, priority);
    }

    // Returns true when a dispatcher post is needed; otherwise the single timer owns this ticket.
    private bool PrepareLocked(out long ticket, out DispatcherPriority priority)
    {
        ticket = ++_ticket;
        _timer?.Dispose();
        _timer = null;
        TimeSpan delay = _urgent ? TimeSpan.Zero : DelayUntilNextApplication();
        priority = _urgent || _minimumInterval == TimeSpan.Zero ? DispatcherPriority.Normal : DispatcherPriority.Background;
        _waiting = delay > TimeSpan.Zero;
        if (!_waiting) return true;
        long captured = ticket;
        // Arm only after assigning the timer: a concurrently firing callback must see its owner.
        _timer = _clock.CreateTimer(_ => TimerElapsed(captured), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _timer.Change(delay, Timeout.InfiniteTimeSpan);
        return false;
    }

    private TimeSpan DelayUntilNextApplication()
    {
        if (_minimumInterval == TimeSpan.Zero || !_hasApplied) return TimeSpan.Zero;
        TimeSpan elapsed = _clock.GetElapsedTime(_lastAppliedAt);
        return elapsed >= _minimumInterval ? TimeSpan.Zero : _minimumInterval - elapsed;
    }

    private void TimerElapsed(long ticket)
    {
        lock (_sync)
        {
            if (_disposed || !_pending || !_waiting || ticket != _ticket) return;
            _waiting = false;
            _timer?.Dispose();
            _timer = null;
        }
        Post(ticket, DispatcherPriority.Background);
    }

    private void Post(long ticket, DispatcherPriority priority)
    {
        try { _post(() => Apply(ticket), priority); }
        catch
        {
            lock (_sync)
                if (ticket == _ticket) { _pending = false; _urgent = false; }
            throw;
        }
    }

    private void Apply(long ticket)
    {
        long started;
        lock (_sync)
        {
            if (_disposed || !_pending || _running || _waiting || ticket != _ticket) return;
            _pending = false;
            _urgent = false;
            _running = true;
            _lastAppliedAt = started = _clock.GetTimestamp();
            _hasApplied = true;
        }
        using var measurement = PerformanceDiagnostics.Measure("ui.coalesced.apply", _diagnosticId, revision: ticket);
        try { _update(); }
        finally
        {
            bool post = false;
            long next = 0;
            DispatcherPriority priority = default;
            lock (_sync)
            {
                _applications++;
                _longestApplicationTicks = Math.Max(_longestApplicationTicks, _clock.GetElapsedTime(started).Ticks);
                _running = false;
                if (!_disposed && _pending) post = PrepareLocked(out next, out priority);
            }
            if (post) Post(next, priority);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            ++_ticket;
            _pending = _waiting = _urgent = false;
            _timer?.Dispose();
            _timer = null;
        }
    }
}
