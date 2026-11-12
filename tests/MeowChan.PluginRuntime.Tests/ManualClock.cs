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
        if (dueTime <= TimeSpan.Zero) entry.Fire();
        return entry;
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
        public bool Change(TimeSpan dueTime, TimeSpan period) { _due = owner.GetUtcNow().Ticks + Math.Max(0, dueTime.Ticks); return !Disposed; }
        internal void Fire() { if (Disposed || Due > owner.GetUtcNow()) return; if (period == Timeout.InfiniteTimeSpan) Disposed = true; else _due = owner.GetUtcNow().Ticks + period.Ticks; callback(state); }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
