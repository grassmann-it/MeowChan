using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Cross-catalog contracts only: individual dialog and Reader wiring tests remain in their own files.
[Collection("Localization")]
public sealed class LocalSearchCatalogContractTests : IDisposable
{
    private const int MaximumArguments = 16;
    private static readonly object?[] Arguments = Enumerable.Range(0, MaximumArguments)
        .Select(i => (object?)("<synthetic-" + i.ToString(CultureInfo.InvariantCulture) + ">")).ToArray();

    private readonly string _previousLanguage = Texts.Language;

    public LocalSearchCatalogContractTests() => Texts.Initialize();

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    private sealed record LocalSearchCatalog(
        string Prefix,
        (string Key, string English, string German)[] Entries,
        Func<string, object?[], string> Resolve);

    private static readonly LocalSearchCatalog[] Catalogs =
    {
        new("readerFind.", ReaderFindCatalog.Entries,
            (key, args) => Texts.T(key, args)),
        new("filename.search.", FilenameSearchCatalog.Entries,
            (key, args) => FilenameSearchCatalog.T(key, args)),
        new("mediahash.", MediaHashSearchCatalog.Entries,
            (key, args) => MediaHashSearchCatalog.T(key, args)),
    };

    [Fact]
    public void ThreeSearchCatalogsHaveDisjointKeysAndMatchingPlaceholderMultisets()
    {
        Assert.Equal(new[] { 15, 20, 24 }, Catalogs.Select(catalog => catalog.Entries.Length).ToArray());
        var allEntries = Catalogs.SelectMany(catalog => catalog.Entries).ToArray();
        Assert.Equal(59, allEntries.Length);
        Assert.Equal(allEntries.Length, allEntries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count());

        foreach (var catalog in Catalogs)
        {
            Assert.Equal(catalog.Entries.Length,
                catalog.Entries.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count());
            foreach (var entry in catalog.Entries)
            {
                Assert.StartsWith(catalog.Prefix, entry.Key, StringComparison.Ordinal);
                Assert.False(string.IsNullOrWhiteSpace(entry.English));
                Assert.False(string.IsNullOrWhiteSpace(entry.German));
                Assert.Equal(PlaceholderMultiset(entry.English), PlaceholderMultiset(entry.German));
            }
        }
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void SearchCatalogsResolveBuiltinEnglishGermanAndBundledSpanishFrenchRussian(string language)
    {
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        IReadOnlyDictionary<string, string>? bundledTranslations = language is "en" or "de"
            ? null : BundledLocalSearchTranslations(language);

        foreach (var catalog in Catalogs)
        {
            foreach (var entry in catalog.Entries)
            {
                string template;
                if (bundledTranslations != null)
                {
                    Assert.True(bundledTranslations.TryGetValue(entry.Key, out string? bundled));
                    template = bundled!;
                }
                else
                {
                    template = language == "de" ? entry.German : entry.English;
                }
                string expected = string.Format(CultureInfo.GetCultureInfo(language), template, Arguments);
                string actual = catalog.Resolve(entry.Key, Arguments);

                Assert.False(string.IsNullOrWhiteSpace(actual));
                Assert.NotEqual(entry.Key, actual);
                Assert.Equal(expected, actual);
            }
        }
    }

    [Fact]
    public void GlobalRegistrationContainsAllThreeCatalogsWithoutCollision()
    {
        Assert.Equal(BuiltInCatalog.English.Count, BuiltInCatalog.German.Count);
        foreach (var entry in Catalogs.SelectMany(catalog => catalog.Entries))
        {
            Assert.Equal(entry.English, BuiltInCatalog.English[entry.Key]);
            Assert.Equal(entry.German, BuiltInCatalog.German[entry.Key]);
        }
    }

    private static IReadOnlyDictionary<string, string> BundledLocalSearchTranslations(string language)
    {
        string resource = "MeowChan.Desktop.Localization.Packs." + language + ".json";
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)!;
        Assert.NotNull(stream);
        using JsonDocument document = JsonDocument.Parse(stream);
        return document.RootElement.GetProperty("strings").EnumerateObject()
            .Where(property => Catalogs.Any(catalog =>
                property.Name.StartsWith(catalog.Prefix, StringComparison.Ordinal)))
            .ToDictionary(property => property.Name, property => property.Value.GetString()!,
                StringComparer.Ordinal);
    }

    private static string[] PlaceholderMultiset(string format)
    {
        _ = System.Text.CompositeFormat.Parse(format);
        return Regex.Matches(format, @"\{\d+(?:,[^}]*)?(?::[^}]*)?\}")
            .Select(match => match.Value)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
