using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using MeowChan.Diagnostics;

namespace MeowChan.Desktop;

/// <summary>
/// Bounded, passive evidence for the explicitly requested apphost playback selftest.
/// Reads only already-observed values and the managed trace; never queries or changes VLC.
/// </summary>
internal sealed class ApphostPlaybackDiagnostics : IDisposable
{
    internal const int ReadinessCapacity = 128;
    internal const int TraceCapacity = 8192;
    private readonly PerformanceDiagnostics.DiagnosticRun _trace;
    private readonly Queue<ReadinessObservation> _readiness = new();
    private long _observations, _lastCheckpoint;
    private int _attempt;

    internal sealed record ReadinessObservation(long Sequence, long Timestamp, int Attempt, bool Ready,
        GalleryVideoPlayer.PlaybackEvidence Playback, object Readiness);
    internal sealed record Evidence(int ReadinessAttempts, long DroppedReadinessObservations,
        ReadinessObservation[] Readiness, PerformanceSnapshot PlaybackTrace);

    internal ApphostPlaybackDiagnostics(string source, string fixtureHash)
        => _trace = PerformanceDiagnostics.BeginRun("apphost-playback", source, fixtureHash, TraceCapacity);

    internal void BeginReadiness()
    {
        _attempt++;
        PerformanceDiagnostics.Record("video.apphost.readiness", "begin", epoch: _attempt);
        Console.WriteLine("APPHOST PLAYBACK READINESS: attempt=" + _attempt);
    }

    internal void ObserveReadiness(GalleryVideoPlayer.PlaybackEvidence playback, object readiness, bool ready)
    {
        if (_readiness.Count == ReadinessCapacity) _readiness.Dequeue();
        _readiness.Enqueue(new(++_observations, Stopwatch.GetTimestamp(), _attempt, ready, playback, readiness));
        if (ready) PerformanceDiagnostics.Record("video.apphost.readiness", "ready", epoch: _attempt);
    }

    // Preserve partial evidence if a later native getter never returns and the outer
    // runner kills the process. No extra poll, sleep or readiness deadline is introduced.
    internal bool CheckpointDue()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastCheckpoint != 0 && Stopwatch.GetElapsedTime(_lastCheckpoint, now) < TimeSpan.FromSeconds(1)) return false;
        _lastCheckpoint = now;
        return true;
    }

    internal Evidence Snapshot()
    {
        PerformanceSnapshot trace = _trace.Snapshot();
        return new(_attempt, _observations - _readiness.Count, _readiness.ToArray(),
            trace with { Events = trace.Events.Where(e => e.Phase.StartsWith("video.", StringComparison.Ordinal)).ToArray() });
    }

    public void Dispose() => _trace.Dispose();
}
