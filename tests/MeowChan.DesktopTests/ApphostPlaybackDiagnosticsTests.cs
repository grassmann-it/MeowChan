using MeowChan.Desktop;
using MeowChan.Diagnostics;

namespace MeowChan.DesktopTests;

[CollectionDefinition("ApphostPlaybackDiagnostics", DisableParallelization = true)]
public sealed class ApphostPlaybackDiagnosticsCollection { }

[Collection("ApphostPlaybackDiagnostics")]
public sealed class ApphostPlaybackDiagnosticsTests
{
    private static GalleryVideoPlayer.PlaybackEvidence Stalled() => new(
        "Playing", 0, 0, 0, true, true, false, 0, 0,
        SurfaceFrames: 10, SurfaceNonUniform: true, Length: 12000, Seekable: true,
        AudioTracks: 2, Volume: 70, DecodedAudio: 0, PlayedAudioBuffers: 0);

    [Fact]
    public void KeepsPublishedZeroStatsSeparateFromSurfaceEvidenceAndReadiness()
    {
        using var diagnostics = new ApphostPlaybackDiagnostics("source", "fixture");
        diagnostics.BeginReadiness();
        var playback = Stalled();
        var readiness = new { TransportEnabled = true };
        diagnostics.ObserveReadiness(playback, readiness, ready: false);
        var snapshot = diagnostics.Snapshot();
        var row = Assert.Single(snapshot.Readiness);
        Assert.Same(playback, row.Playback);
        Assert.Same(readiness, row.Readiness);
        Assert.False(row.Ready); // No surface-as-decoder substitution or inference about AAC.
        Assert.Equal(0, row.Playback.DecodedFrames);
        Assert.Equal(0, row.Playback.DecodedAudio);
        Assert.Equal(10, row.Playback.SurfaceFrames);
        Assert.Equal(1, row.Attempt);
        Assert.True(row.Timestamp >= snapshot.PlaybackTrace.StartedAt);
        Assert.True(snapshot.PlaybackTrace.Frequency > 0);
        Assert.Equal("source", snapshot.PlaybackTrace.SourceHash);
        Assert.Equal("fixture", snapshot.PlaybackTrace.FixtureHash);
    }

    [Fact]
    public void HistoryIsBoundedOrderedAndKeepsAttemptsDistinct()
    {
        using var diagnostics = new ApphostPlaybackDiagnostics("source", "fixture");
        diagnostics.BeginReadiness();
        var playback = Stalled();
        for (int i = 0; i < ApphostPlaybackDiagnostics.ReadinessCapacity + 3; i++)
            diagnostics.ObserveReadiness(playback, new { TransportEnabled = true }, ready: false);
        var before = diagnostics.Snapshot();
        diagnostics.BeginReadiness();
        diagnostics.ObserveReadiness(playback with { Time = 500, DecodedFrames = 40, DisplayedFrames = 5 }, new { }, ready: true);
        var after = diagnostics.Snapshot();
        Assert.Equal(ApphostPlaybackDiagnostics.ReadinessCapacity, after.Readiness.Length);
        Assert.Equal(4, after.DroppedReadinessObservations);
        Assert.Equal(5, after.Readiness[0].Sequence);
        Assert.Equal(2, after.ReadinessAttempts);
        Assert.Equal(2, after.Readiness[^1].Attempt);
        Assert.True(after.Readiness[^1].Ready);
        Assert.True(after.Readiness.Zip(after.Readiness.Skip(1)).All(pair => pair.First.Timestamp <= pair.Second.Timestamp));
        Assert.All(before.Readiness, row => Assert.Equal(1, row.Attempt));
    }

    [Fact]
    public void TraceContainsOnlyVideoPhasesAndAccountsForBoundedEvictions()
    {
        using var diagnostics = new ApphostPlaybackDiagnostics("source", "fixture");
        for (int i = 0; i < ApphostPlaybackDiagnostics.TraceCapacity + 7; i++)
            PerformanceDiagnostics.Record("video.observer.statistics", "end", value: i);
        PerformanceDiagnostics.Record("unrelated.phase", "not exported");
        var trace = diagnostics.Snapshot().PlaybackTrace;
        Assert.Equal(8, trace.Dropped);
        Assert.Equal(ApphostPlaybackDiagnostics.TraceCapacity - 1, trace.Events.Length);
        Assert.All(trace.Events, item => Assert.Equal("video.observer.statistics", item.Phase));
        Assert.Equal(8, trace.Events[0].Value);
    }

    [Fact]
    public void CheckpointsAreThrottledWithoutWaitingAndDisposalReleasesTheSink()
    {
        using (var diagnostics = new ApphostPlaybackDiagnostics("source", "fixture"))
        {
            Assert.True(diagnostics.CheckpointDue());
            Assert.False(diagnostics.CheckpointDue());
        }
        using var next = new ApphostPlaybackDiagnostics("next", "fixture");
        Assert.Empty(next.Snapshot().PlaybackTrace.Events);
        Assert.True(next.CheckpointDue());
    }
}
