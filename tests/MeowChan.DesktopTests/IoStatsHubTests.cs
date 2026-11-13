using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

public sealed class IoStatsHubTests
{
    private const long MiB = 1024 * 1024;
    private static readonly DateTimeOffset Noon = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);

    private sealed class Harness : IDisposable
    {
        internal readonly IoManualClock Clock;
        internal readonly IoFakeSource Source = new();
        internal readonly IoStatsHub Hub;
        internal int Active;
        internal int Raised;

        internal Harness(DateTimeOffset? start = null, IoStatsOptions? options = null)
        {
            Clock = new IoManualClock(start ?? Noon);
            Hub = new IoStatsHub(Source, Clock, options);
            Hub.Sampled += () => Raised++;
        }

        internal IoActivity Probe() => new(Active, 0, 0);
        public void Dispose() => Hub.Dispose();
    }

    private static IoStatsOptions Opts(int interval = 1000, int history = 30) =>
        new() { SampleIntervalMs = interval, HistorySamples = history, Persist = false };

    [Fact]
    public void WindowSamplingTurnsCounterDeltasIntoRatesAndRingSamples()
    {
        using Harness h = new(options: Opts());
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        Assert.Equal(1, h.Hub.ActiveTimerCount);
        for (int second = 1; second <= 3; second++)
        {
            h.Source.Add("4chan", net: 2 * MiB, diskWritten: MiB, requests: 4);
            h.Clock.Advance(TimeSpan.FromSeconds(1));
        }
        IoStatsSnapshot snapshot = h.Hub.Snapshot();
        Assert.Equal(3, snapshot.Series.Count);
        Assert.Equal([2f * MiB, 2f * MiB, 2f * MiB], snapshot.Series.NetDown);
        Assert.Equal([1f * MiB, 1f * MiB, 1f * MiB], snapshot.Series.DiskWrite);
        Assert.Equal([4f, 4f, 4f], snapshot.Series.Requests);
        Assert.Equal(2d * MiB, snapshot.Rate.NetDown);
        IoSourceStats row = Assert.Single(snapshot.Sources);
        Assert.Equal("4chan", row.Key);
        Assert.Equal(6 * MiB, row.Session.NetDown);
        Assert.Equal(12, row.Session.Requests);
        Assert.Equal(2d * MiB, row.Rate.NetDown);
        Assert.Equal(3, h.Raised);
    }

    [Fact]
    public void TheRateUsesTheElapsedTimeNotTheNominalInterval()
    {
        using Harness h = new(options: Opts(interval: 2000));
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Source.Add("a", net: 4 * MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(2d * MiB, h.Hub.Snapshot().Rate.NetDown);
    }

    [Fact]
    public void RateAndRowsPerSourceAddUpToTheTotal()
    {
        using Harness h = new(options: Opts());
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Source.Add("big", net: 3 * MiB, errors: 1);
        h.Source.Add("small", net: MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        IoStatsSnapshot s = h.Hub.Snapshot();
        Assert.Equal(["big", "small"], s.Sources.Select(row => row.Key));
        Assert.Equal(4 * MiB, s.Total.Session.NetDown);
        Assert.Equal(1, s.Total.Session.Errors);
        Assert.Equal(4d * MiB, s.Total.Rate.NetDown);
        Assert.Equal(s.Total.Rate, s.Rate);
    }

    [Fact]
    public void ChangingTheHistoryLengthAtRuntimeKeepsTheNewestSamples()
    {
        using Harness h = new(options: Opts(history: 60));
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        for (int i = 1; i <= 40; i++) { h.Source.Add("a", net: i * 1024); h.Clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(40, h.Hub.Snapshot().Series.Count);
        h.Hub.Configure(Opts(history: 30));
        IoStatsSnapshot shrunk = h.Hub.Snapshot();
        Assert.Equal(30, shrunk.Series.Count);
        Assert.Equal(30, shrunk.Series.Capacity);
        Assert.Equal(40f * 1024, shrunk.Series.NetDown[^1]);
        Assert.Equal(11f * 1024, shrunk.Series.NetDown[0]);
        h.Hub.Configure(Opts(history: 100));
        Assert.Equal(30, h.Hub.Snapshot().Series.Count);
        Assert.Equal(100, h.Hub.Snapshot().Series.Capacity);
    }

    [Fact]
    public void ChangingTheIntervalAtRuntimeRetimesTheTimerAndStartsAFreshGraph()
    {
        using Harness h = new(options: Opts(interval: 1000));
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Source.Add("a", net: MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(3, h.Hub.Snapshot().Series.Count);
        h.Hub.Configure(Opts(interval: 500));
        Assert.Equal(500, h.Hub.SamplingIntervalMs);
        Assert.Equal(0, h.Hub.Snapshot().Series.Count);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(2, h.Hub.Snapshot().Series.Count);
        Assert.Equal(1, h.Hub.ActiveTimerCount);
    }

    [Fact]
    public void ZeroSamplesStopRaisingOnceTheWholeBufferIsZero()
    {
        using Harness h = new(options: Opts(history: 30));
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Source.Add("a", net: MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, h.Raised);
        // The nonzero sample leaves the buffer after 30 more samples; until then every draw still changes something.
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(31, h.Raised);
        h.Clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(31, h.Raised);
        // Activity wakes the graph again.
        h.Source.Add("a", net: MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(32, h.Raised);
    }

    [Fact]
    public void WindowTimersFollowVisibilityOnly()
    {
        using Harness h = new(options: Opts());
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        IoStatsSubscription a = h.Hub.Subscribe(IoStatsVisibility.None);
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        a.Visibility = IoStatsVisibility.Window;
        Assert.Equal(1, h.Hub.ActiveTimerCount);
        Assert.Equal(IoStatsVisibility.Window, h.Hub.SamplingMode);
        Assert.Equal(1000, h.Hub.SamplingIntervalMs);
        a.Visibility = IoStatsVisibility.None;
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        Assert.Equal(0, h.Clock.ActiveTimers);
        a.Visibility = IoStatsVisibility.Window;
        a.Dispose();
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        Assert.Equal(0, h.Clock.ActiveTimers);
        a.Visibility = IoStatsVisibility.Window; // after dispose: ignored
        Assert.Equal(0, h.Hub.ActiveTimerCount);
    }

    [Fact]
    public void HiddenAndIdleMeansNoTimerEvenWithTheTraySubscribed()
    {
        using Harness h = new(options: new IoStatsOptions { Persist = false });
        using IDisposable attachment = h.Hub.Attach(null, h.Probe);
        using IoStatsSubscription tray = h.Hub.Subscribe(IoStatsVisibility.Tray);
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        Assert.Equal(0, h.Clock.ActiveTimers);
        h.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, h.Raised);
        // Reading on demand does not start anything either.
        h.Source.Add("a", net: MiB);
        IoStatsPeek peek = h.Hub.Peek();
        Assert.Equal(MiB, peek.Today.NetDown);
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        Assert.Equal(0, h.Clock.ActiveTimers);
    }

    [Fact]
    public void HiddenWithActiveDownloadsRunsOneSlowTimerOnlyForTheTooltip()
    {
        using Harness h = new(options: new IoStatsOptions { Persist = false, BackgroundIntervalMs = 5000 });
        using IDisposable attachment = h.Hub.Attach(null, h.Probe);
        using IoStatsSubscription tray = h.Hub.Subscribe(IoStatsVisibility.Tray);
        h.Active = 3;
        h.Hub.NotifyActivityChanged();
        Assert.Equal(1, h.Hub.ActiveTimerCount);
        Assert.Equal(IoStatsVisibility.Tray, h.Hub.SamplingMode);
        Assert.Equal(5000, h.Hub.SamplingIntervalMs);

        h.Source.Add("a", net: 10 * MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(1, h.Raised);
        IoStatsPeek peek = h.Hub.Peek();
        Assert.Equal(2d * MiB, peek.Rate.NetDown);
        Assert.Equal(3, peek.Activity.Active);
        // The slow cadence never feeds the window graph.
        Assert.Equal(0, h.Hub.Snapshot().Series.Count);

        // Downloads end: the timer goes away at the next look at the queue, the tail is folded in.
        h.Source.Add("a", net: MiB);
        h.Active = 0;
        h.Hub.NotifyActivityChanged();
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        Assert.Equal(11 * MiB, h.Hub.Peek().Today.NetDown);
        h.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, h.Raised);
    }

    [Fact]
    public void TheTooltipSettingSwitchesTheBackgroundTimerOffAndTheTrayClaimAloneNeverRunsAWindowTimer()
    {
        using Harness h = new(options: new IoStatsOptions { Persist = false, TrayTooltip = false });
        using IDisposable attachment = h.Hub.Attach(null, h.Probe);
        using IoStatsSubscription tray = h.Hub.Subscribe(IoStatsVisibility.Tray);
        h.Active = 2;
        h.Hub.NotifyActivityChanged();
        Assert.Equal(0, h.Hub.ActiveTimerCount);
        h.Hub.Configure(new IoStatsOptions { Persist = false, TrayTooltip = true });
        Assert.Equal(1, h.Hub.ActiveTimerCount);
        h.Hub.Configure(new IoStatsOptions { Persist = false, TrayTooltip = false });
        Assert.Equal(0, h.Hub.ActiveTimerCount);
    }

    [Fact]
    public void ShowingTheWindowAfterAHiddenSpanDoesNotMakeOneHugeSpike()
    {
        using Harness h = new(options: Opts());
        h.Source.Add("a", net: 500 * MiB); // arrived while hidden, no timer running
        h.Clock.Advance(TimeSpan.FromMinutes(5));
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        IoStatsSnapshot s = h.Hub.Snapshot();
        Assert.Equal(0d, s.Rate.NetDown);
        Assert.Equal(500 * MiB, s.Total.Session.NetDown); // still counted in the totals
    }

    [Fact]
    public void ADayBoundaryMovesNewBytesToTheNewDayAndKeepsTheRetentionTotal()
    {
        using Harness h = new(start: new DateTimeOffset(2026, 11, 13, 23, 59, 58, TimeSpan.Zero), options: Opts());
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Source.Add("a", net: 100);
        h.Clock.Advance(TimeSpan.FromSeconds(1)); // 23:59:59
        IoStatsSnapshot before = h.Hub.Snapshot();
        Assert.Equal(new DateOnly(2026, 11, 13), before.Today);
        Assert.Equal(100, before.Sources[0].Today.NetDown);

        h.Source.Add("a", net: 40);
        h.Clock.Advance(TimeSpan.FromSeconds(2)); // 00:00:01 on the 14th
        IoStatsSnapshot after = h.Hub.Snapshot();
        Assert.Equal(new DateOnly(2026, 11, 14), after.Today);
        IoSourceStats row = after.Sources[0];
        Assert.Equal(40, row.Today.NetDown);
        Assert.Equal(140, row.Retained.NetDown);
        Assert.Equal(140, row.Session.NetDown);
    }

    [Fact]
    public void RetentionDropsOldDaysAtTheDayChange()
    {
        using Harness h = new(start: new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero), options: Opts() with { RetentionDays = 2 });
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Source.Add("a", net: 1000);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Clock.Advance(TimeSpan.FromDays(1)); // the 14th: the 13th is still inside two days
        Assert.Equal(1000, h.Hub.Snapshot().Total.Retained.NetDown);
        h.Clock.Advance(TimeSpan.FromDays(1)); // the 15th: 13th is gone, session stays
        IoStatsSnapshot s = h.Hub.Snapshot();
        Assert.Equal(0, s.Total.Retained.NetDown);
        Assert.Equal(1000, s.Total.Session.NetDown);
    }

    [Fact]
    public void SourcesBeyondTheLimitAreSummedIntoOther()
    {
        using Harness h = new(options: Opts() with { MaxSources = 8 });
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        for (int i = 0; i < 20; i++) h.Source.Add("s" + i.ToString("00"), net: 1000 + i);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        IoStatsSnapshot s = h.Hub.Snapshot();
        Assert.Equal(8, s.Sources.Count);
        Assert.Equal(IoStatsKeys.Other, s.Sources[^1].Key);
        Assert.Equal(20 * 1000 + 190, s.Total.Session.NetDown);
        Assert.Equal(s.Total.Session.NetDown, s.Total.Today.NetDown);
        Assert.Equal(s.Total.Session.NetDown, s.Total.Retained.NetDown);
        Assert.Equal(20 * 1000 + 190, s.Sources.Sum(row => row.Session.NetDown));
    }

    [Fact]
    public void ACounterThatGoesBackwardsNeverSubtracts()
    {
        using Harness h = new(options: Opts());
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Source.Set("a", 1000);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Source.Set("a", 100);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Source.Set("a", 150);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1050, h.Hub.Snapshot().Total.Session.NetDown);
    }

    [Fact]
    public void ResetClearsCountsRingAndRatesButNotWhatAlreadyWasCounted()
    {
        using Harness h = new(options: Opts());
        using IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Source.Add("a", net: 5 * MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(2));
        int raisedBefore = h.Raised;
        h.Hub.Reset();
        Assert.Equal(raisedBefore + 1, h.Raised);
        IoStatsSnapshot s = h.Hub.Snapshot();
        Assert.Empty(s.Sources);
        Assert.Equal(0, s.Series.Count);
        Assert.Equal(0, s.Total.Today.NetDown);
        h.Source.Add("a", net: MiB);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(MiB, h.Hub.Snapshot().Total.Session.NetDown); // only the new megabyte
        Assert.Equal(1, h.Hub.ActiveTimerCount);
    }

    [Fact]
    public void ADisposedClaimStopsItsTimerForGood()
    {
        using Harness h = new(options: Opts());
        IoStatsSubscription window = h.Hub.Subscribe(IoStatsVisibility.Window);
        h.Source.Add("a", net: MiB);
        window.Dispose();
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(0, h.Raised);
        Assert.Equal(0, h.Hub.Snapshot().Series.Count);
    }

    [Fact]
    public void SettingsAreClampedIntoTheirRanges()
    {
        IoStatsOptions options = new IoStatsOptions
        {
            SampleIntervalMs = 1, HistorySamples = 1_000_000, BackgroundIntervalMs = 5, RetentionDays = 9999,
            PersistIntervalMinutes = 0, MaxSources = -3
        }.Normalized();
        Assert.Equal((500, 3600, 2000, 365, 1, 8), (options.SampleIntervalMs, options.HistorySamples, options.BackgroundIntervalMs,
            options.RetentionDays, options.PersistIntervalMinutes, options.MaxSources));
        Config config = new() { StatsSampleIntervalMs = 2500, StatsHistorySamples = 600, StatsTrayMenu = false, StatsMaxSources = 100 };
        IoStatsOptions fromConfig = IoStatsOptions.FromConfig(config);
        Assert.Equal((2500, 600, false, 100, true), (fromConfig.SampleIntervalMs, fromConfig.HistorySamples, fromConfig.TrayMenu,
            fromConfig.MaxSources, fromConfig.Persist));
    }
}
