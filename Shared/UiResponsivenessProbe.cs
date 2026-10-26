using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace MeowChan.Diagnostics;

/// <summary>One answered UI ping. Times are monotonic stopwatch timestamps.</summary>
internal readonly record struct UiPingSample(long Sequence, long SentAt, long ExecutedAt, double LagMilliseconds);

/// <summary>Evidence captured when a ping stays unanswered past the hard deadline.</summary>
internal sealed record UiStallReport(long Sequence, double UnansweredMilliseconds, string WatchdogStack, DateTimeOffset At);

/// <summary>Aggregated statistics. Percentiles of tiny series are descriptive only, never a product budget.</summary>
internal sealed record UiResponsivenessSummary(int Samples, long DroppedSamples, double MedianLagMilliseconds,
    double P95LagMilliseconds, double MaxLagMilliseconds, double MaxGapMilliseconds, double OngoingSilenceMilliseconds,
    long Pings, long HardStalls, double MeanLagMilliseconds = 0, double StdDevLagMilliseconds = 0);

/// <summary>
/// Measures UI responsiveness from outside the UI thread (PF-00). A dedicated thread sends at most one outstanding
/// ping through <c>postToUi</c>; the UI answers by recording when the ping actually ran. Because the clock and the
/// watchdog live on their own thread, a completely blocked UI is observed (growing silence, hard-stall report) instead of
/// being invisible. The buffer is bounded and counts what it drops. It takes no product locks and never touches the UI itself.
/// </summary>
internal sealed class UiResponsivenessProbe : IDisposable
{
    private readonly Action<Action> _postToUi;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _hardDeadline;
    private readonly Action<UiStallReport>? _onHardStall;
    private readonly int _capacity;
    private readonly Action<UiPingSample>? _onAnswer;
    private int _disposed;
    private readonly object _sync = new();
    private readonly Queue<UiPingSample> _samples = new();
    private readonly ManualResetEventSlim _stop = new(false);
    private readonly Thread _thread;
    private long _sequence, _dropped, _pings, _hardStalls;
    private long _outstandingSequence, _outstandingSentAt;
    private long _lastExecutedAt, _maxGapTicks;
    private bool _hardStallReported;
    private bool _started;

    public UiResponsivenessProbe(Action<Action> postToUi, TimeSpan interval, TimeSpan hardDeadline,
        Action<UiStallReport>? onHardStall = null, int capacity = 8192, Action<UiPingSample>? onAnswer = null)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (hardDeadline <= interval) throw new ArgumentOutOfRangeException(nameof(hardDeadline));
        if (capacity < 16) throw new ArgumentOutOfRangeException(nameof(capacity));
        _onAnswer = onAnswer;
        _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));
        _interval = interval; _hardDeadline = hardDeadline; _onHardStall = onHardStall; _capacity = capacity;
        _thread = new Thread(Run) { IsBackground = true, Name = "MeowChan UI responsiveness probe" };
    }

    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_started) return;
            _started = true;
            _lastExecutedAt = Stopwatch.GetTimestamp();
            _thread.Start();
        }
    }

    private void Run()
    {
        while (!_stop.IsSet)
        {
            long now = Stopwatch.GetTimestamp();
            long sequence = 0;
            bool send = false;
            lock (_sync)
            {
                if (_outstandingSequence == 0)
                {
                    sequence = ++_sequence;
                    _outstandingSequence = sequence; _outstandingSentAt = now; _hardStallReported = false;
                    send = true;
                }
            }
            if (send)
            {
                Interlocked.Increment(ref _pings);
                try { _postToUi(() => Answer(sequence, now)); }
                catch (Exception) { lock (_sync) { if (_outstandingSequence == sequence) _outstandingSequence = 0; } }
            }
            else CheckHardDeadline(now);
            if (_stop.Wait(_interval)) return;
        }
    }

    private void Answer(long sequence, long sentAt)
    {
        long executed = Stopwatch.GetTimestamp();
        UiPingSample sample = new(sequence, sentAt, executed, Stopwatch.GetElapsedTime(sentAt, executed).TotalMilliseconds);
        lock (_sync)
        {
            if (_disposed != 0 || _outstandingSequence != sequence) return;
            long gap = executed - _lastExecutedAt;
            if (gap > _maxGapTicks) _maxGapTicks = gap;
            _lastExecutedAt = executed;
            if (_outstandingSequence == sequence) _outstandingSequence = 0;
            if (_samples.Count >= _capacity) { _samples.Dequeue(); _dropped++; }
            _samples.Enqueue(sample);
        }
        try { _onAnswer?.Invoke(sample); } catch (Exception) { /* Observers cannot break the dispatcher. */ }
    }

    private void CheckHardDeadline(long now)
    {
        UiStallReport? report = null;
        lock (_sync)
        {
            if (_outstandingSequence == 0 || _hardStallReported) return;
            TimeSpan age = Stopwatch.GetElapsedTime(_outstandingSentAt, now);
            if (age < _hardDeadline) return;
            _hardStallReported = true; _hardStalls++;
            report = new UiStallReport(_outstandingSequence, age.TotalMilliseconds, Environment.StackTrace, DateTimeOffset.UtcNow);
        }
        try { _onHardStall?.Invoke(report); } catch (Exception) { /* Diagnostics must never become the failure. */ }
    }

    public IReadOnlyList<UiPingSample> Samples { get { lock (_sync) return _samples.ToArray(); } }

    public UiResponsivenessSummary Summarize()
    {
        UiPingSample[] samples;
        long dropped, maxGap, last;
        lock (_sync) { samples = _samples.ToArray(); dropped = _dropped; maxGap = _maxGapTicks; last = _lastExecutedAt; }
        double[] lags = samples.Select(sample => sample.LagMilliseconds).OrderBy(value => value).ToArray();
        double Percentile(double fraction) => lags.Length == 0 ? 0 : lags[Math.Max(0, (int)Math.Ceiling(fraction * lags.Length) - 1)];
        double ongoing = _started ? Stopwatch.GetElapsedTime(last).TotalMilliseconds : 0;
        double mean = lags.Length == 0 ? 0 : lags.Average();
        double deviation = lags.Length == 0 ? 0 : Math.Sqrt(lags.Average(value => (value - mean) * (value - mean)));
        return new UiResponsivenessSummary(samples.Length, dropped, Percentile(0.5), Percentile(0.95),
            lags.Length == 0 ? 0 : lags[^1], Stopwatch.GetElapsedTime(0, maxGap).TotalMilliseconds, ongoing,
            Interlocked.Read(ref _pings), Interlocked.Read(ref _hardStalls), mean, deviation);
    }

    public void Dispose()
    {
        bool started;
        lock (_sync)
        {
            if (_disposed != 0) return;
            _disposed = 1;
            started = _started;
            _stop.Set();
        }
        // A thread that does not end in time is a background thread and ends with the process; do not dispose under it.
        if (!started || (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(2)))) _stop.Dispose();
    }
}
