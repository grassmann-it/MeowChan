using System;
using System.Diagnostics;
using System.Threading;

namespace MeowChan.Diagnostics;

internal sealed record PerformanceEvent(long Sequence, long Timestamp, int Thread, string Phase, string Outcome,
    long Request, string? Watch, string? Asset, long Epoch, long Revision, long Value);
internal sealed record PerformanceSnapshot(string RunId, string SourceHash, string FixtureHash, long Frequency,
    long StartedAt, long Dropped, PerformanceEvent[] Events);

/// <summary>Opt-in bounded internal PF diagnostic sink. Compile ONCE in libmeowchan; all friends share that sink.
/// No file IO, callbacks, task scheduling or product locks. A contended write is dropped, never awaited.
/// IDs are process-monotonic; combine with RunId across processes. Value units are defined by each phase.
/// Measure emits begin/end and its end Value is elapsed Stopwatch ticks (Frequency is in the snapshot).</summary>
internal static class PerformanceDiagnostics
{
    private static DiagnosticRun? _current;
    private static long _id;
    internal static long NextId() => Interlocked.Increment(ref _id);
    internal static bool Enabled => Volatile.Read(ref _current) != null;

    internal static DiagnosticRun BeginRun(string runId, string sourceHash, string fixtureHash, int capacity = 16384)
    {
        if (capacity < 16 || capacity > 262144) throw new ArgumentOutOfRangeException(nameof(capacity));
        DiagnosticRun run = new(runId, sourceHash, fixtureHash, capacity);
        if (Interlocked.CompareExchange(ref _current, run, null) != null)
            throw new InvalidOperationException("A performance diagnostic run is already active.");
        return run;
    }

    internal static void Record(string phase, string outcome = "", long request = 0, string? watch = null,
        string? asset = null, long epoch = 0, long revision = 0, long value = 0)
        => Volatile.Read(ref _current)?.Record(phase, outcome, request, watch, asset, epoch, revision, value);

    internal static Measurement Measure(string phase, long request = 0, string? watch = null,
        string? asset = null, long epoch = 0, long revision = 0)
        => new(Volatile.Read(ref _current), phase, request, watch, asset, epoch, revision);

    internal struct Measurement : IDisposable
    {
        private DiagnosticRun? _run;
        private readonly string _phase;
        private readonly long _request, _epoch, _revision, _start;
        private readonly string? _watch, _asset;
        internal Measurement(DiagnosticRun? run, string phase, long request, string? watch, string? asset, long epoch, long revision)
        {
            _run = run; _phase = phase; _request = request; _watch = watch; _asset = asset; _epoch = epoch; _revision = revision;
            _start = run == null ? 0 : Stopwatch.GetTimestamp();
            run?.Record(phase, "begin", request, watch, asset, epoch, revision, 0);
        }
        public void Dispose() => Interlocked.Exchange(ref _run, null)?.Record(_phase, "end", _request, _watch, _asset,
            _epoch, _revision, Stopwatch.GetTimestamp() - _start);
    }

    internal sealed class DiagnosticRun : IDisposable
    {
        private readonly object _sync = new();
        private readonly PerformanceEvent?[] _events;
        private readonly string _run, _source, _fixture;
        private readonly long _start = Stopwatch.GetTimestamp();
        private long _sequence, _dropped;
        private int _count, _next, _disposed;
        internal DiagnosticRun(string run, string source, string fixture, int capacity)
        {
            _run = Limit(run) ?? ""; _source = Limit(source) ?? ""; _fixture = Limit(fixture) ?? "";
            _events = new PerformanceEvent[capacity];
        }
        private static string? Limit(string? text) => text?.Length > 256 ? text[..256] : text;
        internal void Record(string phase, string outcome, long request, string? watch, string? asset, long epoch, long revision, long value)
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            if (!Monitor.TryEnter(_sync)) { Interlocked.Increment(ref _dropped); return; }
            try
            {
                if (_disposed != 0) return;
                _events[_next] = new(++_sequence, Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId,
                    Limit(phase) ?? "", Limit(outcome) ?? "", request, Limit(watch), Limit(asset), epoch, revision, value);
                _next = (_next + 1) % _events.Length;
                if (_count == _events.Length) Interlocked.Increment(ref _dropped); else _count++;
            }
            finally { Monitor.Exit(_sync); }
        }
        internal PerformanceSnapshot Snapshot()
        {
            lock (_sync)
            {
                PerformanceEvent[] result = new PerformanceEvent[_count];
                int first = (_next - _count + _events.Length) % _events.Length;
                for (int i = 0; i < result.Length; i++) result[i] = _events[(first + i) % _events.Length]!;
                return new(_run, _source, _fixture, Stopwatch.Frequency, _start, Interlocked.Read(ref _dropped), result);
            }
        }
        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            Interlocked.CompareExchange(ref _current, null, this);
        }
    }
}
