using System.Text.Json;
using System.Text.Json.Nodes;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// P9: Config.ExportFilenameTemplate, the filename template of exported originals. Empty (the default) keeps the current naming; a template is
// validated by ExportFilenameTemplate (SafeFilenameTemplate underneath). Covered here: default, clone/copy/transfer, validation, a damaged file,
// the Downloads-page row (markup, reset, save, search aliases) and the texts in English, German and all bundled packs.
[Collection("Localization")]
public sealed class ExportFilenameTemplateSettingTests : IDisposable
{
    public ExportFilenameTemplateSettingTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    private static readonly string[] Keys =
    [
        "settings.exportFilenameTemplate", "settings.exportFilenameTemplateHint", "settings.exportFilenameTemplatePlaceholder",
        "settings.exportFilenameTemplateOff", "settings.exportFilenameTemplatePreview", "settings.exportFilenameTemplate.tooLong",
        "settings.exportFilenameTemplate.invalid", "settings.exportFilenameTemplate.extension", "settings.exportFilenameTemplate.unsafe",
        "settings.exportFilenameTemplate.nameTooLong", "settings.exportFilenameTemplate.unusable", "export.filenameTemplateNote"
    ];

    [Fact]
    public void TheDefaultIsEmptyAndEveryPathCarriesTheTemplate()
    {
        Config defaults = new();
        Assert.Equal(string.Empty, defaults.ExportFilenameTemplate);
        defaults.Validate();
        Config candidate = defaults.Clone();
        candidate.ExportFilenameTemplate = "{source}_{post}.{ext}";
        candidate.Validate();
        Assert.Equal("{source}_{post}.{ext}", candidate.Clone().ExportFilenameTemplate);
        Config target = new();
        target.CopySettingsFieldsFrom(candidate);
        Assert.Equal("{source}_{post}.{ext}", target.ExportFilenameTemplate);
        Config copy = new();
        copy.CopyFrom(candidate);
        Assert.Equal("{source}_{post}.{ext}", copy.ExportFilenameTemplate);
        Assert.Equal("{source}_{post}.{ext}", SettingsTransfer.CreatePortableSnapshot(candidate).ExportFilenameTemplate);
        Assert.Equal("{source}_{post}.{ext}", SettingsTransfer.Export(candidate, "1.1.1")[nameof(Config.ExportFilenameTemplate)]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{bogus}.{ext}")]
    [InlineData("{source}")]
    [InlineData("a/b.{ext}")]
    [InlineData("CON.{ext}")]
    public void AnUnusableTemplateFailsValidation(string template)
    {
        Config candidate = new() { ExportFilenameTemplate = template };
        ArgumentException failure = Assert.Throws<ArgumentException>(() => candidate.Validate());
        Assert.Equal(nameof(Config.ExportFilenameTemplate), failure.ParamName);
    }

    [Theory]
    [InlineData("\"{source}_{post}.{ext}\"", "{source}_{post}.{ext}")]
    [InlineData("\"{nope}.{ext}\"", "")]
    [InlineData("\"no extension token\"", "")]
    [InlineData("17", "")]
    [InlineData("{}", "")]
    public void LoadingRepairsOnlyThePreference(string raw, string expected)
    {
        string directory = Directory.CreateTempSubdirectory("meowchan-filenametemplate-").FullName;
        try
        {
            new Config { DownloadPath = Path.Combine(directory, "media"), MaxParallelDownloads = 7 }.SaveConfig(directory);
            string path = Path.Combine(directory, "settings.json");
            JsonObject document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            document[nameof(Config.ExportFilenameTemplate)] = JsonNode.Parse(raw);
            File.WriteAllText(path, document.ToJsonString());
            Config loaded = Config.LoadConfig(directory);
            Assert.Null(loaded.LoadWarning);
            Assert.Equal(expected, loaded.ExportFilenameTemplate);
            Assert.Equal(7, loaded.MaxParallelDownloads);
            // Old settings without the value stay loadable and keep the current naming.
            document.Remove(nameof(Config.ExportFilenameTemplate));
            File.WriteAllText(path, document.ToJsonString());
            Assert.Equal(string.Empty, Config.LoadConfig(directory).ExportFilenameTemplate);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void TheDownloadsPageHasARowWithPreviewAndInlineErrorAndResetsAndSavesIt()
    {
        string axaml = Source("Desktop", "Views", "Settings", "SettingsEditor.axaml");
        int downloads = axaml.IndexOf("x:Name=\"SettingsDownloadsPage\"", StringComparison.Ordinal);
        int row = axaml.IndexOf("x:Name=\"ExportFilenameTemplateInput\"", StringComparison.Ordinal);
        Assert.True(downloads > 0 && row > downloads);
        Assert.Contains("x:Name=\"ExportFilenameTemplatePreviewStatus\"", axaml);
        Assert.Contains("x:Name=\"ExportFilenameTemplateInputError\"", axaml);
        Assert.Contains("IsVisible=\"{Binding HasExportFilenameTemplateError}\"", axaml);
        string partial = Source("Desktop", "Views", "Settings", "SettingsEditor.FilenameTemplate.cs");
        Assert.Contains("candidate.ExportFilenameTemplate = ExportFilenameTemplate;", partial);
        Assert.Contains("ExportFilenameTemplate = config.ExportFilenameTemplate;", partial);
        Assert.Contains("ExportFilenameTemplate = defaults.ExportFilenameTemplate;", Source("Desktop", "Views", "Settings", "SettingsEditor.Reset.cs"));
        Assert.Contains("Text(ExportFilenameTemplateInput,", Source("Desktop", "Views", "Settings", "SettingsEditor.RowMenu.cs"));
        Assert.Contains("ValidateExportFilenameTemplateDraft()", Source("Desktop", "Views", "Settings", "SettingsEditor.axaml.cs"));
        Assert.Contains("[\"ExportFilenameTemplate\"]", Source("Desktop", "Views", "Settings", "SettingsSearchAliases.cs"));
        Assert.Contains("_archivePage.ExportFilenameTemplate = () => _config.ExportFilenameTemplate;", Source("Desktop", "Shell", "MainWindow.Navigation.cs"));
        Assert.Contains("run.Token, ExportFilenameTemplate?.Invoke()", Source("Desktop", "Views", "Archive", "ArchivePage.Favorites.cs"));
    }

    [Fact]
    public void TheSettingsSearchFindsTheRowByFilenameWords()
    {
        IReadOnlyList<string> aliases = SettingsSearchAliases.For("ExportFilenameTemplate", "settings.downloads");
        string joined = string.Join(' ', aliases);
        Assert.Contains("dateiname", joined);
        Assert.Contains("template", joined);
        Assert.Contains("export", joined);
    }

    [Fact]
    public void TextsExistInEnglishGermanAndEveryBundledPackWithTheSamePlaceholders()
    {
        string[] packs = Directory.GetFiles(Path.Combine(RepoRoot(), "Desktop", "Localization", "Packs"), "*.json");
        Assert.Equal(10, packs.Length);
        foreach (string key in Keys)
        {
            Assert.True(BuiltInCatalog.English.TryGetValue(key, out string? english) && english.Length > 0, key);
            Assert.True(BuiltInCatalog.German.TryGetValue(key, out string? german) && german.Length > 0, key);
            Assert.NotEqual(english, german);
            string placeholders = Placeholders(english);
            Assert.Equal(placeholders, Placeholders(german));
            foreach (string pack in packs)
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(pack));
                JsonElement strings = document.RootElement.GetProperty("strings");
                Assert.True(strings.TryGetProperty(key, out JsonElement text) && text.GetString()?.Length > 0, Path.GetFileName(pack) + " " + key);
                Assert.Equal(placeholders, Placeholders(text.GetString()!));
            }
        }
    }

    private static string Placeholders(string text) =>
        string.Join(',', System.Text.RegularExpressions.Regex.Matches(text, @"\{\d\}").Select(match => match.Value).Distinct().Order());

    private static string RepoRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory != null && !File.Exists(Path.Combine(directory, "MeowChan.csproj"))) directory = Path.GetDirectoryName(directory);
        return directory ?? throw new InvalidOperationException("The repository root was not found.");
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));
}
