using MeowChan.Desktop.Stats;
using MeowChan.DesktopSmoke;

namespace MeowChan.DesktopTests;

public sealed class StatisticsResetContractTests
{
    [Fact]
    public void ResetClearsEveryMetricAndNewIdleSamplesAreNotLeftoverHistory()
    {
        IoManualClock clock = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock, new IoStatsOptions { Persist = false, SampleIntervalMs = 500 });
        using IoStatsSubscription window = hub.Subscribe(IoStatsVisibility.Window);
        source.Add("fixture", net: 100, netUp: 200, diskWritten: 300, diskRead: 400, requests: 5, errors: 6);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        Assert.False(StatisticsResetContract.IsCleared(hub.Snapshot()));
        // Also discard increments not sampled yet, without resetting the monotonic transport counters.
        source.Add("fixture", net: 10, netUp: 20, diskWritten: 30, diskRead: 40, requests: 1, errors: 1);
        hub.Reset();
        IoStatsSnapshot immediate = hub.Snapshot();
        Assert.True(StatisticsResetContract.IsCleared(immediate));
        Assert.Equal(0, immediate.Series.Count);
        Assert.Equal(1, hub.ActiveTimerCount);

        // Reproduce the smoke's race without a scheduler or sleep: the UI sees completion after three more ticks.
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        IoStatsSnapshot acknowledged = hub.Snapshot();
        Assert.Equal(3, acknowledged.Series.Count); // the old Count == 0 assertion rejects a correct reset
        Assert.True(StatisticsResetContract.IsCleared(acknowledged));

        source.Add("fixture", net: 11, netUp: 22, diskWritten: 33, diskRead: 44, requests: 2, errors: 1);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        IoStatsSnapshot fresh = hub.Snapshot();
        IoCounts expected = new(11, 22, 33, 44, 2, 1);
        Assert.Equal(expected, fresh.Total.Session);
        Assert.Equal(expected, fresh.Total.Today);
        Assert.Equal(expected, fresh.Total.Retained);
        Assert.Equal(expected, Assert.Single(fresh.Sources).Session);
        Assert.Equal(new IoRates(22, 44, 66, 88, 4), fresh.Rate);
        Assert.Equal([0f, 0f, 0f, 22f], fresh.Series.NetDown);
        Assert.Equal([0f, 0f, 0f, 44f], fresh.Series.NetUp);
        Assert.Equal([0f, 0f, 0f, 66f], fresh.Series.DiskWrite);
        Assert.Equal([0f, 0f, 0f, 88f], fresh.Series.DiskRead);
        Assert.Equal([0f, 0f, 0f, 4f], fresh.Series.Requests);
        Assert.False(StatisticsResetContract.IsCleared(fresh));
    }

    public static IEnumerable<object[]> CounterCases() =>
        from metric in Enumerable.Range(0, 6)
        from period in Enumerable.Range(0, 3)
        select new object[] { metric, period };

    [Theory]
    [MemberData(nameof(CounterCases))]
    public void AnyLeftoverCounterInAnyPeriodIsRejected(int metric, int period)
    {
        long[] values = new long[6];
        values[metric] = 1;
        IoCounts counts = new(values[0], values[1], values[2], values[3], values[4], values[5]);
        IoSourceStats total = new("", period == 0 ? counts : default, period == 1 ? counts : default,
            period == 2 ? counts : default, default);
        Assert.False(StatisticsResetContract.IsCleared(new IoStatsSnapshot { Total = total }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void NonzeroHistoryInAnyDirectionIsRejectedEvenWithNoSourcesOrTotals(int metric)
    {
        float[][] values = Enumerable.Range(0, 5).Select(_ => new float[2]).ToArray();
        values[metric][0] = 1; // an old sample, not just the last one
        IoSeries series = new(30, 2, values[0], values[1], values[2], values[3], values[4]);
        Assert.False(StatisticsResetContract.IsCleared(new IoStatsSnapshot { Series = series }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void NonzeroCurrentRatesAreRejected(int metric)
    {
        double[] values = new double[5];
        values[metric] = 1;
        IoRates rate = new(values[0], values[1], values[2], values[3], values[4]);
        Assert.False(StatisticsResetContract.IsCleared(new IoStatsSnapshot { Rate = rate }));
        Assert.False(StatisticsResetContract.IsCleared(new IoStatsSnapshot
        {
            Total = new IoSourceStats("", default, default, default, rate)
        }));
    }

    [Fact]
    public void AnUnremovedSourceIsRejectedEvenIfItsCountersAreZero() =>
        Assert.False(StatisticsResetContract.IsCleared(new IoStatsSnapshot
        {
            Sources = [new IoSourceStats("fixture", default, default, default, default)]
        }));
}
