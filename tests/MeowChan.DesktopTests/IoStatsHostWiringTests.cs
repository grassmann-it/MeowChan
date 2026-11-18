using MeowChan.Desktop;
using MeowChan.Desktop.Stats;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>The session host connects the hub to the profile, the queue and the settings, with a hub of the test's own.</summary>
public sealed class IoStatsHostWiringTests
{
    private const long MiB = 1024 * 1024;

    [Fact]
    public async Task TheHostConfiguresTheHubFollowsSettingsChangesAndWritesTheTotalsAtShutdown()
    {
        using TempDirectory directory = new();
        new Config
        {
            AutomaticUpdateChecks = false, SaveUrlsOnExit = false, DownloadPath = Path.Combine(directory.Path, "media"),
            StatsHistorySamples = 77, StatsRetentionDays = 7
        }.SaveConfig(directory.Path);
        IoManualClock clock = new(new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero));
        IoFakeSource source = new();
        using IoStatsHub hub = new(source, clock);
        string file = Path.Combine(directory.Path, IoStatsStore.FileName);
        {
            await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), ioStats: hub);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Same(hub, host.IoStats);
            Assert.Equal((77, 7, true), (hub.Options.HistorySamples, hub.Options.RetentionDays, hub.Options.Persist));
            // Hidden and idle: the host never starts a timer on its own.
            Assert.Equal(0, hub.ActiveTimerCount);

            Config draft = host.Config.Clone();
            draft.StatsSampleIntervalMs = 2500;
            draft.StatsPersist = true;
            draft.StatsRetentionDays = 14;
            await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal((2500, 14), (hub.Options.SampleIntervalMs, hub.Options.RetentionDays));

            source.Add("4chan", net: 5 * MiB, requests: 3);
            Assert.False(File.Exists(file));
        }
        // Stopping the host folds in the last counts and writes them once.
        Assert.True(File.Exists(file));
        Assert.Contains("\"down\":" + 5 * MiB, File.ReadAllText(file));
        Assert.Equal(0, hub.ActiveTimerCount);

        {
            await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>(), ioStats: hub);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(5 * MiB, hub.Snapshot().Total.Today.NetDown);
        }
    }
}
