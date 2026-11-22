using MeowChan.Desktop;
using MeowChan.Desktop.Commands;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

// Persistence/runtime boundary only. Real SettingsEditor constructor/control/draft
// assertions live in DesktopSmoke/FirstRunSettingsReopenChecks.cs, where the
// native application supplies Avalonia assets and UI-thread resources.
[Collection("Avalonia global state")]
public sealed class FirstRunSettingsReopenBoundaryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData(48, 12, 12, 36)]
    [InlineData(7, 64, 7, 0)]
    public async Task SavedFirstRunReloadsDurableValuesAndRestartsWithEffectiveCap(
        int requested, int cap, int effective, int queuedByCap)
    {
        using TempDirectory directory = new();
        string chosen = Path.Combine(directory.Path, "actual media ä", "nested destination");
        Directory.CreateDirectory(chosen);
        string profile = Path.Combine(directory.Path, "profile");
        Directory.CreateDirectory(profile);
        new Config
        {
            FirstRunSetupPending = true,
            DownloadPath = Path.Combine(directory.Path, "before setup"),
            ParallelDownloadsSafetyLimit = cap,
            AutomaticUpdateChecks = false,
            SaveUrlsOnExit = false,
            MinimumFreeSpaceGiB = 0
        }.SaveConfig(profile);

        await using (DesktopSessionHost host = OwnedUiDispatcher.CreateHost(() => new(profile, Array.Empty<IImageBoard>())))
        {
            await host.StartAsync().WaitAsync(Timeout);
            Assert.Equal(DesktopSessionState.Ready, host.State);
            FirstRunSetupCommitResult commit = await host.CompleteFirstRunSetupAsync(
                new(chosen, 2_621_440, requested)).WaitAsync(Timeout);
            Assert.True(commit.Persisted);
            Assert.Null(commit.ActivationWarning);
        }

        string settingsFile = Path.Combine(profile, "settings.json");
        byte[] committedBytes = File.ReadAllBytes(settingsFile);
        Config saved = Config.LoadConfig(profile);
        Assert.Null(saved.LoadWarning);
        Assert.False(saved.FirstRunSetupPending);
        Assert.Equal(1, saved.FirstRunSetupVersion);
        Assert.Equal(chosen, saved.DownloadPath);
        Assert.Equal(requested, saved.MaxParallelDownloads);
        Assert.Equal(cap, saved.ParallelDownloadsSafetyLimit);
        Assert.Equal(2_621_440, saved.BandwidthLimiter);

        Assert.False(saved.AutomaticUpdateChecks);
        Assert.False(saved.SaveUrlsOnExit);
        Assert.Equal(0, saved.MinimumFreeSpaceGiB);
        Assert.Equal(new Config().RecheckTimer, saved.RecheckTimer);
        Assert.Equal(new Config().Theme, saved.Theme);

        // Reading a second independent snapshot must not rewrite the committed
        // settings. This makes no claim about SettingsEditor construction.
        Config reopened = Config.LoadConfig(profile);
        Assert.NotSame(saved, reopened);
        Assert.Null(reopened.LoadWarning);
        Assert.Equal(chosen, reopened.DownloadPath);
        Assert.Equal(requested, reopened.MaxParallelDownloads);
        Assert.Equal(cap, reopened.ParallelDownloadsSafetyLimit);
        Assert.Equal(2_621_440, reopened.BandwidthLimiter);
        Assert.Equal(committedBytes, File.ReadAllBytes(settingsFile));

        await using DesktopSessionHost restarted = OwnedUiDispatcher.CreateHost(() => new(profile, Array.Empty<IImageBoard>()));
        await restarted.StartAsync().WaitAsync(Timeout);
        await restarted.CaptureProjectionAsync().WaitAsync(Timeout);
        Assert.Equal(chosen, restarted.Config.DownloadPath);
        Assert.Equal(cap, restarted.Config.ParallelDownloadsSafetyLimit);
        Assert.Equal(2_621_440, restarted.Config.BandwidthLimiter);
        Assert.False(restarted.Config.AutomaticUpdateChecks);
        Assert.False(restarted.Config.SaveUrlsOnExit);
        Assert.Equal(committedBytes, File.ReadAllBytes(settingsFile));
        Assert.Equal((requested, effective, 0, queuedByCap), restarted.Projection.MediaConcurrency);
        Assert.False(FirstRunSetupCoordinator.CanOffer(restarted.Config, restarted.State));
    }

    [Fact]
    public async Task FixtureHostDoesNotRefreshOtherTestsShortcutPresentations()
    {
        using TempDirectory directory = new();
        using ShortcutTestState shortcuts = new();
        DesktopSessionHost? host = null;
        try
        {
            OwnedUiDispatcher.Run(() =>
            {
                int refreshes = 0;
                Action refresh = () => refreshes++;
                ShortcutService.Observe(refresh);
                long revision = ShortcutService.Revision;

                host = OwnedUiDispatcher.CreateHost(() => new(directory.Path, Array.Empty<IImageBoard>()));

                Assert.Equal(0, refreshes);
                Assert.Equal(revision, ShortcutService.Revision);
                Assert.Equal(1, ShortcutService.ObserverCount);
                GC.KeepAlive(refresh);
            });
        }
        finally
        {
            if (host != null) await host.DisposeAsync();
        }
    }
}
