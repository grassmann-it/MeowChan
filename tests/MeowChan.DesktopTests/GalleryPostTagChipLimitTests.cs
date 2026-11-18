using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop;

namespace MeowChan.DesktopTests;

// Opus review 15.11. (finding 8a): the number of tag chips of the gallery's Post segment was a fixed 30. Every count limit is a setting, so it is
// Config.GalleryPostTagChipLimit (default 30, from 5 to 200): clone, copy, export, import, reset, a row of the Settings page and its texts in all 12
// languages. The chips themselves are pinned in GalleryRatingPanelTests.
[Collection("Localization")]
public sealed class GalleryPostTagChipLimitTests : IDisposable
{
    public GalleryPostTagChipLimitTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void TheLimitHasADefaultOfThirtyFromFiveToTwoHundredAndEveryPathCarriesIt()
    {
        Assert.Equal((30, 5, 200), (Config.GalleryPostTagChipLimitDefault, Config.GalleryPostTagChipLimitMinimum, Config.GalleryPostTagChipLimitMaximum));
        Config defaults = new();
        Assert.Equal(30, defaults.GalleryPostTagChipLimit);
        defaults.Validate();
        foreach (int valid in new[] { 5, 77, 200 })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryPostTagChipLimit = valid;
            candidate.Validate();
            Assert.Equal(valid, candidate.Clone().GalleryPostTagChipLimit);
            Config target = new();
            target.CopySettingsFieldsFrom(candidate);
            Assert.Equal(valid, target.GalleryPostTagChipLimit);
            Config copy = new();
            copy.CopyFrom(candidate);
            Assert.Equal(valid, copy.GalleryPostTagChipLimit);
            Assert.Equal(valid, SettingsTransfer.CreatePortableSnapshot(candidate).GalleryPostTagChipLimit);
            Assert.Equal(valid, SettingsTransfer.Export(candidate, "1.1.0")[nameof(Config.GalleryPostTagChipLimit)]!.GetValue<int>());
        }
        foreach (int invalid in new[] { 4, 201, int.MinValue, int.MaxValue })
        {
            Config candidate = defaults.Clone();
            candidate.GalleryPostTagChipLimit = invalid;
            Assert.Throws<ArgumentOutOfRangeException>(() => candidate.Validate());
        }
    }

    [Theory]
    [InlineData("100", 100)]
    [InlineData("1", 5)]
    [InlineData("100000", 200)]
    [InlineData("1.5", 30)]
    [InlineData("\"x\"", 30)]
    [InlineData("{}", 30)]
    public void LoadingRepairsOnlyThePreference(string raw, int expected)
    {
        string directory = Directory.CreateTempSubdirectory("meowchan-chiplimit-").FullName;
        try
        {
            new Config { DownloadPath = Path.Combine(directory, "media"), MaxParallelDownloads = 7 }.SaveConfig(directory);
            string path = Path.Combine(directory, "settings.json");
            JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document[nameof(Config.GalleryPostTagChipLimit)] = JsonNode.Parse(raw);
            File.WriteAllText(path, document.ToJsonString());
            Config loaded = Config.LoadConfig(directory);
            Assert.Null(loaded.LoadWarning);
            Assert.Equal(expected, loaded.GalleryPostTagChipLimit);
            Assert.Equal(7, loaded.MaxParallelDownloads);
            loaded.SaveConfig(directory);
            using JsonDocument saved = JsonDocument.Parse(File.ReadAllText(path));
            Assert.Equal(expected, saved.RootElement.GetProperty(nameof(Config.GalleryPostTagChipLimit)).GetInt32());
            // Old settings without the value stay loadable and get the default.
            document.Remove(nameof(Config.GalleryPostTagChipLimit));
            File.WriteAllText(path, document.ToJsonString());
            Assert.Equal(30, Config.LoadConfig(directory).GalleryPostTagChipLimit);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TheSettingsPageHasARowForItAndResetsAndSavesIt()
    {
        string axaml = Source("Desktop", "Views", "Settings", "SettingsEditor.axaml");
        int row = axaml.IndexOf("x:Name=\"GalleryPostTagChipLimitInput\"", StringComparison.Ordinal);
        Assert.True(row > 0);
        Assert.Contains("Minimum=\"5\" Maximum=\"200\"", axaml.Substring(row, 200));
        string editor = Source("Desktop", "Views", "Settings", "SettingsEditor.Viewer.cs");
        Assert.Contains("candidate.GalleryPostTagChipLimit = GetInteger(GalleryPostTagChipLimit, GalleryPostTagChipLimitLabel, Config.GalleryPostTagChipLimitMinimum, Config.GalleryPostTagChipLimitMaximum);", editor);
        Assert.Contains("ResetNumber(GalleryPostTagChipLimitInput, config.GalleryPostTagChipLimit);", editor);
        Assert.Contains("yield return (GalleryPostTagChipLimitInput, GalleryPostTagChipLimit, GalleryPostTagChipLimitLabel", editor);
        Assert.Contains("_gallery.PostTagChipLimit = _config.GalleryPostTagChipLimit;", Source("Desktop", "Shell", "MainWindow.GalleryPreferences.cs"));
    }

    [Fact]
    public void LabelAndHintExistInEnglishGermanAndEveryBundledPack()
    {
        foreach (string key in new[] { "settings.galleryPostTagChipLimit", "settings.galleryPostTagChipLimitHint" })
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
        Assert.Contains("{0}", Texts.T("settings.galleryPostTagChipLimitHint"));
        Assert.Contains("{2}", Texts.T("settings.galleryPostTagChipLimitHint"));
    }

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));
}
