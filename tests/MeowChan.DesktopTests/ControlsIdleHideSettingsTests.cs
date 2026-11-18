using System.Text.Json;
using System.Text.Json.Nodes;

namespace MeowChan.DesktopTests;

public sealed class ControlsIdleHideSettingsTests
{
    [Fact]
    public void MissingPreferenceUsesTheDefault()
    {
        Assert.Equal((2000, 500, 30_000),
            (Config.ControlsIdleHideMsDefault, Config.ControlsIdleHideMsMinimum, Config.ControlsIdleHideMsMaximum));
        Assert.Equal(2000, new Config().ControlsIdleHideMs);
        Config loaded = JsonSerializer.Deserialize("{}", ConfigJsonContext.Default.Config)!;
        Assert.Equal(2000, loaded.ControlsIdleHideMs);
        loaded.Validate();
    }

    [Theory]
    [InlineData(500)]
    [InlineData(2000)]
    [InlineData(30_000)]
    public void ValidValuesSurviveCloneCopiesAndSaveLoad(int value)
    {
        using TempDirectory directory = new();
        Config original = new() { ControlsIdleHideMs = value, DownloadPath = Path.Combine(directory.Path, "media") };
        Config clone = original.Clone();
        Config settingsCopy = new();
        settingsCopy.CopySettingsFieldsFrom(clone);
        Config fullCopy = new();
        fullCopy.CopyFrom(settingsCopy);
        fullCopy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config candidate in new[] { original, clone, settingsCopy, fullCopy, loaded })
        {
            Assert.Equal(value, candidate.ControlsIdleHideMs);
            candidate.Validate();
        }
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.Path, "settings.json")));
        Assert.Equal(value, saved.RootElement.GetProperty(nameof(Config.ControlsIdleHideMs)).GetInt32());
    }

    [Theory]
    [InlineData("-2147483648", 500)]
    [InlineData("-1", 500)]
    [InlineData("0", 500)]
    [InlineData("499", 500)]
    [InlineData("500", 500)]
    [InlineData("2000", 2000)]
    [InlineData("30000", 30_000)]
    [InlineData("30001", 30_000)]
    [InlineData("2147483647", 30_000)]
    [InlineData("null", 2000)]
    [InlineData("true", 2000)]
    [InlineData("false", 2000)]
    [InlineData("\"1200\"", 2000)]
    [InlineData("\"invalid\"", 2000)]
    [InlineData("1.5", 2000)]
    [InlineData("1500.0", 2000)]
    [InlineData("1e3", 2000)]
    [InlineData("2147483648", 2000)]
    [InlineData("-2147483649", 2000)]
    [InlineData("1e100", 2000)]
    [InlineData("[]", 2000)]
    [InlineData("[1,{\"nested\":[true]}]", 2000)]
    [InlineData("{}", 2000)]
    [InlineData("{\"nested\":[1,2]}", 2000)]
    public void LoadingRepairsOnlyThePreferenceAndCanSaveItAgain(string raw, int expected)
    {
        using TempDirectory directory = new();
        new Config { DownloadPath = Path.Combine(directory.Path, "media"), MaxParallelDownloads = 7 }.SaveConfig(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        document[nameof(Config.ControlsIdleHideMs)] = JsonNode.Parse(raw);
        // An unrelated preference following a skipped object/array must still be read.
        document.Remove(nameof(Config.VideoVolume));
        document[nameof(Config.VideoVolume)] = 31;
        File.WriteAllText(path, document.ToJsonString());

        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(expected, loaded.ControlsIdleHideMs);
        Assert.Equal(7, loaded.MaxParallelDownloads);
        Assert.Equal(31, loaded.VideoVolume);
        loaded.Validate();
        loaded.SaveConfig(directory.Path);
        using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(expected, saved.RootElement.GetProperty(nameof(Config.ControlsIdleHideMs)).GetInt32());
        Config reloaded = Config.LoadConfig(directory.Path);
        Assert.Null(reloaded.LoadWarning);
        Assert.Equal(expected, reloaded.ControlsIdleHideMs);
    }

    [Fact]
    public void OldSettingsWithoutThePreferenceRemainLoadable()
    {
        using TempDirectory directory = new();
        new Config { DownloadPath = Path.Combine(directory.Path, "media"), MaxParallelDownloads = 7 }.SaveConfig(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.True(document.Remove(nameof(Config.ControlsIdleHideMs)));
        File.WriteAllText(path, document.ToJsonString());
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(2000, loaded.ControlsIdleHideMs);
        Assert.Equal(7, loaded.MaxParallelDownloads);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(499)]
    [InlineData(30_001)]
    [InlineData(int.MaxValue)]
    public void ProgrammaticOutOfRangeValuesAreStillRejectedBeforeSaving(int value)
    {
        using TempDirectory directory = new();
        Config invalid = new() { ControlsIdleHideMs = value, DownloadPath = Path.Combine(directory.Path, "media") };
        Assert.Throws<ArgumentOutOfRangeException>(() => invalid.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => invalid.SaveConfig(directory.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }
}
