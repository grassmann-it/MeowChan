using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Downloader;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class DownloadSafetyLimitSettingsTests
{
    [Fact]
    public void LegacyProfileKeepsRequestedParallelismAndDefaultsTo32WithoutRewriting()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "settings.json");
        const string legacy = "{\"MaxParallelDownloads\":90,\"RecheckTimer\":91}";
        File.WriteAllText(path, legacy);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(90, loaded.MaxParallelDownloads);
        Assert.Equal(32, loaded.ParallelDownloadsSafetyLimit);
        Assert.Equal(91, loaded.RecheckTimer);
        Assert.Equal(legacy, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(256)]
    public void CapSurvivesCloneBothCopiesAndSaveReload(int cap)
    {
        using TempDirectory directory = new();
        Config original = new() { MaxParallelDownloads = 90, ParallelDownloadsSafetyLimit = cap };
        Config draft = original.Clone();
        Assert.Equal(cap, draft.ParallelDownloadsSafetyLimit);
        Config live = new() { DownloadsPaused = true, VideoVolume = 27 };
        live.CopySettingsFieldsFrom(draft);
        Assert.Equal(cap, live.ParallelDownloadsSafetyLimit);
        Assert.Equal(90, live.MaxParallelDownloads);
        Assert.True(live.DownloadsPaused);
        Assert.Equal(27, live.VideoVolume);
        Config copy = new();
        copy.CopyFrom(live);
        Assert.Equal(cap, copy.ParallelDownloadsSafetyLimit);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(cap, loaded.ParallelDownloadsSafetyLimit);
        Assert.Equal(90, loaded.MaxParallelDownloads);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(257)]
    [InlineData(int.MaxValue)]
    public void InvalidCapCannotValidateOrCopyIntoLiveConfig(int cap)
    {
        Config live = new() { ParallelDownloadsSafetyLimit = 64 };
        Config invalid = live.Clone();
        invalid.ParallelDownloadsSafetyLimit = cap;
        Assert.Throws<ArgumentOutOfRangeException>(() => invalid.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => live.CopySettingsFieldsFrom(invalid));
        Assert.Equal(64, live.ParallelDownloadsSafetyLimit);
    }

    [Fact]
    public async Task HostAppliesAndPersistsBothLimitsWithoutRestartAndRestoresCapAtConstruction()
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, MaxParallelDownloads = 90,
            ParallelDownloadsSafetyLimit = 64, DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        await using (DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>()))
        {
            await host.CaptureProjectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal((90, 64, 0, 26), host.Projection.MediaConcurrency);
            await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Config draft = host.Config.Clone();
            draft.MaxParallelDownloads = 48;
            draft.ParallelDownloadsSafetyLimit = 40;
            Assert.Equal(64, host.Config.ParallelDownloadsSafetyLimit);
            await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
            await host.CaptureProjectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal((48, 40, 0, 8), host.Projection.MediaConcurrency);
            Assert.Equal(40, Config.LoadConfig(directory.Path).ParallelDownloadsSafetyLimit);
            draft.ParallelDownloadsSafetyLimit = 257;
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => host.ApplySettingsAsync(draft));
            await host.CaptureProjectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal((48, 40, 0, 8), host.Projection.MediaConcurrency);
        }
        await using DesktopSessionHost restored = new(directory.Path, Array.Empty<IImageBoard>());
        await restored.CaptureProjectionAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal((48, 40, 0, 8), restored.Projection.MediaConcurrency);
    }

    [Fact]
    public void SettingsMarkupHasBoundedDraftFieldBesideParallelismAndVisibleExplanation()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement field = Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "ParallelDownloadsSafetyInput");
        Assert.Contains(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsDownloadsPage");
        Assert.Equal("{Binding ParallelDownloadsSafetyLimit, Mode=TwoWay}", (string?)field.Attribute("Value"));
        Assert.Equal("1", (string?)field.Attribute("Minimum"));
        Assert.Equal(DownloadQueue.MaximumParallelDownloadsSafetyLimit.ToString(), (string?)field.Attribute("Maximum"));
        Assert.Equal("{Binding ParallelSafetyLabel}", (string?)field.Attribute("AutomationProperties.Name"));
        Assert.Contains(field.Parent!.Parent!.Elements(), e => (string?)e.Attribute("Text") == "{Binding ParallelSafetyHint}"
            && (string?)e.Attribute("TextWrapping") == "Wrap");
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
