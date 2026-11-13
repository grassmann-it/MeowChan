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
    /// <summary>Default slack of <see cref="HasTimer"/>: absorbs a due time computed from a rounded or second clock reading.</summary>
    internal static readonly TimeSpan DefaultTolerance = TimeSpan.FromMilliseconds(1);

    /// <summary>True while a not yet disposed timer has <paramref name="remaining"/> left (± <paramref name="tolerance"/>).</summary>
    internal bool HasTimer(TimeSpan remaining, TimeSpan? tolerance = null)
    {
        TimeSpan slack = tolerance ?? DefaultTolerance;
        return HasTimerBetween(remaining - slack, remaining + slack);
    }

    /// <summary>True while a not yet disposed timer has between <paramref name="minimum"/> and <paramref name="maximum"/> left.</summary>
    internal bool HasTimerBetween(TimeSpan minimum, TimeSpan maximum)
    {
        lock (_gate)
        {
            DateTimeOffset now = GetUtcNow();
            return _timers.Any(timer => !timer.Disposed && timer.Due - now >= minimum && timer.Due - now <= maximum);
        }
    }

    /// <summary>What the pending timers have left, for a timeout message that names the actual delays.</summary>
    internal string DescribeTimers()
    {
        lock (_gate)
        {
            DateTimeOffset now = GetUtcNow();
            TimeSpan[] left = _timers.Where(timer => !timer.Disposed).Select(timer => timer.Due - now).ToArray();
            return left.Length == 0 ? "no pending timer" : "pending timers with " + string.Join(", ", left) + " left";
        }
    }

    /// <summary>
    /// Waits until the code under test is parked on a timer with <paramref name="remaining"/> left (± <paramref name="tolerance"/>), so
    /// that a following <see cref="Advance"/> cannot overtake the moment the delay's due time is computed. The limit only guards
    /// against a hang; its message lists the timers that were registered instead.
    /// </summary>
    internal Task WaitForTimerAsync(TimeSpan remaining, TimeSpan? limit = null, TimeSpan? tolerance = null)
    {
        TimeSpan slack = tolerance ?? DefaultTolerance;
        return WaitForTimerBetweenAsync(remaining - slack, remaining + slack, limit);
    }

    /// <summary>Range form of <see cref="WaitForTimerAsync"/>, e.g. for a retry delay with jitter.</summary>
    internal async Task WaitForTimerBetweenAsync(TimeSpan minimum, TimeSpan maximum, TimeSpan? limit = null)
    {
        using CancellationTokenSource guard = new(limit ?? TimeSpan.FromSeconds(30));
        while (!HasTimerBetween(minimum, maximum))
        {
            if (guard.IsCancellationRequested)
                throw new TimeoutException($"No timer with {minimum} to {maximum} remaining was registered ({DescribeTimers()}).");
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
