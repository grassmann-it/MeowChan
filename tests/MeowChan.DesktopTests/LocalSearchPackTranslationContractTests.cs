using System.Globalization;
using System.Text;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Embedded external packs + live service contract, not translation-quality/native UI approval.
[Collection("Localization")]
public sealed class LocalSearchPackTranslationContractTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;
    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;
    private static readonly object?[] Arguments = Enumerable.Range(0, 16)
        .Select(index => (object?)(1234.5m + index)).ToArray();

    public LocalSearchPackTranslationContractTests() => Texts.Initialize(); // Bundles only; no user directory.

    public void Dispose()
    {
        CultureInfo.CurrentCulture = _previousCulture;
        CultureInfo.CurrentUICulture = _previousUiCulture;
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    private static IEnumerable<(string Key, string English, string German)> Entries =>
        FilenameSearchCatalog.Entries.Concat(MediaHashSearchCatalog.Entries);

    [Theory]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void EmbeddedPackContainsEveryActualLocalSearchKeyWithMatchingPlaceholderMultisets(string code)
    {
        Dictionary<string, string> pack = ReadLocalSearchPack(code);
        Assert.Equal(Entries.Select(entry => entry.Key).Order(StringComparer.Ordinal),
            pack.Keys.Order(StringComparer.Ordinal));
        foreach (var entry in Entries)
        {
            string translation = pack[entry.Key];
            Assert.False(string.IsNullOrWhiteSpace(translation));
            Assert.NotEqual(entry.Key, translation);
            AssertNoUnapprovedEnglishCopy(code, entry.Key, entry.English, translation);
            Assert.Equal(PlaceholderMultiset(entry.English), PlaceholderMultiset(translation));
            Assert.Equal(PlaceholderMultiset(entry.German), PlaceholderMultiset(translation));
            Assert.Equal(System.Text.CompositeFormat.Parse(entry.English).MinimumArgumentCount,
                System.Text.CompositeFormat.Parse(translation).MinimumArgumentCount);
        }
    }

    [Fact]
    public void LiveServiceTransitionsUsePackValuesAndSelectedCultureWithoutLocalEnglishFallback()
    {
        var packs = new[] { "es", "fr", "ru" }.ToDictionary(code => code, ReadLocalSearchPack, StringComparer.Ordinal);
        Assert.Empty(Texts.Warnings);
        Texts.SetLanguage("en");
        var notifications = new List<string>();
        EventHandler changed = (_, _) =>
        {
            notifications.Add(Texts.Language);
            AssertResolved(packs[Texts.Language], Texts.Language);
        };
        Texts.Changed += changed;
        try
        {
            // Deliberately different ambient cultures: service formatting must follow Language.
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            foreach (string code in new[] { "es", "fr", "ru", "es" })
            {
                int before = notifications.Count;
                Texts.SetLanguage(code);
                Assert.Equal(before + 1, notifications.Count);
                Assert.Equal(code, Texts.Language);
                Assert.Equal(CultureInfo.GetCultureInfo(code), Texts.Culture);
                AssertResolved(packs[code], code);
                Assert.Equal("en-US", CultureInfo.CurrentCulture.Name);
                Assert.Equal("de-DE", CultureInfo.CurrentUICulture.Name);
                Texts.SetLanguage(code);
                Assert.Equal(before + 1, notifications.Count); // Same language is not a new transition.
            }
            Assert.Equal(new[] { "es", "fr", "ru", "es" }, notifications);
            Assert.Empty(Texts.Warnings);
        }
        finally { Texts.Changed -= changed; }
    }

    private static void AssertResolved(IReadOnlyDictionary<string, string> pack, string code)
    {
        var language = Assert.Single(Texts.Languages, value => value.Code == code);
        Assert.EndsWith(" (Untested)", language.NativeName);
        foreach (var entry in Entries)
        {
            string template = pack[entry.Key];
            Assert.Equal(template, Texts.T(entry.Key)); // Raw resource template, no local adapter fallback.
            string expected = string.Format(CultureInfo.GetCultureInfo(code), template, Arguments);
            Assert.Equal(expected, Texts.T(entry.Key, Arguments));
            string actual = entry.Key.StartsWith("filename.search.", StringComparison.Ordinal)
                ? FilenameSearchCatalog.T(entry.Key, Arguments)
                : MediaHashSearchCatalog.T(entry.Key, Arguments);
            Assert.Equal(expected, actual);
            Assert.NotEqual(entry.Key, actual);
            AssertNoUnapprovedEnglishCopy(code, entry.Key,
                string.Format(CultureInfo.GetCultureInfo(code), entry.English, Arguments), actual);
        }
    }

    private static void AssertNoUnapprovedEnglishCopy(string code, string key, string english, string translation)
    {
        // Exact language/key/value allowance, not a language/key skip: Spanish "no" is also English "no".
        // Resource presence, placeholders and live resource/service/adapter equality still apply to this key.
        if (code == "es" && key == "mediahash.no")
        {
            Assert.Equal("no", english);
            Assert.Equal("no", translation);
        }
        else Assert.NotEqual(english, translation);
    }

    private static Dictionary<string, string> ReadLocalSearchPack(string code)
    {
        string resource = "MeowChan.Desktop.Localization.Packs." + code + ".json";
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException("Required embedded pack missing: " + resource);
        using JsonDocument document = JsonDocument.Parse(stream);
        Assert.Equal(code, document.RootElement.GetProperty("code").GetString());
        Assert.EndsWith(" (Untested)", document.RootElement.GetProperty("nativeName").GetString()!);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.GetProperty("strings").EnumerateObject())
        {
            if (!property.Name.StartsWith("filename.search.", StringComparison.Ordinal)
                && !property.Name.StartsWith("mediahash.", StringComparison.Ordinal)) continue;
            Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
            Assert.True(values.TryAdd(property.Name, property.Value.GetString()!),
                "Duplicate local-search pack key: " + property.Name);
        }
        // Callers assert exact key sets: missing and obsolete/extra prefix keys must not pass.
        return values;
    }

    private static string[] PlaceholderMultiset(string format)
    {
        _ = System.Text.CompositeFormat.Parse(format); // Validate syntax before scanning real format items.
        var items = new List<string>();
        for (int index = 0; index < format.Length; index++)
        {
            if (index + 1 < format.Length && ((format[index] == '{' && format[index + 1] == '{')
                || (format[index] == '}' && format[index + 1] == '}'))) { index++; continue; }
            if (format[index] != '{') continue;
            var item = new StringBuilder();
            do { item.Append(format[index++]); } while (index < format.Length && format[index] != '}');
            item.Append('}');
            items.Add(item.ToString()); // Includes index, alignment and format; duplicates remain duplicates.
        }
        return items.Order(StringComparer.Ordinal).ToArray();
    }
}
