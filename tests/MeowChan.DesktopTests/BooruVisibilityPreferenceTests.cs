using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// BP2 storage contract. The page and global view-menu switch are composed separately.
public sealed class BooruVisibilityPreferenceTests
{
    [Fact]
    public void MissingPreferenceKeepsPreviouslyAvailableToolsVisible()
    {
        Assert.True(new Config().ShowBooruTools);
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"RecheckTimer\":120}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.True(loaded.ShowBooruTools);
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.Null(loaded.LoadWarning);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryCopyAndPersistencePathKeepsThePreference(bool visible)
    {
        using TempDirectory directory = new();
        Config source = new() { ShowBooruTools = visible, DownloadPath = Path.Combine(directory.Path, "media") };
        Config clone = source.Clone();
        Config copy = new(); copy.CopyFrom(source);
        Config settings = new(); settings.CopySettingsFieldsFrom(source);
        Assert.Equal(visible, clone.ShowBooruTools);
        Assert.Equal(visible, copy.ShowBooruTools);
        Assert.Equal(visible, settings.ShowBooruTools);
        Assert.Equal(visible, SettingsTransfer.CreatePortableSnapshot(source).ShowBooruTools);
        Assert.Equal(visible, SettingsTransfer.Export(source)[nameof(Config.ShowBooruTools)]!.GetValue<bool>());
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Equal(visible, loaded.ShowBooruTools);
        Assert.Null(loaded.LoadWarning);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("0")]
    [InlineData("\"false\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void InvalidPresentationPreferenceDoesNotRejectUnrelatedSettings(string token)
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"),
            "{\"ShowBooruTools\":" + token + ",\"RecheckTimer\":120}");
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.True(loaded.ShowBooruTools);
        Assert.Equal(120, loaded.RecheckTimer);
        Assert.Null(loaded.LoadWarning);
    }

    [Fact]
    public void HidingToolsIsNotAPauseOrGrantReset()
    {
        Config config = new() { DownloadsPaused = false, WorkViewNoticePending = true };
        var permissions = config.SourceMediaIntegrityPermissions;
        config.ShowBooruTools = false;
        Assert.False(config.DownloadsPaused);
        Assert.True(config.WorkViewNoticePending);
        Assert.Same(permissions, config.SourceMediaIntegrityPermissions);
        Assert.Empty(permissions);
    }
}
