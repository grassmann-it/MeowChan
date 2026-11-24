using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// The header tagline mode "Interval" (New every N minutes) and its companion setting Config.HeaderTaglineIntervalMinutes (default 5, from 1 to 1440):
// clone, copy, export, import, reset, a conditional row of the Settings page, the window's timer and the texts in all 12 languages.
[Collection("Localization")]
public sealed class TaglineIntervalTests : IDisposable
{
    public TaglineIntervalTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void TheMinutesHaveADefaultOfFiveFromOneTo1440AndEveryPathCarriesThem()
    {
        Assert.Equal((5, 1, 1440), (Config.HeaderTaglineIntervalMinutesDefault, Config.HeaderTaglineIntervalMinutesMinimum, Config.HeaderTaglineIntervalMinutesMaximum));
        Config defaults = new();
        Assert.Equal(5, defaults.HeaderTaglineIntervalMinutes);
        defaults.Validate();
        foreach (int valid in new[] { 1, 77, 1440 })
        {
            Config candidate = defaults.Clone();
            candidate.HeaderTaglineIntervalMinutes = valid;
            candidate.Validate();
            Assert.Equal(valid, candidate.Clone().HeaderTaglineIntervalMinutes);
            Config target = new();
            target.CopySettingsFieldsFrom(candidate);
            Assert.Equal(valid, target.HeaderTaglineIntervalMinutes);
            Config copy = new();
            copy.CopyFrom(candidate);
            Assert.Equal(valid, copy.HeaderTaglineIntervalMinutes);
            Assert.Equal(valid, SettingsTransfer.CreatePortableSnapshot(candidate).HeaderTaglineIntervalMinutes);
            JsonObject transfer = SettingsTransfer.Export(candidate, "1.1.0");
            Assert.Equal(valid, transfer[nameof(Config.HeaderTaglineIntervalMinutes)]!.GetValue<int>());
            Config restored = JsonSerializer.Deserialize(transfer.ToJsonString(), ConfigJsonContext.Default.Config)!;
            Assert.Equal(valid, restored.HeaderTaglineIntervalMinutes);
        }
        foreach (int invalid in new[] { 0, 1441, int.MinValue, int.MaxValue })
        {
            Config candidate = defaults.Clone();
            candidate.HeaderTaglineIntervalMinutes = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
        }
    }

    [Theory]
    [InlineData("100", 100)]
    [InlineData("0", 1)]
    [InlineData("100000", 1440)]
    [InlineData("1.5", 5)]
    [InlineData("\"x\"", 5)]
    [InlineData("{}", 5)]
    public void LoadingRepairsOnlyThePreference(string raw, int expected)
    {
        string directory = Directory.CreateTempSubdirectory("meowchan-taglineminutes-").FullName;
        try
        {
            new Config { DownloadPath = Path.Combine(directory, "media"), MaxParallelDownloads = 7 }.SaveConfig(directory);
            string path = Path.Combine(directory, "settings.json");
            JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document[nameof(Config.HeaderTaglineIntervalMinutes)] = JsonNode.Parse(raw);
            File.WriteAllText(path, document.ToJsonString());
            Config loaded = Config.LoadConfig(directory);
            Assert.Null(loaded.LoadWarning);
            Assert.Equal(expected, loaded.HeaderTaglineIntervalMinutes);
            Assert.Equal(7, loaded.MaxParallelDownloads);
            loaded.SaveConfig(directory);
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(expected, saved.RootElement.GetProperty(nameof(Config.HeaderTaglineIntervalMinutes)).GetInt32());
            // Old settings without the value stay loadable and get the default.
            document.Remove(nameof(Config.HeaderTaglineIntervalMinutes));
            File.WriteAllText(path, document.ToJsonString());
            Assert.Equal(5, Config.LoadConfig(directory).HeaderTaglineIntervalMinutes);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TheModeIsKnownAndPicksLikeEveryStart()
    {
        Assert.Contains(TaglinePicker.ModeInterval, TaglinePicker.Modes);
        Assert.Equal(TaglinePicker.ModeInterval, TaglinePicker.NormalizeMode("interval"));
        Assert.Equal(TaglinePicker.ModePerStart, TaglinePicker.NormalizeMode("every-n"));
        Config config = new() { HeaderTagline = "interval" };
        Assert.Equal("Interval", config.HeaderTagline);
        IReadOnlyList<TaglineEntry> pool = TaglineCatalog.Available();
        DateOnly day = new(2026, 11, 21);
        Assert.Equal(
            TaglinePicker.Pick(TaglinePicker.ModePerStart, "", pool, day, null, null, atStart: true, new Random(1)),
            TaglinePicker.Pick(TaglinePicker.ModeInterval, "", pool, day, null, null, atStart: true, new Random(1)));
    }

    [Fact]
    public void LabelAndHintExistInEnglishGermanAndEveryBundledPack()
    {
        foreach (string language in new[] { "en", "de" }.Concat(Texts.BundledPackCodes()))
        {
            Texts.SetLanguage(language);
            foreach (string key in new[] { "settings.headerTagline.interval", "settings.headerTagline.intervalLabel", "settings.headerTagline.intervalHint" })
                Assert.False(string.IsNullOrWhiteSpace(Texts.Own(key)), language + " " + key);
            string formatted = Texts.T("settings.headerTagline.intervalHint", "⟨default⟩", "⟨min⟩", "⟨max⟩");
            Assert.Contains("⟨default⟩", formatted);
            Assert.Contains("⟨min⟩", formatted);
            Assert.Contains("⟨max⟩", formatted);
        }
    }
}
