using System;
using System.Threading;
using Avalonia.Threading;

namespace MeowChan.Desktop.Controls;

/// <summary>
/// Trailing-edge debounce for one search query: every request restarts the delay, so only the last one in a
/// burst of keystrokes runs. A superseded or cancelled timer callback carries an old ticket and does nothing,
/// even when it was already queued while the newer request arrived. Time and thread hand-over are injectable.
/// </summary>
internal sealed class QueryDebounce : IDisposable
{
    private readonly Action _action;
    private readonly TimeProvider _clock;
    private readonly Action<Action> _post;
    private readonly object _sync = new();
    private ITimer? _timer;
    private long _ticket;
    private bool _pending, _disposed;

    internal QueryDebounce(Action action, TimeProvider? clock = null, Action<Action>? post = null)
    {
        _action = action ?? throw new ArgumentNullException(nameof(action));
        _clock = clock ?? TimeProvider.System;
        _post = post ?? (callback => Dispatcher.UIThread.Post(callback, DispatcherPriority.Normal));
    }

    internal bool IsPending { get { lock (_sync) return _pending; } }

    /// <summary>Runs the action once, <paramref name="delay"/> after the last call.</summary>
    internal void Request(TimeSpan delay)
    {
        long ticket;
        lock (_sync)
        {
            if (_disposed) return;
            ticket = ++_ticket;
            _pending = true;
            _timer?.Dispose();
            _timer = null;
            // Arm only after the field is assigned: a callback that fires at once must find its owner.
            ITimer timer = _clock.CreateTimer(_ => Elapsed(ticket), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer = timer;
            timer.Change(delay, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Drops a waiting run. Returns whether one was waiting.</summary>
    internal bool Cancel()
    {
        lock (_sync) return Retire();
    }

    /// <summary>Runs a waiting action now instead of at its time. Returns whether one was waiting.</summary>
    internal bool Flush()
    {
        lock (_sync) { if (!Retire()) return false; }
        _action();
        return true;
    }

    private bool Retire()
    {
        bool was = _pending;
        ++_ticket;
        _pending = false;
        _timer?.Dispose();
        _timer = null;
        return was;
    }

    private void Elapsed(long ticket)
    {
        lock (_sync) { if (_disposed || !_pending || ticket != _ticket) return; }
        _post(() => Run(ticket));
    }

    private void Run(long ticket)
    {
        lock (_sync)
        {
            if (_disposed || !_pending || ticket != _ticket) return;
            _pending = false;
            _timer?.Dispose();
            _timer = null;
        }
        _action();
    }

    public void Dispose()
    {
        lock (_sync) { _disposed = true; Retire(); }
    }
}
