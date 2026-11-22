using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;
using MeowChan.DesktopSmoke;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

[Collection("Avalonia global state")]
public sealed class StatisticsResetContractTests
{
    [Fact]
    public void FailedBackupDeleteReportsFailureAndResetCanBeRetriedAfterRestart() => BooruComponentDispatcher.Run(() =>
    {
        using IDisposable scope = AvaloniaLocator.EnterScope();
        AvaloniaLocator.CurrentMutable.Bind<ICursorFactory>().ToConstant(new DeadAffordanceGuard.RecordingCursorFactory());
        TokenApplication.Bind();
        using TempDirectory dir = new();
        IoManualClock clock = new(new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero));
        IoStatsOptions options = new() { Persist = true };
        StatisticsPage page = new();
        string path = Path.Combine(dir.Path, IoStatsStore.FileName);
        File.WriteAllText(path, """{"version":1,"days":[{"day":"2026-11-14","sources":{"fixture":{"down":456}}}]}""");
        File.WriteAllText(path + ".bak", """{"version":1,"days":[{"day":"2026-11-14","sources":{"fixture":{"down":123}}}]}""");
        try
        {
            using (ProfileLease lease = ProfileLease.Acquire(dir.Path))
            using (IoStatsHub hub = new(new IoFakeSource(), clock, options))
            {
                IoStatsStore store = new(dir.Path, lease, file =>
                {
                    if (file == path + ".bak") throw new IOException("Backup in use");
                    File.Delete(file);
                });
                using IDisposable attachment = hub.Attach(store, null);
                page.Attach(hub, null);
                Assert.Equal(Texts.T("stats.reset.failed"), BooruComponentDispatcher.Wait(ResetAndReadStatusAsync(page)));
                Assert.Equal(456, hub.Snapshot().Total.Retained.NetDown);
                Assert.True(File.Exists(path));
                Assert.True(File.Exists(path + ".bak"));
            }

            using ProfileLease restarted = ProfileLease.Acquire(dir.Path);
            IoStatsStore retryStore = new(dir.Path, restarted);
            using IoStatsHub retryHub = new(new IoFakeSource(), clock, options);
            using IDisposable retryAttachment = retryHub.Attach(retryStore, null);
            Assert.Equal(456, retryHub.Snapshot().Total.Retained.NetDown);
            page.Attach(retryHub, null);
            Assert.Equal(Texts.T("stats.reset.done"), BooruComponentDispatcher.Wait(ResetAndReadStatusAsync(page)));
            Assert.Equal(0, retryHub.Snapshot().Total.Retained.NetDown);
            Assert.Empty(Directory.GetFiles(dir.Path, "io-stats*"));
            IoStatsLoadResult saved = retryStore.Load(new DateOnly(2026, 11, 14), 30, 64);
            Assert.Equal(IoStatsLoadStatus.Ok, saved.Status);
            Assert.Empty(saved.Days.Days);
        }
        finally { page.EndLifetime(); }
    });

    private static async Task<string?> ResetAndReadStatusAsync(StatisticsPage page)
    {
        await page.ResetStatisticsAsync();
        return page.FindControl<TextBlock>("StatisticsStatus")!.Text;
    }

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
