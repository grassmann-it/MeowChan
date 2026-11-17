using MeowChan.Desktop;
using MeowChan.Desktop.Booru;
using MeowChan.Tests.Shared;

namespace MeowChan.DesktopTests;

// PKG-C B5: existing profile preferences, not persisted remote results or download intent.
public sealed class BooruBrowsePersistenceTests
{
    [Theory]
    [InlineData(true, 8, 1)]
    [InlineData(false, 32, 24)]
    [InlineData(true, 128, 168)]
    public void BrowsePreferencesSurviveAllCopiesPortableExportAndProfileRestart(bool visible, int limit, int cooldown)
    {
        using TempDirectory profile = new();
        Config original = new() { ShowBooruTools = visible, BooruCatalogLimitMiB = limit,
            BooruRetryAfterCooldownHours = cooldown, DownloadPath = Path.Combine(profile.Path, "media") };
        Config copy = new(); copy.CopyFrom(original);
        Config settings = new(); settings.CopySettingsFieldsFrom(original);
        foreach (Config candidate in new[] { original.Clone(), copy, settings, SettingsTransfer.CreatePortableSnapshot(original) })
        {
            Assert.Equal(visible, candidate.ShowBooruTools);
            Assert.Equal(limit, candidate.BooruCatalogLimitMiB);
            Assert.Equal(cooldown, candidate.BooruRetryAfterCooldownHours);
        }
        var exported = SettingsTransfer.Export(original);
        Assert.Equal(visible, exported[nameof(Config.ShowBooruTools)]!.GetValue<bool>());
        Assert.Equal(limit, exported[nameof(Config.BooruCatalogLimitMiB)]!.GetValue<int>());
        Assert.Equal(cooldown, exported[nameof(Config.BooruRetryAfterCooldownHours)]!.GetValue<int>());
        original.SaveConfig(profile.Path);
        Config restarted = Config.LoadConfig(profile.Path);
        Assert.Null(restarted.LoadWarning);
        Assert.Equal((visible, limit, cooldown), (restarted.ShowBooruTools, restarted.BooruCatalogLimitMiB,
            restarted.BooruRetryAfterCooldownHours));
    }

    [Fact]
    public async Task RestartRestoresPreferencesButNeverReplaysSearchesOrOriginalActions()
    {
        using TempDirectory profile = new();
        Config config = new() { ShowBooruTools = false, BooruRetryAfterCooldownHours = 1 };
        config.SaveConfig(profile.Path);
        byte[] before = File.ReadAllBytes(Path.Combine(profile.Path, "settings.json"));
        BooruUiHarness harness = new();
        using (BooruWorkbenchModel first = new(harness.Services))
        {
            first.Search.SetInput("cat");
            await first.Search.SubmitAsync();
            first.Search.SetInput("unsent_edit");
        }
        int requests = harness.Search.Requests;
        Config restarted = Config.LoadConfig(profile.Path);
        using BooruWorkbenchModel second = new(harness.Services);
        await second.InitializeAsync();
        Assert.False(restarted.ShowBooruTools);
        Assert.Equal(string.Empty, second.Search.View.Input);
        Assert.Empty(second.Search.View.Rows);
        Assert.Equal(BooruSearchStatus.Idle, second.Search.View.Status);
        Assert.Equal(requests, harness.Search.Requests);
        Assert.Empty(harness.Queue.Loads);
        Assert.Empty(harness.Queue.Retries);
        Assert.Empty(harness.Queue.Cancellations);
        Assert.Equal(0, harness.Watchers.Checks);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(profile.Path, "settings.json")));
    }
}
