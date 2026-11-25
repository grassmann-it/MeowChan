using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MeowChan.Notifications;

/// <summary>
/// Delivers state invalidations asynchronously with one active invocation and one
/// pending invalidation per subscription. A subscription cannot delay another.
/// </summary>
internal sealed class CoalescedEvent<THandler> : IDisposable where THandler : Delegate
{
    private readonly object _lifetime = new object();
    private readonly CoalescedSubscriptions<THandler> _subscriptions;
    private bool _disposed;

    public CoalescedEvent(Action<THandler> invoke, Action<Exception>? reportFailure = null)
    {
        _subscriptions = new CoalescedSubscriptions<THandler>(invoke, reportFailure);
    }

    public void Add(THandler? handler)
    {
        if (handler == null) return;
        lock (_lifetime)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscriptions.Add(handler);
        }
    }

    public void Remove(THandler? handler) => _subscriptions.Remove(handler);

    public void Signal() => _subscriptions.Signal();

    public void Dispose()
    {
        lock (_lifetime)
        {
            if (_disposed) return;
            _disposed = true;
            _subscriptions.Clear();
        }
    }
}

/// <summary>Reusable observer registrations. Removing a handler retires its pending delivery.</summary>
internal sealed class CoalescedSubscriptions<THandler> where THandler : Delegate
{
    private readonly object _gate = new object();
    private readonly List<Subscription> _subscriptions = new List<Subscription>();
    private readonly Action<THandler> _invoke;
    private readonly Action<Exception>? _reportFailure;

    public CoalescedSubscriptions(Action<THandler> invoke, Action<Exception>? reportFailure = null)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        _invoke = invoke;
        _reportFailure = reportFailure;
    }

    public void Add(THandler? handler)
    {
        if (handler == null) return;
        lock (_gate)
        {
            foreach (Delegate invocation in handler.GetInvocationList())
                _subscriptions.Add(new Subscription((THandler)invocation, _invoke, _reportFailure));
        }
    }

    public void Remove(THandler? handler)
    {
        if (handler == null) return;
        lock (_gate)
        {
            Delegate[] invocations = handler.GetInvocationList();
            for (int start = _subscriptions.Count - invocations.Length; start >= 0; start--)
            {
                bool matches = true;
                for (int offset = 0; offset < invocations.Length; offset++)
                {
                    if (_subscriptions[start + offset].Handler.Equals(invocations[offset])) continue;
                    matches = false;
                    break;
                }
                if (!matches) continue;
                for (int offset = 0; offset < invocations.Length; offset++)
                    _subscriptions[start + offset].Retire();
                _subscriptions.RemoveRange(start, invocations.Length);
                return;
            }
        }
    }

    public void Signal()
    {
        Subscription[] subscriptions;
        lock (_gate)
            subscriptions = _subscriptions.ToArray();
        foreach (Subscription subscription in subscriptions) subscription.Signal();
    }

    internal void Clear()
    {
        lock (_gate)
        {
            foreach (Subscription subscription in _subscriptions) subscription.Retire();
            _subscriptions.Clear();
        }
    }

    private sealed class Subscription
    {
        private readonly object _gate = new object();
        private readonly Action<THandler> _invoke;
        private readonly Action<Exception>? _reportFailure;
        private bool _pending;
        private bool _running;
        private bool _retired;

        public THandler Handler { get; }

        public Subscription(THandler handler, Action<THandler> invoke, Action<Exception>? reportFailure)
        {
            Handler = handler;
            _invoke = invoke;
            _reportFailure = reportFailure;
        }

        public void Signal()
        {
            lock (_gate)
            {
                if (_retired) return;
                _pending = true;
                if (_running) return;
                _running = true;
            }
            _ = Task.Run(Drain);
        }

        private void Drain()
        {
            while (true)
            {
                lock (_gate)
                {
                    if (_retired || !_pending)
                    {
                        _running = false;
                        return;
                    }
                    _pending = false;
                }

                try { _invoke(Handler); }
                catch (Exception exception)
                {
                    try { _reportFailure?.Invoke(exception); }
                    catch (Exception) { /* A diagnostic sink cannot fault the delivery task. */ }
                }
            }
        }

        public void Retire()
        {
            lock (_gate)
            {
                _retired = true;
                _pending = false;
            }
        }
    }
}
