namespace MeowChan.Tests;

    internal sealed class BudgetTime : TimeProvider
    {
        private readonly object _sync = new object();
        private readonly List<ManualTimer> _timers = new List<ManualTimer>();
        private long _ticks;
        private static readonly DateTimeOffset Start = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_sync) { return _ticks; } }
        public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(GetTimestamp());

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualTimer timer = new ManualTimer(this, callback, state);
            lock (_sync)
            {
                _timers.Add(timer);
                timer.ChangeLocked(dueTime, period);
            }
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            List<ManualTimer> due;
            lock (_sync)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToList();
                foreach (ManualTimer timer in due)
                    timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : _ticks + timer.Period.Ticks;
            }
            foreach (ManualTimer timer in due) timer.Callback(timer.State);
        }

        public Task WaitForTimerAtAsync(TimeSpan elapsed) => TestWait.UntilAsync(() =>
        {
            lock (_sync) { return _timers.Any(timer => !timer.Disposed && timer.Due == elapsed.Ticks); }
        }, $"No policy timer was scheduled for {elapsed}.");

        private sealed class ManualTimer(BudgetTime owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; set; }
            public TimeSpan Period { get; private set; }
            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._sync)
                {
                    if (Disposed) return false;
                    ChangeLocked(dueTime, period);
                    return true;
                }
            }

            public void ChangeLocked(TimeSpan dueTime, TimeSpan period)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period;
            }

            public void Dispose() { lock (owner._sync) { Disposed = true; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
