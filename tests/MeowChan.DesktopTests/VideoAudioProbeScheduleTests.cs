using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Pure model of the player's per-tick audio probe: no VLC, no dispatcher.
public sealed class VideoAudioProbeScheduleTests
{
    private static VideoAudioProbeSchedule Started()
    {
        var schedule = new VideoAudioProbeSchedule();
        schedule.BeginMedia();
        schedule.Invalidate(); // Playing
        return schedule;
    }

    private static void Probe(VideoAudioProbeSchedule schedule, int tracks, bool available, int played)
        => schedule.Record(schedule.Observe(), tracks, available, played);

    [Fact]
    public void RenderingAudioSettlesOnlyAfterPlayedBuffersGrowWithinOneEpoch()
    {
        var schedule = Started();
        Assert.True(schedule.Tick());
        Probe(schedule, 1, true, 10);
        // One available sample may be stale statistics of a replaced input: keep probing.
        Assert.True(schedule.Tick());
        Probe(schedule, 1, true, 10);
        Assert.True(schedule.Tick());
        Probe(schedule, 1, true, 12);
        Assert.False(schedule.Tick());
        Assert.False(schedule.Tick());
    }

    [Fact]
    public void SilentTrackNeverSettlesSoTicksKeepProbing()
    {
        var schedule = Started();
        for (int i = 0; i < 20; i++)
        {
            Assert.True(schedule.Tick());
            Probe(schedule, 2, false, 0);
        }
    }

    [Fact]
    public void TracklessMediaSettlesUntilATrackEvent()
    {
        var schedule = Started();
        Probe(schedule, 0, false, 0);
        Assert.False(schedule.Tick());
        schedule.Invalidate(); // ESAdded
        Assert.True(schedule.Tick());
        Probe(schedule, 1, false, 0);
        Assert.True(schedule.Tick());
    }

    // ESAdded/ESDeleted/ESSelected and Playing (start, resume, repeat, replay) all invalidate.
    [Fact]
    public void NativeEventsInvalidateASettledEpoch()
    {
        var schedule = Started();
        Probe(schedule, 1, true, 5);
        Probe(schedule, 1, true, 6);
        Assert.False(schedule.Tick());
        schedule.Invalidate();
        Assert.True(schedule.Tick());
        // The pre-event baseline does not count: the new epoch needs its own growth.
        Probe(schedule, 1, true, 7);
        Assert.True(schedule.Tick());
        Probe(schedule, 1, true, 8);
        Assert.False(schedule.Tick());
    }

    [Fact]
    public void NewMediaIsUnsettledAndCountsRestart()
    {
        var schedule = Started();
        Probe(schedule, 0, false, 0);
        Assert.False(schedule.Tick());
        schedule.BeginMedia();
        Assert.True(schedule.Tick());
        var counts = schedule.Counts;
        Assert.Equal((1L, 0L, 0L, 0L, false), counts);
    }

    [Fact]
    public void ProbeThatReadStateBeforeAnEventCannotSettleTheNewEpoch()
    {
        var schedule = Started();
        Probe(schedule, 1, true, 5);
        long observed = schedule.Observe();
        schedule.Invalidate(); // Track change while the probe reads native state.
        schedule.Record(observed, 1, true, 9);
        Assert.True(schedule.Tick());
        schedule.Record(observed, 0, false, 0);
        Assert.True(schedule.Tick());
    }

    [Fact]
    public void RestartedStatisticsBecomeTheNewBaseline()
    {
        var schedule = Started();
        Probe(schedule, 1, true, 500); // Stale value of the previous input.
        Probe(schedule, 1, true, 3);   // Fresh input restarted its counters.
        Assert.True(schedule.Tick());
        Probe(schedule, 1, true, 4);
        Assert.False(schedule.Tick());
    }

    [Fact]
    public void LosingAvailabilityResetsTheBaseline()
    {
        var schedule = Started();
        Probe(schedule, 1, true, 5);
        Probe(schedule, 1, false, 6);
        Probe(schedule, 1, true, 7);
        Assert.True(schedule.Tick());
        Probe(schedule, 1, true, 8);
        Assert.False(schedule.Tick());
    }

    // The backend can replace its output without a track or Playing event; mute must be reapplied.
    [Fact]
    public void SettledRenderingAudioStillProbesOnTheHeartbeat()
    {
        var schedule = Started();
        Probe(schedule, 1, true, 5);
        Probe(schedule, 1, true, 6);
        for (int round = 0; round < 3; round++)
        {
            for (int i = 1; i < VideoAudioProbeSchedule.HeartbeatTicks; i++) Assert.False(schedule.Tick());
            Assert.True(schedule.Tick());
            Probe(schedule, 1, true, 7 + round);
        }
    }

    [Fact]
    public void SettledTracklessMediaHasNoHeartbeat()
    {
        var schedule = Started();
        Probe(schedule, 0, false, 0);
        for (int i = 0; i < 3 * VideoAudioProbeSchedule.HeartbeatTicks; i++) Assert.False(schedule.Tick());
    }

    // Counted work for a 10 s clip at VLC's ~4 time ticks per second, audio rendering throughout.
    // The removed behavior requested one probe per tick and refreshed the transport per probe.
    [Fact]
    public void RenderingClipProbesOnlyOnTheHeartbeatAfterSettling()
    {
        var schedule = Started();
        int played = 0, probes = 0;
        for (int tick = 0; tick < 40; tick++)
        {
            played += 12;
            if (!schedule.Tick()) continue;
            probes++;
            Probe(schedule, 1, true, played);
        }
        // Two probes settle; the remaining 38 ticks carry four heartbeats.
        Assert.Equal(6, probes);
        Assert.Equal(40, schedule.Counts.Ticks);
        Assert.True(schedule.Counts.Settled);
    }
}
