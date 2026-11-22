using System.Runtime.CompilerServices;
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
            Assert.Equal(valid, SettingsTransfer.Export(candidate, "1.1.0")[nameof(Config.HeaderTaglineIntervalMinutes)]!.GetValue<int>());
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
    public void TheSettingsPageHasAConditionalRowThatResetsAndSavesIt()
    {
        string axaml = Source("Desktop", "Views", "Settings", "SettingsEditor.axaml");
        int row = axaml.IndexOf("x:Name=\"HeaderTaglineIntervalMinutesInput\"", StringComparison.Ordinal);
        Assert.True(row > 0);
        Assert.Contains("Minimum=\"1\" Maximum=\"1440\"", axaml.Substring(row, 200));
        Assert.Contains("SettingsConditional.When=\"{Binding HeaderTaglineIntervalEnabled}\"", axaml.Substring(Math.Max(0, row - 1800), 1800));
        string editor = Source("Desktop", "Views", "Settings", "SettingsEditor.HeaderTagline.cs");
        Assert.Contains("Config.HeaderTaglineIntervalMinutesMinimum, Config.HeaderTaglineIntervalMinutesMaximum", editor);
        Assert.Contains("ResetNumber(HeaderTaglineIntervalMinutesInput, minutes);", editor);
        Assert.Contains("candidate.HeaderTaglineIntervalMinutes = HeaderTaglineIntervalForSave();", Source("Desktop", "Views", "Settings", "SettingsEditor.Viewer.cs"));
        Assert.Contains("LoadHeaderTaglineIntervalDraft(defaults.HeaderTaglineIntervalMinutes);", Source("Desktop", "Views", "Settings", "SettingsEditor.Reset.cs"));
        Assert.Contains("HeaderTaglineNumericFields()", Source("Desktop", "Views", "Settings", "SettingsEditor.Validation.cs"));
        Assert.Contains("[\"HeaderTaglineIntervalMinutes\"]", Source("Desktop", "Views", "Settings", "SettingsSearchAliases.cs"));
    }

    [Fact]
    public void TheModeIsKnownAndPicksLikeEveryStartWhileTheWindowOwnsOneStoppedTimer()
    {
        Assert.Contains(TaglinePicker.ModeInterval, TaglinePicker.Modes);
        Assert.Equal(TaglinePicker.ModeInterval, TaglinePicker.NormalizeMode("interval"));
        Assert.Equal(TaglinePicker.ModePerStart, TaglinePicker.NormalizeMode("every-n"));
        Config config = new() { HeaderTagline = "interval" };
        Assert.Equal("Interval", config.HeaderTagline);
        IReadOnlyList<TaglineEntry> pool = TaglineCatalog.Available();
        Assert.NotNull(TaglinePicker.Pick(TaglinePicker.ModeInterval, string.Empty, pool, new DateOnly(2026, 11, 21), null, null, atStart: true, new Random(1)).Entry);
        string window = Source("Desktop", "Shell", "MainWindow.Tagline.cs");
        Assert.Contains("TimeSpan.FromMinutes(minutes)", window);
        Assert.Contains("StopTaglineRotation();", window.Substring(window.IndexOf("private void StopHeaderTagline()", StringComparison.Ordinal)));
        Assert.Contains("_taglineRotationTimer.Tick -= OnTaglineRotationTick;", window);
    }

    [Fact]
    public void LabelAndHintExistInEnglishGermanAndEveryBundledPack()
    {
        foreach (string key in new[] { "settings.headerTagline.interval", "settings.headerTagline.intervalLabel", "settings.headerTagline.intervalHint" })
        {
            foreach (string language in new[] { "en", "de" })
            {
                Texts.SetLanguage(language);
                Assert.NotEqual(key, Texts.T(key));
            }
            string[] packs = Directory.GetFiles(Path.Combine(RepoRoot(), "Desktop", "Localization", "Packs"), "*.json");
            Assert.Equal(10, packs.Length);
            foreach (string pack in packs)
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(pack));
                Assert.True(document.RootElement.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.TryGetProperty(key, out JsonElement text) && text.GetString()?.Length > 0)
                    || document.RootElement.TryGetProperty(key, out JsonElement flat) && flat.GetString()?.Length > 0, pack + " " + key);
            }
        }
        Texts.SetLanguage("en");
        Assert.Contains("{0}", Texts.T("settings.headerTagline.intervalHint"));
        Assert.Contains("{2}", Texts.T("settings.headerTagline.intervalHint"));
    }

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));
}
