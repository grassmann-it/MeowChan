using MeowChan.Admission;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Interfaces;
using MeowChan.Persistence;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BoardAdmissionStartupTests
{
    [Theory]
    [InlineData("{}", "boardAdmission.startupDamaged")]
    [InlineData("""{"SchemaVersion":2,"Boards":[]}""", "boardAdmission.startupNewer")]
    public async Task DesktopStartupReportsProtectedBoardRulesAndReleasesTheProfile(string primary, string messageKey)
    {
        using TempDirectory directory = new();
        Config config = SaveSettings(directory.Path);
        string path = Path.Combine(directory.Path, BoardAdmissionStore.FileName);
        const string backup = """{"SchemaVersion":1,"Boards":{}}""";
        File.WriteAllText(path, primary);
        File.WriteAllText(path + ".bak", backup);
        string settings = File.ReadAllText(Path.Combine(directory.Path, "settings.json"));

        BoardAdmissionStartupException failure = await Assert.ThrowsAsync<BoardAdmissionStartupException>(async () =>
        {
            await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        });
        Assert.Equal(config.Language, failure.SavedLanguage);
        Assert.False(ProfileLease.IsInUse(directory.Path));
        Assert.Equal(primary, File.ReadAllText(path));
        Assert.Equal(backup, File.ReadAllText(path + ".bak"));
        Assert.Equal(settings, File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(Texts.T(messageKey, BoardAdmissionStore.FileName, BoardAdmissionStore.FileName + ".bak"), failure.LocalizedMessage());
    }

    [Fact]
    public async Task LoweringBoardRuleLimitCannotMakeTheNextStartupFail()
    {
        using TempDirectory directory = new();
        SaveSettings(directory.Path);
        string path = Path.Combine(directory.Path, BoardAdmissionStore.FileName);
        // A valid legacy document with padding slightly larger than one MiB.
        string boardRules = """{"SchemaVersion":1,"Boards":{}}""" + new string(' ', 1024 * 1024);
        File.WriteAllText(path, boardRules);
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync();
        Assert.Equal(DesktopSessionState.Ready, host.State);

        Config candidate = host.Config.Clone();
        candidate.BoardAdmissionSizeMiB = Config.BoardAdmissionSizeMiBMinimum;
        await Assert.ThrowsAsync<InvalidDataException>(() => host.ApplySettingsAsync(candidate));
        Assert.Equal(Config.BoardAdmissionSizeMiBDefault, host.Config.BoardAdmissionSizeMiB);
        Assert.Equal(boardRules, File.ReadAllText(path));
    }

    private static Config SaveSettings(string profile)
    {
        Config config = new()
        {
            DownloadPath = Path.Combine(profile, "media"),
            Language = "de",
            DownloadsPaused = true,
            AutomaticUpdateChecks = false
        };
        config.SaveConfig(profile);
        return config;
    }
}
