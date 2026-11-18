using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan.Archive;
using MeowChan.Desktop;
using MeowChan.Tags;

namespace MeowChan.DesktopTests;

// Core/Config preparation only: no assertion here claims SettingsEditor or host integration.
[Collection("Localization")]
public sealed class CoreLimitSettingsTests
{
    public static IEnumerable<object[]> Limits()
    {
        yield return new object[] { nameof(Config.ArchiveTagFilterTagsPerList), 32, 8, 128 };
        yield return new object[] { nameof(Config.UrlTextExportLimitMiB), 1, 1, 64 };
        yield return new object[] { nameof(Config.SlashTagSuggestionCount), 8, 3, 20 };
    }

    private static int Get(Config config, string name) => (int)typeof(Config).GetProperty(name)!.GetValue(config)!;
    private static void Set(Config config, string name, int value) => typeof(Config).GetProperty(name)!.SetValue(config, value);
    private static int Constant(string name) =>
        (int)typeof(Config).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    [Theory]
    [MemberData(nameof(Limits))]
    public void DefaultsInclusiveBoundsAndStrictValidationMatchThePlan(string name, int fallback, int minimum, int maximum)
    {
        Assert.Equal(fallback, Get(new Config(), name));
        Assert.Equal(fallback, Constant(name + "Default"));
        Assert.Equal(minimum, Constant(name + "Minimum"));
        Assert.Equal(maximum, Constant(name + "Maximum"));
        foreach (int value in new[] { minimum, maximum })
        {
            Config valid = new();
            Set(valid, name, value);
            valid.Validate();
        }
        foreach (int value in new[] { int.MinValue, minimum - 1, maximum + 1, int.MaxValue })
        {
            Config invalid = new();
            Set(invalid, name, value);
            Assert.Equal(name, Assert.Throws<ArgumentOutOfRangeException>(invalid.Validate).ParamName);
            Assert.Equal(value, Get(invalid, name)); // Validate refuses, never silently clamps an edited draft.
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopyFrom(invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(invalid));
            Config loaded = JsonSerializer.Deserialize<Config>(JsonSerializer.Serialize(invalid))!;
            Assert.Equal(Math.Clamp(value, minimum, maximum), Get(loaded, name));
        }
    }

    [Fact]
    public void DefaultsAndRangesAreTiedToTheCoreComponents()
    {
        Assert.Equal((ArchiveTagFilter.DefaultTagsPerList, ArchiveTagFilter.MinimumTagsPerList, ArchiveTagFilter.MaximumConfigurableTagsPerList),
            (Config.ArchiveTagFilterTagsPerListDefault, Config.ArchiveTagFilterTagsPerListMinimum, Config.ArchiveTagFilterTagsPerListMaximum));
        Assert.Equal((UrlTextExportFileWriter.DefaultLimitMiB, UrlTextExportFileWriter.MinimumLimitMiB, UrlTextExportFileWriter.MaximumLimitMiB),
            (Config.UrlTextExportLimitMiBDefault, Config.UrlTextExportLimitMiBMinimum, Config.UrlTextExportLimitMiBMaximum));
        Assert.Equal((SlashTagSuggestions.DefaultSuggestionCount, SlashTagSuggestions.MinimumSuggestionCount, SlashTagSuggestions.MaximumSuggestionCount),
            (Config.SlashTagSuggestionCountDefault, Config.SlashTagSuggestionCountMinimum, Config.SlashTagSuggestionCountMaximum));
    }

    [Fact]
    public void EveryValueSurvivesCloneBothCopiesAndTwoReloads()
    {
        using TempDirectory directory = new();
        Config original = new()
        {
            ArchiveTagFilterTagsPerList = 128, UrlTextExportLimitMiB = 64, SlashTagSuggestionCount = 20,
            ShrinkWindowForBarebones = false, DownloadPath = Path.Combine(directory.Path, "media")
        };
        Config clone = original.Clone(), copy = new(), settings = new();
        copy.CopyFrom(clone);
        settings.CopySettingsFieldsFrom(copy);
        settings.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        loaded.SaveConfig(directory.Path);
        Config reloaded = Config.LoadConfig(directory.Path);
        Assert.Null(reloaded.LoadWarning);
        foreach (Config current in new[] { clone, copy, settings, loaded, reloaded })
        {
            Assert.Equal((128, 64, 20, false), (current.ArchiveTagFilterTagsPerList, current.UrlTextExportLimitMiB,
                current.SlashTagSuggestionCount, current.ShrinkWindowForBarebones));
            current.Validate();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutOfRangeFileValuesAreClampedWithoutRecoveryAndMissingFieldsKeepDefaults(bool above)
    {
        using TempDirectory directory = new();
        new Config { DownloadPath = Path.Combine(directory.Path, "media") }.SaveConfig(directory.Path);
        string path = Path.Combine(directory.Path, "settings.json");
        JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        foreach (object[] row in Limits()) document[(string)row[0]] = above ? int.MaxValue : int.MinValue;
        document[nameof(Config.ShrinkWindowForBarebones)] = false;
        File.WriteAllText(path, document.ToJsonString());
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.False(loaded.ShrinkWindowForBarebones);
        foreach (object[] row in Limits()) Assert.Equal((int)row[above ? 3 : 2], Get(loaded, (string)row[0]));
        loaded.Validate();

        foreach (object[] row in Limits()) document.Remove((string)row[0]);
        document.Remove(nameof(Config.ShrinkWindowForBarebones));
        File.WriteAllText(path, document.ToJsonString());
        Config legacy = Config.LoadConfig(directory.Path);
        Assert.Null(legacy.LoadWarning);
        foreach (object[] row in Limits()) Assert.Equal((int)row[1], Get(legacy, (string)row[0]));
        Assert.True(legacy.ShrinkWindowForBarebones);
    }

    [Fact]
    public void ShrinkPreferenceHasATolerantTrueDefaultButExplicitFalseSurvives()
    {
        foreach (string json in new[] { "{}", "{\"ShrinkWindowForBarebones\":null}", "{\"ShrinkWindowForBarebones\":\"invalid\"}" })
            Assert.True(JsonSerializer.Deserialize<Config>(json)!.ShrinkWindowForBarebones);
        Assert.False(JsonSerializer.Deserialize<Config>("{\"ShrinkWindowForBarebones\":false}")!.ShrinkWindowForBarebones);
    }
}
