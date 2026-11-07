using System.Globalization;
using System.Text.Json;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Catalog-only coverage; actual context-menu integration belongs to the UI owner.
[Collection("Localization")]
public sealed class ThreadPriorityMenuLocalizationTests : IDisposable
{
    private static readonly (string Code, string High, string Normal)[] Labels =
    [
        ("en", "High priority ({0})", "Normal priority ({0})"),
        ("de", "Hohe Priorität ({0})", "Normale Priorität ({0})"),
        ("es", "Prioridad alta ({0})", "Prioridad normal ({0})"),
        ("fr", "Priorité haute ({0})", "Priorité normale ({0})"),
        ("ru", "Высокий приоритет ({0})", "Обычный приоритет ({0})")
    ];

    private readonly string _previousLanguage = Texts.Language;

    public ThreadPriorityMenuLocalizationTests()
    {
        // Load the real embedded resources, never a personal/profile directory.
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    public static IEnumerable<object[]> LabelCases()
    {
        foreach (var label in Labels)
        foreach (int count in new[] { 0, 1, 2, 1234, int.MaxValue })
        {
            yield return [label.Code, "priority.menuHigh", label.High, count];
            yield return [label.Code, "priority.menuNormal", label.Normal, count];
        }
    }

    [Theory]
    [MemberData(nameof(LabelCases))]
    public void RealCatalogAndFormatterPreserveExactLabelAndAffectedCount(
        string language, string key, string template, int count)
    {
        // Reading the source catalog as well as T prevents English fallback from
        // concealing a missing bundled key or a rejected embedded resource.
        Assert.Equal(template, ReadCatalogValue(language, key));
        Texts.SetLanguage(language);
        Assert.Equal(language, Texts.Language);
        Assert.Empty(Texts.Warnings);
        Assert.Equal(template, Texts.T(key));
        string expected = template.Replace("{0}", count.ToString(CultureInfo.GetCultureInfo(language)));
        Assert.Equal(expected, Texts.T(key, count));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("fr")]
    [InlineData("ru")]
    public void LanguageChangeNotificationsExposeFreshLabelsAndCountsThenReturnToStartingLanguage(
        string startingLanguage)
    {
        Texts.SetLanguage(startingLanguage);
        int affectedCount = 1;
        var observed = new List<(string Language, string High, string Normal)>();
        EventHandler changed = (_, _) => observed.Add((Texts.Language,
            Texts.T("priority.menuHigh", affectedCount),
            Texts.T("priority.menuNormal", affectedCount)));
        Texts.Changed += changed;
        try
        {
            var start = Labels.Single(label => label.Code == startingLanguage);
            foreach (var target in Labels.Where(label => label.Code != startingLanguage).Append(start))
            {
                affectedCount += 7;
                int before = observed.Count;
                Texts.SetLanguage(target.Code);
                Assert.Equal(before + 1, observed.Count);
                string count = affectedCount.ToString(CultureInfo.GetCultureInfo(target.Code));
                Assert.Equal((target.Code, target.High.Replace("{0}", count),
                    target.Normal.Replace("{0}", count)), observed[^1]);
                Assert.Equal(target.Code, Texts.Language);
                Texts.SetLanguage(target.Code);
                Assert.Equal(before + 1, observed.Count); // No spurious refresh for the same language.
            }
            Assert.Equal(startingLanguage, Texts.Language);
            Assert.Empty(Texts.Warnings);
        }
        finally
        {
            Texts.Changed -= changed;
        }
    }

    [Theory]
    [InlineData("es", "Español (Untested)")]
    [InlineData("fr", "Français (Untested)")]
    [InlineData("ru", "Русский (Untested)")]
    public void BundledPreviewsRetainUntestedNames(string language, string expectedName)
    {
        Assert.Equal(expectedName, Assert.Single(Texts.Languages, item => item.Code == language).NativeName);
        Assert.Empty(Texts.Warnings);
    }

    private static string ReadCatalogValue(string language, string key)
    {
        if (language == "en") return BuiltInCatalog.English[key];
        if (language == "de") return BuiltInCatalog.German[key];
        using Stream stream = typeof(Texts).Assembly.GetManifestResourceStream(
            "MeowChan.Desktop.Localization.Packs." + language + ".json")!;
        Assert.NotNull(stream);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonProperty entry = Assert.Single(document.RootElement.GetProperty("strings")
            .EnumerateObject().Where(property => property.Name == key));
        return entry.Value.GetString()!;
    }
}
