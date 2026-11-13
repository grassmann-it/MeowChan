using System.Globalization;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BundledLanguagePackTests : IDisposable
{
    public BundledLanguagePackTests() => Reset();
    public void Dispose() => Reset();
    private static void Reset() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void BundledPackStaysWellBelowTheLoaderCeilings(string code)
    {
        // The loader would refuse a pack at the ceiling and the language would silently vanish from the choice.
        // This fails at 75 % so the ceilings are raised, with a reason, long before that happens.
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream("MeowChan.Desktop.Localization.Packs." + code + ".json")!;
        Assert.True(stream.Length <= Texts.MaximumPackBytes * 3 / 4,
            code + " pack is " + stream.Length + " bytes of " + Texts.MaximumPackBytes + " allowed; raise the ceiling in Texts.cs.");
        using JsonDocument document = JsonDocument.Parse(stream);
        int keys = document.RootElement.GetProperty("strings").EnumerateObject().Count();
        Assert.True(keys <= Texts.MaximumPackKeys * 3 / 4,
            code + " pack has " + keys + " keys of " + Texts.MaximumPackKeys + " allowed; raise the ceiling in Texts.cs.");
    }

    [Fact]
    public void BuiltInCatalogStaysWellBelowTheKeyCeilingAPackMustFit()
    {
        Assert.True(BuiltInCatalog.English.Count <= Texts.MaximumPackKeys * 3 / 4,
            "The English catalog has " + BuiltInCatalog.English.Count + " keys of " + Texts.MaximumPackKeys + " a pack may hold.");
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public void PackAtTheKeyCeilingLoadsAndOneKeyMoreIsRejectedWithAWarning(int extra, bool loads)
    {
        using TempDirectory directory = new();
        System.Text.StringBuilder json = new("{\"schemaVersion\":1,\"code\":\"it\",\"nativeName\":\"Italiano\",\"strings\":{\"action.close\":\"Chiudi\"");
        for (int index = 1; index < Texts.MaximumPackKeys + extra; index++)
            json.Append(",\"future.key.").Append(index).Append("\":\"x\"");
        json.Append("}}");
        File.WriteAllText(Path.Combine(directory.Path, "it.json"), json.ToString());

        Texts.Initialize(directory.Path);

        Assert.Equal(loads, Texts.Languages.Any(language => language.Code == "it"));
        Assert.Equal(loads ? 0 : 1, Texts.Warnings.Count(warning => warning.StartsWith("it.json:", StringComparison.Ordinal)));
    }

    [Fact]
    public void DefaultInitializationIncludesBundlesWithoutSelectingOneOrWritingFiles()
    {
        using TempDirectory directory = new();
        string missing = Path.Combine(directory.Path, "LanguagePacks");
        Texts.Initialize(missing);
        Assert.Equal(new[] { "en", "de", "es", "fr", "ru" }, Texts.Languages.Select(l => l.Code));
        Assert.Equal("en", Texts.Language);
        Assert.Equal("English", Texts.Languages[0].NativeName);
        Assert.Equal("Deutsch", Texts.Languages[1].NativeName);
        Assert.Empty(Texts.Warnings);
        Assert.False(Directory.Exists(missing));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
        Texts.Initialize(); // The default, no-directory startup path must also load resources.
        Assert.Equal(new[] { "en", "de", "es", "fr", "ru" }, Texts.Languages.Select(l => l.Code));
        Assert.Empty(Texts.Warnings);
    }

    private static bool IsExtraPluralForm(string key)
    {
        int dot = key.LastIndexOf('.');
        return dot > 0 && !BuiltInCatalog.English.ContainsKey(key) && BuiltInCatalog.English.ContainsKey(key[..dot])
            && key[(dot + 1)..] is "zero" or "two" or "few" or "many";
    }

    [Theory]
    [InlineData("es", "Español (Untested)", "Configuración")]
    [InlineData("fr", "Français (Untested)", "Paramètres")]
    [InlineData("ru", "Русский (Untested)", "Настройки")]
    public void RealAssemblyResourceLoadsEveryTranslationThroughNormalStartup(string code, string name, string title)
    {
        // No source-tree path, manual copy or Assembly.Location (empty in single-file apps).
        string resource = "MeowChan.Desktop.Localization.Packs." + code + ".json";
        Assert.Contains(resource, typeof(Texts).Assembly.GetManifestResourceNames());
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)!;
        Assert.NotNull(stream);
        using JsonDocument document = JsonDocument.Parse(stream);
        var translations = document.RootElement.GetProperty("strings").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        Assert.Equal(BuiltInCatalog.English.Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => p.Key).Order(StringComparer.Ordinal),
            // Optional extra number forms ("<key>.few") exist only in the pack, e.g. Russian.
            translations.Keys.Where(key => !IsExtraPluralForm(key)).Order(StringComparer.Ordinal));
        Assert.All(translations.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Texts.Initialize();
        Texts.SetLanguage(code);
        Assert.Empty(Texts.Warnings);
        Assert.Equal(name, Assert.Single(Texts.Languages, l => l.Code == code).NativeName);
        Assert.Equal(name, Assert.Single(Texts.Languages, l => l.Code == code).ToString());
        foreach (var (key, value) in translations) Assert.Equal(value, Texts.T(key));
        Assert.Equal(title, Texts.T("settings.title"));
        Assert.Equal("", Texts.T("savedSearch.prompt.None"));
        Assert.Equal("", Texts.T("savedSearch.confirm.None"));
        var time = new DateTime(2026, 11, 1, 14, 15, 0);
        Assert.Equal(string.Format(CultureInfo.GetCultureInfo(code), translations["shell.startupPreviewDetails"], time, 2, 3, 5),
            Texts.T("shell.startupPreviewDetails", time, 2, 3, 5));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void ReinitializationPreservesExplicitSavedSelection(string code)
    {
        using TempDirectory directory = new();
        Texts.SetLanguage(code);
        Texts.Initialize(directory.Path);
        Assert.Equal(code, Texts.Language);
        Assert.Empty(Texts.Warnings);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void ValidUserOverrideWinsWithoutRewritingItAndRemovalRestoresBundle(string code)
    {
        using TempDirectory directory = new();
        Texts.SetLanguage(code);
        string bundledTitle = Texts.T("settings.title");
        string path = WritePack(directory, "custom.json", code, "My custom language", "Custom settings");
        byte[] original = File.ReadAllBytes(path);
        Texts.Initialize(directory.Path);
        Assert.Equal(code, Texts.Language);
        Assert.Empty(Texts.Warnings);
        Assert.Equal("Custom settings", Texts.T("settings.title"));
        Assert.Equal("My custom language", Assert.Single(Texts.Languages, l => l.Code == code).NativeName);
        // A valid partial override is a whole custom catalog: missing keys keep the existing English fallback.
        Assert.Equal(BuiltInCatalog.English["action.openFolder"], Texts.T("action.openFolder"));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(new[] { "custom.json" }, Directory.GetFiles(directory.Path).Select(Path.GetFileName));
        File.Delete(path);
        Texts.Initialize(directory.Path);
        Assert.Equal(code, Texts.Language);
        Assert.Equal(bundledTitle, Texts.T("settings.title"));
        Assert.Empty(Texts.Warnings);
    }

    [Theory]
    [InlineData("es", "{")]
    [InlineData("fr", "{\"schemaVersion\":2,\"code\":\"fr\",\"nativeName\":\"Bad\",\"strings\":{\"settings.title\":\"Bad\"}}")]
    [InlineData("ru", "{\"schemaVersion\":1,\"code\":\"ru\",\"nativeName\":\"Bad\",\"strings\":{\"settings.title\":\"Bad\",\"progress.media\":\"{9}\"}}")]
    public void InvalidUserPackKeepsBundledSelectionAndWarnsWithoutRewritingFile(string code, string json)
    {
        using TempDirectory directory = new();
        Texts.SetLanguage(code);
        string title = Texts.T("settings.title");
        string path = Path.Combine(directory.Path, code + ".json");
        File.WriteAllText(path, json);
        byte[] original = File.ReadAllBytes(path);
        Texts.Initialize(directory.Path);
        Assert.Equal(code, Texts.Language);
        Assert.Equal(title, Texts.T("settings.title"));
        Assert.EndsWith(" (Untested)", Assert.Single(Texts.Languages, l => l.Code == code).NativeName);
        Assert.StartsWith(code + ".json: ", Assert.Single(Texts.Warnings));
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(5, Texts.Languages.Count);
    }

    [Fact]
    public void OversizedUserPackIsRejectedByTheSameSizeLimitAndKeepsBundle()
    {
        using TempDirectory directory = new();
        string path = Path.Combine(directory.Path, "fr.json");
        File.WriteAllText(path, new string(' ', 2_000_001));
        Texts.SetLanguage("fr");
        Texts.Initialize(directory.Path);
        Assert.Equal("fr", Texts.Language);
        Assert.Equal("Paramètres", Texts.T("settings.title"));
        Assert.Contains("exceeds 2 MB", Assert.Single(Texts.Warnings));
        Assert.Equal(2_000_001L, new FileInfo(path).Length);
    }

    [Theory]
    [InlineData("fr", "FR")]
    [InlineData("it", "IT")]
    public void DuplicateCustomCodesRemainErrorsRatherThanLastFileWins(string code, string duplicate)
    {
        using TempDirectory directory = new();
        WritePack(directory, "a.json", code, "First custom", "First settings");
        WritePack(directory, "b.json", duplicate, "Second custom", "Second settings");
        Texts.Initialize(directory.Path);
        Texts.SetLanguage(code);
        Assert.Equal("First settings", Texts.T("settings.title"));
        Assert.Single(Texts.Languages, l => l.Code == code);
        Assert.Contains("Duplicate language code:", Assert.Single(Texts.Warnings));
    }

    [Theory]
    [InlineData("EN", "Settings")]
    [InlineData("DE", "Einstellungen")]
    public void UserPacksCannotReplaceSupportedBuiltIns(string code, string title)
    {
        using TempDirectory directory = new();
        WritePack(directory, "custom.json", code, "Fake builtin", "Replaced");
        Texts.Initialize(directory.Path);
        Texts.SetLanguage(code);
        Assert.Equal(title, Texts.T("settings.title"));
        Assert.Contains("Duplicate language code:", Assert.Single(Texts.Warnings));
        Assert.Equal(5, Texts.Languages.Count);
    }

    [Fact]
    public void UnavailableSavedLanguageStillFallsBackToEnglish()
    {
        Texts.SetLanguage("zz");
        Assert.Equal("en", Texts.Language);
        Assert.Equal("Settings", Texts.T("settings.title"));
        Assert.Single(Texts.Warnings);
    }

    private static string WritePack(TempDirectory directory, string filename, string code, string name, string title)
    {
        string path = Path.Combine(directory.Path, filename);
        File.WriteAllText(path, JsonSerializer.Serialize(new { schemaVersion = 1, code, nativeName = name,
            strings = new Dictionary<string, string> { ["settings.title"] = title } }));
        return path;
    }
}
