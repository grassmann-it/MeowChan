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

    // The packs this working copy must ship. A pack that a later branch adds is covered
    // by the discovery-driven theories below as soon as its file exists; add it here when it is merged.
    private static readonly string[] RequiredCodes = { "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };

    // File name (= code) and chooser name of every bundled preview, including the packs written on other branches.
    // Packs/README.md lists the same table; a pack whose code is here must carry exactly this name.
    internal static readonly IReadOnlyDictionary<string, string> ExpectedNativeNames = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["es"] = "Español (Untested)", ["fr"] = "Français (Untested)", ["ru"] = "Русский (Untested)",
        ["zh-Hans"] = "简体中文 (Untested)", ["ja"] = "日本語 (Untested)", ["ko"] = "한국어 (Untested)",
        ["pt-BR"] = "Português (Brasil) (Untested)", ["cs"] = "Čeština (Untested)", ["it"] = "Italiano (Untested)",
        ["pl"] = "Polski (Untested)"
    };

    [Fact]
    public void EveryBundledPackIsAnExpectedPreviewAndTheReadmeNamesAllOfThem()
    {
        Assert.All(Texts.BundledPackCodes(), code => Assert.True(ExpectedNativeNames.ContainsKey(code), "Unplanned bundled pack: " + code));
        string readme = File.ReadAllText(PacksReadme());
        foreach ((string code, string name) in ExpectedNativeNames)
        {
            Assert.Contains("`" + code + ".json`", readme);
            Assert.Contains(name, readme);
            Assert.True(Texts.IsValidLanguageCode(code));
            Assert.Equal(code, System.Globalization.CultureInfo.GetCultureInfo(code).Name);
        }
    }

    /// <summary>Every embedded pack, discovered exactly as the loader discovers it (file name = language code).</summary>
    public static TheoryData<string> BundledCodes()
    {
        TheoryData<string> data = new();
        foreach (string code in Texts.BundledPackCodes()) data.Add(code);
        return data;
    }

    [Fact]
    public void NormalInitializationDiscoversBundledExperimentalPacks()
    {
        Texts.Initialize();

        Assert.Empty(Texts.Warnings);
        string[] codes = Texts.Languages.Select(language => language.Code).ToArray();
        foreach (string code in RequiredCodes)
        {
            Assert.Contains(code, Texts.BundledPackCodes());
            Assert.Contains(code, codes);
        }
        // Discovery is by file name: a code is a valid culture name and appears exactly once.
        Assert.Equal(Texts.BundledPackCodes().Distinct(StringComparer.OrdinalIgnoreCase).Count(), Texts.BundledPackCodes().Count);
        Assert.All(Texts.BundledPackCodes(), code => Assert.Equal(code, System.Globalization.CultureInfo.GetCultureInfo(code).Name));
    }

    private static string PacksReadme([System.Runtime.CompilerServices.CallerFilePath] string sourcePath = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../Desktop/Localization/Packs/README.md"));

    [Theory]
    [MemberData(nameof(BundledCodes))]
    public void BundledPackIsDiscoverableUntestedAndCompleteForNonEmptyEnglishKeys(string code)
    {
        string resourceName = Texts.BundledPackResourcePrefix + code + ".json";
        string[] manifestResources = typeof(Texts).Assembly.GetManifestResourceNames();
        Assert.Contains(resourceName, manifestResources);

        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resourceName)!;
        Assert.NotNull(stream);
        using JsonDocument document = JsonDocument.Parse(stream);

        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        string nativeName = document.RootElement.GetProperty("nativeName").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(nativeName));
        Assert.EndsWith(" (Untested)", nativeName);
        Assert.Equal(ExpectedNativeNames[code], nativeName);

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
        // A variant must be a category the language's number rules can actually select, so a stray
        // Russian-style ".few" in a Spanish pack or any variant in a Chinese pack is an error.
        HashSet<string> categories = new(Enumerable.Range(0, 1000).Select(count => PluralRules.Category(code, count)), StringComparer.Ordinal);
        // English "<key>.one" texts are required only where the language can select "one": zh/ja/ko never do and
        // use the base text for every count, so their packs omit those keys.
        IEnumerable<string> expectedKeys = BuiltInCatalog.English
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Key)
            .Where(key => PluralRules.BaseKey(key) is not { } baseKey || !BuiltInCatalog.English.ContainsKey(baseKey)
                || categories.Contains(key[(key.LastIndexOf('.') + 1)..]));
        // Languages with more number forms than English add optional "<key>.few" style variants of an English key.
        string[] pluralVariants = translations.Keys.Where(key => !BuiltInCatalog.English.ContainsKey(key)).ToArray();
        Assert.All(pluralVariants, key =>
        {
            int dot = key.LastIndexOf('.');
            Assert.True(dot > 0 && key[(dot + 1)..] is "zero" or "one" or "two" or "few" or "many"
                && BuiltInCatalog.English.ContainsKey(key[..dot]), "Unknown bundled translation key: " + key);
            Assert.True(categories.Contains(key[(dot + 1)..]), code + " cannot select the plural variant " + key);
        });
        if (PluralRules.Category(code, 1) == PluralRules.Other)
            Assert.Empty(translations.Keys.Where(key => PluralRules.BaseKey(key) != null)); // zh/ja: no .one keys at all
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
