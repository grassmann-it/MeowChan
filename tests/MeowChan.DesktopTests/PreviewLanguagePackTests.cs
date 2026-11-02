using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Real source pack files, not abbreviated translation fixtures. Deliberately isolate
// user-loader cases from bundles; BundledLanguagePackTests covers normal embedded startup.
[Collection("Localization")]
public sealed class PreviewLanguagePackTests : IDisposable
{
    public PreviewLanguagePackTests() => Reset();
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }
    private static void Reset() { Texts.InitializeWithoutBundlesForTests(); Texts.SetLanguage("en"); }

    [Theory]
    [InlineData("es", "Español (Untested)")]
    [InlineData("fr", "Français (Untested)")]
    [InlineData("ru", "Русский (Untested)")]
    public void SourcePackHasExactCurrentCatalogCoverageAndLoadsEveryValue(string code, string name)
    {
        string pack = PackPath(code);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(pack));
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(code, root.GetProperty("code").GetString());
        Assert.Equal(name, root.GetProperty("nativeName").GetString());
        var entries = root.GetProperty("strings").EnumerateObject().ToArray();
        // ToDictionary deliberately rejects duplicate JSON keys, which GetProperty
        // alone would hide. Runtime loader below also validates all placeholders.
        var translations = entries.ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        Assert.Equal(BuiltInCatalog.English.Where(pair => !string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).Order(StringComparer.Ordinal),
            translations.Keys.Order(StringComparer.Ordinal));
        Assert.All(translations.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        Assert.InRange(new FileInfo(pack).Length, 1, 2_000_000);

        using TempDirectory directory = Install(code);
        Texts.InitializeWithoutBundlesForTests(directory.Path);
        Texts.SetLanguage(code);
        Assert.Empty(Texts.Warnings);
        Assert.Equal(name, Assert.Single(Texts.Languages, l => l.Code == code).NativeName);
        foreach (var (key, value) in translations) Assert.Equal(value, Texts.T(key));
        // Blank saved-search sentinels deliberately fall back: schema1 rejects
        // empty translations, and adding visible filler would change the UI.
        Assert.Equal("", Texts.T("savedSearch.prompt.None"));
        Assert.Equal("", Texts.T("savedSearch.confirm.None"));
        Assert.Empty(Texts.ValidateBuiltInCatalogs());
    }

    [Theory]
    [InlineData("es", "Configuración")]
    [InlineData("fr", "Paramètres")]
    [InlineData("ru", "Настройки")]
    public void SelectedPreviewFormatsWithItsOwnCultureAndDoesNotChangeProcessCulture(string code, string title)
    {
        CultureInfo culture = CultureInfo.CurrentCulture;
        CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        using TempDirectory directory = Install(code);
        Texts.InitializeWithoutBundlesForTests(directory.Path);
        Texts.SetLanguage(code.ToUpperInvariant());
        Assert.Equal(code, Texts.Language);
        Assert.Equal(title, Texts.T("settings.title"));
        string template = Texts.T("progress.media");
        Assert.Equal(string.Format(CultureInfo.GetCultureInfo(code), template, 1, 8, 12.5),
            Texts.T("progress.media", 1, 8, 12.5));
        Assert.Same(culture, CultureInfo.CurrentCulture);
        Assert.Same(uiCulture, CultureInfo.CurrentUICulture);
    }

    [Theory]
    [InlineData("es", false)]
    [InlineData("es", true)]
    [InlineData("fr", false)]
    [InlineData("fr", true)]
    [InlineData("ru", false)]
    [InlineData("ru", true)]
    public void MissingOrDamagedSelectedPreviewFallsBackWithoutChangingTheOtherLanguages(string code, bool damaged)
    {
        using TempDirectory directory = Install(code);
        string sibling = code == "es" ? "fr" : "es";
        File.Copy(PackPath(sibling), Path.Combine(directory.Path, sibling + ".json"));
        Texts.InitializeWithoutBundlesForTests(directory.Path);
        Texts.SetLanguage(code);
        if (damaged) File.WriteAllText(Path.Combine(directory.Path, code + ".json"), "{bad json");
        else File.Delete(Path.Combine(directory.Path, code + ".json"));
        Texts.InitializeWithoutBundlesForTests(directory.Path);
        Assert.Equal("en", Texts.Language);
        Assert.Equal("Settings", Texts.T("settings.title"));
        Assert.Equal(damaged ? 2 : 1, Texts.Warnings.Count); // optional bad file + unavailable selected language
        Assert.Equal(new[] { "en", "de", sibling }, Texts.Languages.Select(l => l.Code));
        Texts.SetLanguage(sibling);
        Assert.Equal(sibling == "es" ? "Configuración" : "Paramètres", Texts.T("settings.title"));
        File.Delete(Path.Combine(directory.Path, code + ".json"));
        Texts.InitializeWithoutBundlesForTests(directory.Path);
        Assert.Empty(Texts.Warnings);
        Assert.Equal(sibling, Texts.Language);
    }

    [Fact]
    public void UserPackFilesCoexistWhenBundlesAreExplicitlyExcludedForIsolation()
    {
        Assert.Equal(new[] { "en", "de" }, Texts.Languages.Select(l => l.Code));
        using TempDirectory directory = Install("es");
        File.Copy(PackPath("fr"), Path.Combine(directory.Path, "fr.json"));
        File.Copy(PackPath("ru"), Path.Combine(directory.Path, "ru.json"));
        Texts.InitializeWithoutBundlesForTests(directory.Path);
        Assert.Empty(Texts.Warnings);
        Assert.Equal(new[] { "en", "de", "es", "fr", "ru" }, Texts.Languages.Select(l => l.Code));
        Texts.SetLanguage("es");
        Assert.NotEqual("Settings", Texts.T("settings.title"));
        Texts.SetLanguage("fr");
        Assert.NotEqual("Settings", Texts.T("settings.title"));
        Texts.SetLanguage("ru");
        Assert.Equal("Настройки", Texts.T("settings.title"));
    }

    private static TempDirectory Install(string code)
    {
        TempDirectory directory = new();
        File.Copy(PackPath(code), Path.Combine(directory.Path, code + ".json"));
        return directory;
    }

    private static string PackPath(string code, [CallerFilePath] string source = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "Desktop", "Localization", "Packs", code + ".json"));
}
