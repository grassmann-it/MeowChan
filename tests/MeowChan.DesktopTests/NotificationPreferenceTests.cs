using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class NotificationPreferenceTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ShowInformationNotifications\":null,\"ShowWarningNotifications\":\"false\",\"ShowErrorNotifications\":0}")]
    [InlineData("{\"ShowInformationNotifications\":[],\"ShowWarningNotifications\":{},\"ShowErrorNotifications\":null}")]
    public void MissingAndMalformedSwitchesKeepCompatibleDefaults(string fields)
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), fields[..^1] + (fields.Length > 2 ? "," : "") + "\"RecheckTimer\":120}");
        Config config = Config.LoadConfig(directory.Path);
        Assert.True(config.ShowInformationNotifications);
        Assert.True(config.ShowWarningNotifications);
        Assert.True(config.ShowErrorNotifications);
        Assert.Equal(120, config.RecheckTimer);
        Assert.Null(config.LoadWarning);
    }

    [Fact]
    public void ValidFalseIsNotLostAlongsideOneMalformedSwitch()
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"ShowInformationNotifications\":false,\"ShowWarningNotifications\":null,\"ShowErrorNotifications\":false}");
        Config config = Config.LoadConfig(directory.Path);
        Assert.False(config.ShowInformationNotifications);
        Assert.True(config.ShowWarningNotifications);
        Assert.False(config.ShowErrorNotifications);
        Config copy = new(); copy.CopyFrom(config.Clone()); copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.False(loaded.ShowInformationNotifications);
        Assert.True(loaded.ShowWarningNotifications);
        Assert.False(loaded.ShowErrorNotifications);
    }

    [Fact]
    public async Task SettingsMergeKeepsCurrentViewPreferencesAndFailedWriteKeepsActiveSwitches()
    {
        using TempDirectory directory = new();
        new Config { DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await using var cleanup = host;
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Config draft = host.Config.Clone();
        draft.ShowInformationNotifications = draft.ShowWarningNotifications = draft.ShowErrorNotifications = false;
        await host.SaveViewPreferencesAsync(current => current.GalleryContextOpen = true);
        string settings = Path.Combine(directory.Path, "settings.json");
        File.Delete(settings);
        Directory.CreateDirectory(settings);
        await Assert.ThrowsAnyAsync<Exception>(() => host.ApplySettingsAsync(draft));
        Assert.True(host.Config.ShowInformationNotifications);
        Assert.True(host.Config.ShowWarningNotifications);
        Assert.True(host.Config.ShowErrorNotifications);
        Assert.True(host.Config.GalleryContextOpen);
        Directory.Delete(settings);
        await host.ApplySettingsAsync(draft);
        Assert.False(host.Config.ShowInformationNotifications);
        Assert.False(host.Config.ShowWarningNotifications);
        Assert.False(host.Config.ShowErrorNotifications);
        Assert.True(host.Config.GalleryContextOpen);
        Config saved = Config.LoadConfig(directory.Path);
        Assert.False(saved.ShowErrorNotifications);
        Assert.True(saved.GalleryContextOpen);
    }
}
