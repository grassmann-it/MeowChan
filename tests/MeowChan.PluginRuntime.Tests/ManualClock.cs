namespace MeowChan.PluginRuntime.Tests;

/// <summary>Deterministic clock: timers fire only when <see cref="Advance"/> passes their due time.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly object _gate = new();
    private long _ticks;
    private readonly List<Entry> _timers = [];
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 11, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        Entry entry = new(this, callback, state, dueTime, period);
        lock (_gate) _timers.Add(entry);
        // Fire() ignores a timer that is not due yet; calling it unconditionally also covers an Advance that passed the due time
        // between the entry's construction and its registration, which the Advance snapshot would otherwise never see.
        entry.Fire();
        return entry;
    }
    /// <summary>True while a not yet disposed timer has exactly <paramref name="remaining"/> left on this frozen clock.</summary>
    internal bool HasTimer(TimeSpan remaining)
    {
        lock (_gate)
        {
            DateTimeOffset now = GetUtcNow();
            return _timers.Any(timer => !timer.Disposed && timer.Due - now == remaining);
        }
    }
    /// <summary>
    /// Waits until the code under test is parked on a timer with exactly <paramref name="remaining"/> left, so that a following
    /// <see cref="Advance"/> cannot overtake the moment the delay's due time is computed. The limit only guards against a hang.
    /// </summary>
    internal async Task WaitForTimerAsync(TimeSpan remaining, TimeSpan? limit = null)
    {
        using CancellationTokenSource guard = new(limit ?? TimeSpan.FromSeconds(30));
        while (!HasTimer(remaining))
        {
            if (guard.IsCancellationRequested) throw new TimeoutException("No timer with " + remaining + " remaining was registered.");
            await Task.Delay(1);
        }
    }
    internal void Advance(TimeSpan amount)
    {
        Interlocked.Add(ref _ticks, amount.Ticks);
        Entry[] due;
        lock (_gate) due = _timers.Where(timer => !timer.Disposed && timer.Due <= GetUtcNow()).ToArray();
        foreach (Entry timer in due) timer.Fire();
    }
    private sealed class Entry(ManualClock owner, TimerCallback callback, object? state, TimeSpan due, TimeSpan period) : ITimer
    {
        private long _due = owner.GetUtcNow().Ticks + Math.Max(0, due.Ticks);
        internal bool Disposed { get; private set; }
        internal DateTimeOffset Due => new(_due, TimeSpan.Zero);
        public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner._gate) { _due = owner.GetUtcNow().Ticks + Math.Max(0, dueTime.Ticks); return !Disposed; } }
        internal void Fire()
        {
            // Claimed under the gate so that a concurrent Advance and the registration check cannot both run a one-shot callback.
            lock (owner._gate)
            {
                if (Disposed || Due > owner.GetUtcNow()) return;
                if (period == Timeout.InfiniteTimeSpan) Disposed = true; else _due = owner.GetUtcNow().Ticks + period.Ticks;
            }
            callback(state);
        }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
