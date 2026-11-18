using System.Globalization;
using System.Runtime.CompilerServices;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

/// <summary>
/// Desktop-facing safety-limit interactions which are intentionally kept separate from
/// the queue's dispatch stress suite. These use only synthetic profiles and the
/// Config/internal-host/localization test seams; no personal profile or media fixture is involved.
/// </summary>
[Collection("Localization")]
public sealed class DownloadSafetyLimitInteractionRegressionTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public DownloadSafetyLimitInteractionRegressionTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(256)]
    public async Task FirstRunSaveKeepsSafetyLimitConsistentWithSettingsAndEffectiveConcurrency(int cap)
    {
        using TempDirectory directory = new();
        string chosen = Path.Combine(directory.Path, "chosen");
        new Config
        {
            FirstRunSetupPending = true,
            SaveUrlsOnExit = false,
            AutomaticUpdateChecks = false,
            MinimumFreeSpaceGiB = 0,
            Language = "fr"
        }.SaveConfig(directory.Path);
        Directory.CreateDirectory(chosen);

        await using (DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>()))
        {
            await host.StartAsync().WaitAsync(Timeout);
            FirstRunSetupCommitResult result = await host.CompleteFirstRunSetupAsync(
                new(chosen, 1_048_576, 48)).WaitAsync(Timeout);

            Assert.True(result.Persisted);
            Assert.Null(result.ActivationWarning);
            Assert.False(host.Config.FirstRunSetupPending);
            Assert.Equal(1, host.Config.FirstRunSetupVersion);
            Assert.Equal(chosen, host.Config.DownloadPath);
            Assert.Equal(1_048_576L, host.Config.BandwidthLimiter);
            Assert.Equal("fr", host.Config.Language);
            await host.CaptureProjectionAsync().WaitAsync(Timeout);

            // The first-run wizard owns only its three fields. A settings draft must read
            // the confirmed Config, retaining the explicit default safety fence.
            // This is a host/config seam test, not a materialized SettingsEditor test.
            Assert.Equal(48, host.Config.MaxParallelDownloads);
            Assert.Equal(DownloadQueue.ProtectiveParallelDownloadsCeiling,
                host.Config.ParallelDownloadsSafetyLimit);
            Assert.Equal((48, 32, 0, 16), host.Projection.MediaConcurrency);

            Config settingsSnapshot = host.Config.Clone();
            Config reloaded = Config.LoadConfig(directory.Path);
            Assert.Null(reloaded.LoadWarning);
            Assert.False(reloaded.FirstRunSetupPending);
            Assert.Equal(1, reloaded.FirstRunSetupVersion);
            Assert.Equal(chosen, reloaded.DownloadPath);
            Assert.Equal(1_048_576L, reloaded.BandwidthLimiter);
            Assert.Equal("fr", reloaded.Language);
            Assert.Equal(settingsSnapshot.MaxParallelDownloads, reloaded.MaxParallelDownloads);
            Assert.Equal(settingsSnapshot.ParallelDownloadsSafetyLimit, reloaded.ParallelDownloadsSafetyLimit);
            Assert.Equal((48, 32, 0, 16),
                (reloaded.MaxParallelDownloads,
                    Math.Min(reloaded.MaxParallelDownloads, reloaded.ParallelDownloadsSafetyLimit),
                    0,
                    reloaded.MaxParallelDownloads - Math.Min(reloaded.MaxParallelDownloads, reloaded.ParallelDownloadsSafetyLimit)));

            // Exercise BOTH inclusive endpoints through the real settings commit. A
            // request of 256 (the largest allowed) detects an upper cap accidentally left at default 32.
            settingsSnapshot.MaxParallelDownloads = 256;
            settingsSnapshot.ParallelDownloadsSafetyLimit = cap;
            Assert.Equal(48, host.Config.MaxParallelDownloads);
            Assert.Equal(32, host.Config.ParallelDownloadsSafetyLimit);
            await host.ApplySettingsAsync(settingsSnapshot).WaitAsync(Timeout);
            DesktopSessionProjection applied = await host.CaptureProjectionAsync().WaitAsync(Timeout);
            Assert.False(applied.IsStale);
            Assert.Equal((256, cap, 0, 256 - cap), applied.MediaConcurrency);
            Assert.Equal(256, host.Config.MaxParallelDownloads);
            Assert.Equal(cap, host.Config.ParallelDownloadsSafetyLimit);

            Config persistedBoundary = Config.LoadConfig(directory.Path);
            Assert.Null(persistedBoundary.LoadWarning);
            Assert.Equal(256, persistedBoundary.MaxParallelDownloads);
            Assert.Equal(cap, persistedBoundary.ParallelDownloadsSafetyLimit);

            string settingsPath = Path.Combine(directory.Path, "settings.json");
            byte[] confirmedBytes = File.ReadAllBytes(settingsPath);
            foreach (int invalidCap in new[] { int.MinValue, -1, 0, 257, int.MaxValue })
            {
                Config rejected = host.Config.Clone();
                rejected.ParallelDownloadsSafetyLimit = invalidCap;
                rejected.MaxParallelDownloads = 200;
                rejected.DownloadPath = Path.Combine(directory.Path, "rejected");
                rejected.BandwidthLimiter = 2_097_152;
                ArgumentOutOfRangeException error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                    () => host.ApplySettingsAsync(rejected).WaitAsync(Timeout));

                Assert.Equal(nameof(Config.ParallelDownloadsSafetyLimit), error.ParamName);
                Assert.Equal(invalidCap, rejected.ParallelDownloadsSafetyLimit);
                DesktopSessionProjection afterRejection = await host.CaptureProjectionAsync().WaitAsync(Timeout);
                Assert.False(afterRejection.IsStale);
                Assert.Equal((256, cap, 0, 256 - cap), afterRejection.MediaConcurrency);
                Assert.Equal(256, host.Config.MaxParallelDownloads);
                Assert.Equal(cap, host.Config.ParallelDownloadsSafetyLimit);
                Assert.Equal(chosen, host.Config.DownloadPath);
                Assert.Equal(1_048_576L, host.Config.BandwidthLimiter);
                Assert.Equal("fr", host.Config.Language);
                Assert.False(host.Config.FirstRunSetupPending);
                Assert.Equal(1, host.Config.FirstRunSetupVersion);
                Assert.Equal(confirmedBytes, File.ReadAllBytes(settingsPath));
            }

            // A later ordinary settings save must still work after rejected drafts,
            // without reintroducing a stale first-run value or the default cap.
            Config followUp = host.Config.Clone();
            followUp.ShowBoardAdmissionWizard = !host.Config.ShowBoardAdmissionWizard;
            await host.ApplySettingsAsync(followUp).WaitAsync(Timeout);
            Config saved = Config.LoadConfig(directory.Path);
            Assert.Null(saved.LoadWarning);
            Assert.Equal(followUp.ShowBoardAdmissionWizard, saved.ShowBoardAdmissionWizard);
            Assert.Equal(256, saved.MaxParallelDownloads);
            Assert.Equal(cap, saved.ParallelDownloadsSafetyLimit);
            Assert.Equal(chosen, saved.DownloadPath);
            Assert.Equal(1_048_576L, saved.BandwidthLimiter);
            Assert.Equal("fr", saved.Language);
            Assert.False(saved.FirstRunSetupPending);
            Assert.Equal(1, saved.FirstRunSetupVersion);
        }

        // The previous host is disposed before opening the same synthetic profile.
        // A load-only min() calculation cannot prove startup restored the live queue.
        await using DesktopSessionHost restarted = new(directory.Path, Array.Empty<IImageBoard>());
        await restarted.StartAsync().WaitAsync(Timeout);
        DesktopSessionProjection restored = await restarted.CaptureProjectionAsync().WaitAsync(Timeout);
        Assert.Equal(DesktopSessionState.Ready, restarted.State);
        Assert.False(restored.IsStale);
        Assert.Equal((256, cap, 0, 256 - cap), restored.MediaConcurrency);
        Assert.Equal(256, restarted.Config.MaxParallelDownloads);
        Assert.Equal(cap, restarted.Config.ParallelDownloadsSafetyLimit);
        Assert.Equal(chosen, restarted.Config.DownloadPath);
        Assert.Equal(1_048_576L, restarted.Config.BandwidthLimiter);
        Assert.Equal("fr", restarted.Config.Language);
        // MainWindow owns the process-wide catalog switch. Replaying that exact
        // startup seam verifies persisted culture selection without pretending that
        // a host-only test constructs the shell or changes .NET ambient cultures.
        Texts.SetLanguage(restarted.Config.Language);
        Assert.Equal("fr", Texts.Language);
        Assert.Equal("fr", Texts.Culture.Name);
        Assert.False(restarted.Config.FirstRunSetupPending);
        Assert.Equal(1, restarted.Config.FirstRunSetupVersion);
        Assert.False(FirstRunSetupCoordinator.CanOffer(restarted.Config, restarted.State));
    }

    [Fact]
    public async Task SettingsApplyHandlesCapBothBelowAndAboveRequestedConcurrency()
    {
        using TempDirectory directory = new();
        new Config
        {
            AutomaticUpdateChecks = false,
            SaveUrlsOnExit = false,
            MaxParallelDownloads = 7,
            ParallelDownloadsSafetyLimit = 12,
            DownloadPath = Path.Combine(directory.Path, "media")
        }.SaveConfig(directory.Path);

        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(Timeout);
        await host.CaptureProjectionAsync().WaitAsync(Timeout);

        // Cap above the requested value must not inflate effective concurrency.
        Assert.Equal((7, 7, 0, 0), host.Projection.MediaConcurrency);

        Config draft = host.Config.Clone();
        draft.MaxParallelDownloads = 40;
        draft.ParallelDownloadsSafetyLimit = 12;
        await host.ApplySettingsAsync(draft).WaitAsync(Timeout);
        await host.CaptureProjectionAsync().WaitAsync(Timeout);

        // The same settings path must cap a larger request without changing the
        // configured/requested value shown to the user.
        Assert.Equal((40, 12, 0, 28), host.Projection.MediaConcurrency);
        Assert.Equal(40, host.Config.MaxParallelDownloads);
        Assert.Equal(12, host.Config.ParallelDownloadsSafetyLimit);
    }

    [Fact]
    public void ChangedCapSurvivesSequentialConfigReloadAndChangesEffectiveValue()
    {
        using TempDirectory directory = new();
        Config first = new()
        {
            MaxParallelDownloads = 40,
            ParallelDownloadsSafetyLimit = 64,
            DownloadPath = Path.Combine(directory.Path, "media")
        };
        first.SaveConfig(directory.Path);

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(64, loaded.ParallelDownloadsSafetyLimit);
        Assert.Equal(40, Effective(loaded));

        loaded.ParallelDownloadsSafetyLimit = 9;
        loaded.SaveConfig(directory.Path);

        Config changed = Config.LoadConfig(directory.Path);
        Assert.Equal(9, changed.ParallelDownloadsSafetyLimit);
        Assert.Equal(9, Effective(changed));
        Assert.Equal(40, changed.MaxParallelDownloads);
    }

    [Theory]
    [InlineData("es", "fr-FR", 1)]
    [InlineData("fr", "ru-RU", 256)]
    [InlineData("ru", "es-ES", 32)]
    public void BundledLanguagesExposeExplanatorySafetyLabelAndHint(string language, string ambientCulture, int cap)
    {
        CultureInfo previousCulture = CultureInfo.CurrentCulture;
        CultureInfo previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(ambientCulture);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            // Only data-only getters/setters are used: an uninitialized editor
            // does not prove binding notifications, raw-text parsing or UI hydration.
            var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
            editor.ParallelDownloadsSafetyLimit = cap;
            string englishLabel = editor.ParallelSafetyLabel;
            string englishHint = editor.ParallelSafetyHint;
            Texts.SetLanguage(language);

            Assert.Equal(language, Texts.Language);
            Assert.Equal(language, Texts.Culture.Name);
            Assert.Equal(ambientCulture, CultureInfo.CurrentCulture.Name);
            Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
            Assert.NotEqual(englishLabel, editor.ParallelSafetyLabel);
            Assert.NotEqual(englishHint, editor.ParallelSafetyHint);
            Assert.NotEqual("settings.parallelSafetyHint", Texts.T("settings.parallelSafetyHint"));
            Assert.Equal((decimal?)cap, editor.ParallelDownloadsSafetyLimit);
            Assert.False(string.IsNullOrWhiteSpace(editor.ParallelSafetyLabel));
            Assert.False(string.IsNullOrWhiteSpace(editor.ParallelSafetyHint));
            Assert.NotEqual("settings.parallelSafetyLimit", Texts.T("settings.parallelSafetyLimit"));
            Assert.Equal(Texts.T("settings.parallelSafetyLimit"), editor.ParallelSafetyLabel);
            Assert.Equal(Texts.T("settings.parallelSafetyHint"), editor.ParallelSafetyHint);
            Assert.Contains("1–256", editor.ParallelSafetyHint);
            Assert.Contains("32", editor.ParallelSafetyHint);
            Assert.DoesNotContain("Effective parallelism is the smaller", editor.ParallelSafetyHint);
            Assert.Contains("(Untested)", Texts.Languages.Single(info => info.Code == language).NativeName);

            // Reuse the same getter target: neither language direction may cache
            // a former label/hint or mutate the numeric draft at a range endpoint.
            Texts.SetLanguage("en");
            Assert.Equal(englishLabel, editor.ParallelSafetyLabel);
            Assert.Equal(englishHint, editor.ParallelSafetyHint);
            Assert.Equal((decimal?)cap, editor.ParallelDownloadsSafetyLimit);
            Assert.Equal(ambientCulture, CultureInfo.CurrentCulture.Name);
            Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
            Texts.SetLanguage("en");
        }
    }

    private static int Effective(Config config) => Math.Min(config.MaxParallelDownloads, config.ParallelDownloadsSafetyLimit);
}
