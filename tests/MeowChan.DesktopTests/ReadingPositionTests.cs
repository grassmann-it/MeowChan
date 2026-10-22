using System.Reflection;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class ReadingPositionTests
{
    private static readonly LocalReadingPosition First = new("4chan", "g", "1000", "1020", -37.5, "1022");
    private static readonly LocalReadingPosition Second = new("8kun", "tech", "2000", "2004", -19, "2005");
    private static DesktopSessionHost Host(string path) => new(path, Array.Empty<IImageBoard>());

    [Fact]
    public async Task DefaultsAndDisabledLegacyEntryNeverEnableReadingOrWrite()
    {
        using TempDirectory directory = new();
        File.WriteAllText(Path.Combine(directory.Path, "settings.json"), "{\"SaveUrlsOnExit\":false}");
        string file = Path.Combine(directory.Path, "reading-position.json");
        File.WriteAllText(file, "untrusted old entry");
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        Assert.False(host.Config.RememberReadingPosition);
        Assert.Null(await host.ReadReadingPositionAsync());
        await host.RememberReadingPositionAsync(First, host.ReadingPositionGeneration);
        Assert.Equal("untrusted old entry", File.ReadAllText(file));
    }

    [Fact]
    public async Task OneEntrySurvivesOwnerRestartAndProfilesStayIsolated()
    {
        using TempDirectory first = new();
        using TempDirectory second = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(first.Path);
        await using (DesktopSessionHost host = Host(first.Path))
        {
            await host.StartAsync();
            await host.RememberReadingPositionAsync(First, host.ReadingPositionGeneration);
            await host.RememberReadingPositionAsync(Second, host.ReadingPositionGeneration);
        }
        await using DesktopSessionHost restarted = Host(first.Path);
        await restarted.StartAsync();
        Assert.True(restarted.Config.RememberReadingPosition);
        Assert.Equal(Second, await restarted.ReadReadingPositionAsync());
        Assert.Single(Directory.GetFiles(first.Path, "reading-position*"));
        string json = File.ReadAllText(Path.Combine(first.Path, "reading-position.json"));
        Assert.DoesNotContain("1020", json);
        Assert.DoesNotContain("History", json);
        await using DesktopSessionHost other = Host(second.Path);
        await other.StartAsync();
        Assert.False(other.Config.RememberReadingPosition);
        Assert.Null(await other.ReadReadingPositionAsync());
        Assert.False(File.Exists(Path.Combine(second.Path, "reading-position.json")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForgetAndDisableRejectQueuedAndLateWritesAcrossRestart(bool disable)
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        await using (DesktopSessionHost host = Host(directory.Path))
        {
            await host.StartAsync();
            long oldGeneration = host.ReadingPositionGeneration;
            await host.RememberReadingPositionAsync(First, oldGeneration);
            SemaphoreSlim commands = (SemaphoreSlim)typeof(DesktopSessionHost).GetField("_commands", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
            await commands.WaitAsync();
            Task queued = host.RememberReadingPositionAsync(Second, oldGeneration);
            Config draft = host.Config.Clone(); draft.RememberReadingPosition = false;
            Task clear = disable ? host.ApplySettingsAsync(draft) : host.ForgetReadingPositionAsync();
            Assert.NotEqual(oldGeneration, host.ReadingPositionGeneration);
            commands.Release();
            await Task.WhenAll(queued, clear);
            await host.RememberReadingPositionAsync(Second, oldGeneration);
            Assert.Null(await host.ReadReadingPositionAsync());
            Assert.False(File.Exists(Path.Combine(directory.Path, "reading-position.json")));
        }
        await using DesktopSessionHost restarted = Host(directory.Path);
        await restarted.StartAsync();
        Assert.Equal(!disable, restarted.Config.RememberReadingPosition);
        Assert.Null(await restarted.ReadReadingPositionAsync());
        if (!disable)
        {
            await restarted.RememberReadingPositionAsync(Second, restarted.ReadingPositionGeneration);
            Assert.Equal(Second, await restarted.ReadReadingPositionAsync());
        }
    }

    [Fact]
    public async Task DisabledUnreadableEntryDoesNotBlockUnrelatedViewPreferences()
    {
        using TempDirectory directory = new();
        new Config { SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "reading-position.json");
        Directory.CreateDirectory(file);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        await host.SaveViewPreferencesAsync(config => config.Theme = "Light");
        Assert.Equal("Light", Config.LoadConfig(directory.Path).Theme);
        Assert.Null(await host.ReadReadingPositionAsync());
        Directory.Delete(file);
    }

    [Fact]
    public async Task CorruptEntryAndIoFailuresAreReportedWithoutDisablingSession()
    {
        using TempDirectory directory = new();
        new Config { RememberReadingPosition = true, SaveUrlsOnExit = false }.SaveConfig(directory.Path);
        await using DesktopSessionHost host = Host(directory.Path);
        await host.StartAsync();
        string file = Path.Combine(directory.Path, "reading-position.json");
        File.WriteAllText(file, "broken JSON");
        await Assert.ThrowsAnyAsync<Exception>(() => host.ReadReadingPositionAsync());
        Assert.Equal("broken JSON", File.ReadAllText(file));
        await host.ForgetReadingPositionAsync();
        Directory.CreateDirectory(file);
        await Assert.ThrowsAnyAsync<Exception>(() => host.RememberReadingPositionAsync(First, host.ReadingPositionGeneration));
        await Assert.ThrowsAnyAsync<Exception>(() => host.ForgetReadingPositionAsync());
        Assert.Equal(DesktopSessionState.Ready, host.State);
        Assert.True(host.Config.RememberReadingPosition);
        Directory.Delete(file);
    }
}
