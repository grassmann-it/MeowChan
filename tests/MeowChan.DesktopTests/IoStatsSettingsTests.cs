using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Stats;

namespace MeowChan.DesktopTests;

/// <summary>The nine network and disk statistics settings: defaults, bounds, copies, save and reload at the largest values.</summary>
public sealed class IoStatsSettingsTests
{
    // Property, default, minimum, maximum.
    private static readonly (string Property, int Default, int Minimum, int Maximum)[] Numbers =
    {
        (nameof(Config.StatsSampleIntervalMs), 1000, 500, 10_000),
        (nameof(Config.StatsHistorySamples), 120, 30, 3600),
        (nameof(Config.StatsBackgroundIntervalMs), 5000, 2000, 60_000),
        (nameof(Config.StatsRetentionDays), 30, 1, 365),
        (nameof(Config.StatsPersistIntervalMinutes), 5, 1, 60),
        (nameof(Config.StatsMaxSources), 64, 8, 256),
    };

    private static readonly string[] Booleans = [nameof(Config.StatsTrayTooltip), nameof(Config.StatsTrayMenu), nameof(Config.StatsPersist)];

    private static int Get(Config config, string property) => (int)typeof(Config).GetProperty(property)!.GetValue(config)!;
    private static void Set(Config config, string property, int value) => typeof(Config).GetProperty(property)!.SetValue(config, value);
    private static int Constant(string name) => (int)typeof(Config).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetRawConstantValue()!;

    [Fact]
    public void EveryNumberHasItsDefaultAndIsBoundedLikeTheExistingLimits()
    {
        Config defaults = new();
        foreach ((string property, int fallback, int minimum, int maximum) in Numbers)
        {
            Assert.Equal(fallback, Get(defaults, property));
            Assert.Equal(fallback, Constant(property + "Default"));
            Assert.Equal(minimum, Constant(property + "Minimum"));
            Assert.Equal(maximum, Constant(property + "Maximum"));
            defaults.Validate();
            foreach (int valid in new[] { minimum, maximum })
            {
                Config candidate = defaults.Clone();
                Set(candidate, property, valid);
                candidate.Validate();
            }
            foreach (int invalid in new[] { minimum - 1, maximum + 1, int.MinValue, int.MaxValue, 0, -1 })
            {
                if (invalid >= minimum && invalid <= maximum) continue;
                Config candidate = defaults.Clone();
                Set(candidate, property, invalid);
                Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
                Assert.Throws<ArgumentOutOfRangeException>(() => new Config().CopySettingsFieldsFrom(candidate));
            }
        }
    }

    [Fact]
    public void TheThreeSwitchesAreOnByDefault()
    {
        Config config = new();
        Assert.True(config.StatsTrayTooltip);
        Assert.True(config.StatsTrayMenu);
        Assert.True(config.StatsPersist);
    }

    [Fact]
    public void TheDefaultsAreTheOptionsDefaults()
    {
        IoStatsOptions options = IoStatsOptions.FromConfig(new Config());
        Assert.Equal(IoStatsOptions.Default, options);
        Assert.Equal((1000, 120, 5000, true, true, true, 30, 5, 64), (options.SampleIntervalMs, options.HistorySamples,
            options.BackgroundIntervalMs, options.TrayTooltip, options.TrayMenu, options.Persist, options.RetentionDays,
            options.PersistIntervalMinutes, options.MaxSources));
    }

    [Fact]
    public void EverySettingSurvivesCloneCopiesAndSaveReloadAtItsLargestValue()
    {
        using TempDirectory directory = new();
        Config original = new();
        foreach ((string property, _, _, int maximum) in Numbers) Set(original, property, maximum);
        original.StatsTrayTooltip = false; original.StatsTrayMenu = false; original.StatsPersist = false;
        Config clone = original.Clone();
        Config live = new();
        live.CopySettingsFieldsFrom(clone);
        Config copy = new();
        copy.CopyFrom(live);
        copy.SaveConfig(directory.Path);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        foreach (Config config in new[] { clone, live, loaded })
        {
            foreach ((string property, _, _, int maximum) in Numbers) Assert.Equal(maximum, Get(config, property));
            Assert.False(config.StatsTrayTooltip);
            Assert.False(config.StatsTrayMenu);
            Assert.False(config.StatsPersist);
        }
    }

    [Fact]
    public void ReadingAcceptsEveryMinimumAndMaximumAndRejectsOutsideValues()
    {
        foreach ((string property, _, int minimum, int maximum) in Numbers)
        {
            foreach (int value in new[] { minimum, maximum })
            {
                using TempDirectory directory = new();
                Config config = new();
                Set(config, property, value);
                config.SaveConfig(directory.Path);
                Config loaded = Config.LoadConfig(directory.Path);
                Assert.Null(loaded.LoadWarning);
                Assert.Equal(value, Get(loaded, property));
            }
            using TempDirectory outside = new();
            new Config().SaveConfig(outside.Path);
            string file = Path.Combine(outside.Path, "settings.json");
            string text = File.ReadAllText(file);
            string changed = Regex.Replace(text, "(\"" + property + "\"\\s*:\\s*)\\d+", "${1}" + (maximum + 1));
            Assert.NotEqual(text, changed);
            File.WriteAllText(file, changed);
            // An out-of-range document is a damaged one, like for every other number: the defaults are used and the file is kept.
            Assert.NotNull(Config.LoadConfig(outside.Path).LoadWarning);
            Assert.True(File.Exists(file));
        }
    }

    [Fact]
    public void AnOldSettingsFileWithoutTheKeysKeepsTheDefaults()
    {
        using TempDirectory directory = new();
        new Config().SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "settings.json");
        JsonDocument document = JsonDocument.Parse(File.ReadAllText(file));
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
                if (!property.Name.StartsWith("Stats", StringComparison.Ordinal)) property.WriteTo(writer);
            writer.WriteEndObject();
        }
        File.WriteAllBytes(file, stream.ToArray());
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.Equal(IoStatsOptions.Default, IoStatsOptions.FromConfig(loaded));
    }

    [Fact]
    public void AMalformedSwitchFallsBackToOnWithoutDiscardingTheOtherSettings()
    {
        using TempDirectory directory = new();
        Config config = new() { RecheckTimer = 123, StatsTrayTooltip = false };
        config.SaveConfig(directory.Path);
        string file = Path.Combine(directory.Path, "settings.json");
        string text = File.ReadAllText(file);
        string changed = Regex.Replace(text, "(\"StatsTrayTooltip\"\\s*:\\s*)false", "${1}\"maybe\"");
        Assert.NotEqual(text, changed);
        File.WriteAllText(file, changed);
        Config loaded = Config.LoadConfig(directory.Path);
        Assert.Null(loaded.LoadWarning);
        Assert.True(loaded.StatsTrayTooltip);
        Assert.Equal(123, loaded.RecheckTimer);
    }
}
