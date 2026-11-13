using System;

namespace MeowChan.Gallery;

/// <summary>
/// Forwards at most one report per interval to <c>target</c>, plus the first report and every report
/// <c>final</c> marks as the end of a phase, so a long loop that reports per item does not post thousands of status
/// updates to the UI thread. A report dropped in between is superseded by a later one; nothing is queued.
/// </summary>
internal sealed class ThrottledProgress<T> : IProgress<T>
{
    private readonly IProgress<T> _target;
    private readonly TimeSpan _interval;
    private readonly Func<T, bool>? _final;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private long? _last;

    internal ThrottledProgress(IProgress<T> target, TimeSpan interval, Func<T, bool>? final = null, TimeProvider? time = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        if (interval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _interval = interval;
        _final = final;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How many reports reached the target; lets a test see the throttle.</summary>
    internal int Forwarded { get; private set; }

    public void Report(T value)
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            bool due = _last is not { } last || _time.GetElapsedTime(last, now) >= _interval || _final?.Invoke(value) == true;
            if (!due) return;
            _last = now;
            Forwarded++;
        }
        _target.Report(value);
    }
}
