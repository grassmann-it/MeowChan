using System.Text.Json;
using MeowChan.Desktop;
using MeowChan.Interfaces;

namespace MeowChan.DesktopTests;

public sealed class ReaderSettingsPersistenceTests
{
    public static IEnumerable<object[]> Numbers()
    {
        yield return new object[] { nameof(Config.ReaderThumbnailWidth), 240, 120, 480 };
        yield return new object[] { nameof(Config.ReaderFoldLines), 24, 8, 400 };
        yield return new object[] { nameof(Config.ReaderPreviewLines), 8, 3, 40 };
        yield return new object[] { nameof(Config.ReaderPreviewDelayMs), 300, 0, 2000 };
        yield return new object[] { nameof(Config.ReaderReplyStep), 5, 1, 50 };
        yield return new object[] { nameof(Config.ReaderReplyLines), 4, 1, 20 };
        yield return new object[] { nameof(Config.ReaderPageSize), 50, 20, 200 };
        yield return new object[] { nameof(Config.ReaderThreadingDepth), 6, 1, 16 };
        yield return new object[] { nameof(Config.GalleryFitMaxUpscalePercent), 200, 100, 1600 };
        yield return new object[] { nameof(Config.PostFilterRuleLimit), 200, 20, 2000 };
        yield return new object[] { nameof(Config.HiddenPostLimit), 10000, 1000, 100000 };
    }

    [Theory]
    [MemberData(nameof(Numbers))]
    public void DefaultsAndInclusiveBoundsMatchTheComponents(string name, int fallback, int min, int max)
    {
        var property = typeof(Config).GetProperty(name)!;
        Assert.Equal(fallback, property.GetValue(new Config()));
        foreach (int value in new[] { min, max })
        {
            Config config = new(); property.SetValue(config, value); config.Validate();
        }
        foreach (int value in new[] { int.MinValue, min - 1, max + 1, int.MaxValue })
        {
            Config config = new(); property.SetValue(config, value);
            Assert.Equal(name, Assert.Throws<ArgumentOutOfRangeException>(config.Validate).ParamName);
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(config));
            Config loaded = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(config))!;
            Assert.Equal(Math.Clamp(value, min, max), property.GetValue(loaded));
        }
    }

    [Fact]
    public void AllReaderSettingsSurviveCloneBothCopiesAndTwoReloads()
    {
        using TempDirectory directory = new();
        Config config = new() { ReaderDateStyle = "SinceOp", ReaderSpoilers = "Hover", GalleryFitUpscaleLimited = true };
        foreach (object[] row in Numbers()) typeof(Config).GetProperty((string)row[0])!.SetValue(config, row[3]);
        Config clone = config.Clone(), copy = new(), settings = new();
        copy.CopyFrom(clone); settings.CopySettingsFieldsFrom(copy);
        settings.SaveConfig(directory.Path);
        Config restarted = Config.LoadConfig(directory.Path); Assert.Null(restarted.LoadWarning);
        restarted.SaveConfig(directory.Path);
        Config second = Config.LoadConfig(directory.Path); Assert.Null(second.LoadWarning);
        foreach (Config current in new[] { clone, copy, settings, restarted, second })
        {
            Assert.Equal("SinceOp", current.ReaderDateStyle); Assert.Equal("Hover", current.ReaderSpoilers);
            Assert.True(current.GalleryFitUpscaleLimited);
            foreach (object[] row in Numbers()) Assert.Equal(row[3], typeof(Config).GetProperty((string)row[0])!.GetValue(current));
        }
    }

    [Fact]
    public void AbsentOrUnknownPresentationChoicesKeepTheOldBehavior()
    {
        Config old = JsonSerializer.Deserialize<Config>("{}")!;
        Assert.Equal(ReaderDateStyle.System, ReaderSettingsPreferences.DateStyle(old));
        Assert.Equal(ReaderSpoilerMode.Hide, ReaderSettingsPreferences.Spoilers(old));
        Assert.False(ReaderSettingsPreferences.Gallery(old).Limit);
        Config invalid = JsonSerializer.Deserialize<Config>("{\"ReaderDateStyle\":\"unknown\",\"ReaderSpoilers\":\"unknown\"}")!;
        Assert.Equal("System", invalid.ReaderDateStyle); Assert.Equal("Hide", invalid.ReaderSpoilers);
    }

    [Fact]
    public async Task PostFilterLimitsReachTheLiveLeaseAtStartAndAfterSave()
    {
        using TempDirectory directory = new();
        new Config { AutomaticUpdateChecks = false, SaveUrlsOnExit = false, PostFilterRuleLimit = 40, HiddenPostLimit = 2000,
            DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        await using DesktopSessionHost host = new(directory.Path, Array.Empty<IImageBoard>());
        await host.StartAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(40, host.StoreLimits.PostFilterRules); Assert.Equal(2000, host.StoreLimits.HiddenPosts);
        Config draft = host.Config.Clone(); draft.PostFilterRuleLimit = 30; draft.HiddenPostLimit = 1000;
        await host.ApplySettingsAsync(draft).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(30, host.StoreLimits.PostFilterRules); Assert.Equal(1000, host.StoreLimits.HiddenPosts);
    }
}
