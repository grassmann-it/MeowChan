using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

[Collection("Localization")]
public sealed class BundledLanguagePackCompletenessTests : IDisposable
{
    public BundledLanguagePackCompletenessTests() => Reset();
    public void Dispose() => Reset();

    private static void Reset()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Fact]
    public void NormalInitializationDiscoversBundledExperimentalPacks()
    {
        Texts.Initialize();

        Assert.Empty(Texts.Warnings);
        string[] codes = Texts.Languages.Select(language => language.Code).ToArray();
        foreach (string code in new[] { "es", "fr", "ru" })
            Assert.Contains(code, codes);
    }

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void BundledPackIsDiscoverableUntestedAndCompleteForNonEmptyEnglishKeys(string code)
    {
        string resourceName = "MeowChan.Desktop.Localization.Packs." + code + ".json";
        string[] manifestResources = typeof(Texts).Assembly.GetManifestResourceNames();
        Assert.Contains(resourceName, manifestResources);

        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resourceName)!;
        Assert.NotNull(stream);
        using JsonDocument document = JsonDocument.Parse(stream);

        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        string nativeName = document.RootElement.GetProperty("nativeName").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(nativeName));
        Assert.EndsWith(" (Untested)", nativeName);

        var translations = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.GetProperty("strings").EnumerateObject())
        {
            Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
            string key = property.Name;
            string value = property.Value.GetString()!;
            Assert.True(translations.TryAdd(key, value), "Duplicate bundled translation key: " + key);
        }

        // Empty English values are intentional sentinel strings and cannot be represented by
        // the pack loader. Every key with an actual English string must be present exactly once.
        IEnumerable<string> expectedKeys = BuiltInCatalog.English
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Key);
        // Languages with more number forms than English add optional "<key>.few" style variants of an English key.
        string[] pluralVariants = translations.Keys.Where(key => !BuiltInCatalog.English.ContainsKey(key)).ToArray();
        Assert.All(pluralVariants, key =>
        {
            int dot = key.LastIndexOf('.');
            Assert.True(dot > 0 && key[(dot + 1)..] is "zero" or "one" or "two" or "few" or "many"
                && BuiltInCatalog.English.ContainsKey(key[..dot]), "Unknown bundled translation key: " + key);
        });
        Assert.Equal(
            expectedKeys.Order(StringComparer.Ordinal),
            translations.Keys.Except(pluralVariants).Order(StringComparer.Ordinal));
        Assert.All(translations.Values, value => Assert.False(string.IsNullOrWhiteSpace(value)));

        Texts.Initialize();
        Assert.Empty(Texts.Warnings);

        LanguageInfo language = Assert.Single(
            Texts.Languages,
            candidate => string.Equals(candidate.Code, code, StringComparison.Ordinal));
        Assert.EndsWith(" (Untested)", language.NativeName);

        Texts.SetLanguage(code);
        Assert.Equal(code, Texts.Language);
        Assert.Empty(Texts.Warnings);
        foreach (var (key, value) in translations)
            Assert.Equal(value, Texts.T(key));
    }
}
