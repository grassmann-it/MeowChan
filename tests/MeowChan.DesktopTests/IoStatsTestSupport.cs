using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>A clock and timer factory under the test's control: time moves only through <see cref="Advance"/>, in UTC.</summary>
internal sealed class IoManualClock(DateTimeOffset startUtc) : TimeProvider
{
    private long _ticks;
    private readonly List<ManualTimer> _timers = [];

    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override DateTimeOffset GetUtcNow() => startUtc + TimeSpan.FromTicks(_ticks);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    internal int ActiveTimers { get { lock (_timers) return _timers.Count(timer => timer.Due != null); } }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        lock (_timers) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Moves time forward and fires every timer that falls due, in time order, each at its own due time.</summary>
    internal void Advance(TimeSpan duration)
    {
        long target = _ticks + duration.Ticks;
        while (true)
        {
            ManualTimer? next;
            lock (_timers) next = _timers.Where(timer => timer.Due is long due && due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
            if (next == null) break;
            _ticks = Math.Max(_ticks, next.Due!.Value);
            next.Fire();
        }
        _ticks = target;
    }

    private sealed class ManualTimer(IoManualClock owner, TimerCallback callback, object? state) : ITimer
    {
        internal long? Due;
        private long _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner._ticks + dueTime.Ticks;
            _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            return true;
        }

        internal void Fire()
        {
            Due = _period > 0 ? Due + _period : null;
            callback(state);
        }

        public void Dispose() => Due = null;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}

/// <summary>Counters the test moves by hand; keys are read like the transport meter's.</summary>
internal sealed class IoFakeSource : IIoCounterSource
{
    private readonly Dictionary<string, IoCounterReading> _readings = new(StringComparer.Ordinal);
    internal int Reads;

    // Append the optional upload parameter so existing positional disk/request arguments keep their meaning.
    internal void Add(string key, long net = 0, long diskWritten = 0, long diskRead = 0, long requests = 0, long errors = 0, long netUp = 0)
    {
        _readings.TryGetValue(key, out IoCounterReading r);
        _readings[key] = new IoCounterReading(Key: key, NetDown: r.NetDown + net, NetUp: r.NetUp + netUp, DiskWritten: r.DiskWritten + diskWritten, DiskRead: r.DiskRead + diskRead, Requests: r.Requests + requests, Errors: r.Errors + errors);
    }

    internal void Set(string key, long net, long netUp = 0, long diskWritten = 0, long diskRead = 0, long requests = 0, long errors = 0)
        => _readings[key] = new IoCounterReading(Key: key, NetDown: net, NetUp: netUp, DiskWritten: diskWritten, DiskRead: diskRead, Requests: requests, Errors: errors);

    public void Read(List<IoCounterReading> into)
    {
        Reads++;
        into.AddRange(_readings.Values);
    }
}
