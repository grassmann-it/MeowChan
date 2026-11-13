using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>
/// The "Storage and limits" settings page: the user limits are named for what they limit
/// (not for the plugin-process expander), the first-run text points at the real page title,
/// and the effective parallel limit follows the two drafts live.
/// </summary>
[Collection("Localization")]
public sealed class StorageLimitsSectionTests : IDisposable
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru" };

    public StorageLimitsSectionTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    [Fact]
    public void PageTitleExpanderHeaderAndFirstRunTextAgreeInEveryBuiltInLanguage()
    {
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            string page = Texts.T("settings.storageLimits");
            Assert.NotEqual("settings.storageLimits", page);
            Assert.NotEqual(page, Texts.T("budgets.advanced"));
            Assert.Contains(page, Texts.T("setup.reserve", 3));
            Assert.DoesNotContain("Worker", Texts.T("setup.reserve", 3), StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(Texts.T("budgets.intro")));
        }
        Texts.SetLanguage("de");
        Assert.Equal("Speicher und Grenzen", Texts.T("settings.storageLimits"));
        Assert.Equal("Erweitert: Pluginprozesse", Texts.T("budgets.advanced"));
    }

    [Fact]
    public void MarkupTitlesThePageWithTheLimitsNameAndKeepsTheUserLimitsOutOfTheAdvancedExpander()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == name);
        Assert.Equal("{Binding StorageLimitsLabel}", (string?)Named("SettingsWorkerBudgetsPageTitle").Attribute("Text"));
        XElement expander = Named("SettingsWorkerBudgetsHeading");
        Assert.Equal("{Binding AdvancedBudgetsLabel}", (string?)expander.Attribute("Header"));
        foreach (string name in new[] { "ReserveInput", "BooruCatalogMiBInput" })
        {
            XElement field = Named(name);
            Assert.Contains(field.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsPage");
            Assert.DoesNotContain(field.Ancestors(), e => ReferenceEquals(e, expander));
        }
        Assert.Contains(Named("ProductionWorkersInput").Ancestors(), e => ReferenceEquals(e, expander));
        // The effective line sits on the Downloads page, under both parallel fields.
        XElement line = Named("ParallelEffectiveLine");
        Assert.Contains(line.Ancestors(), e => (string?)e.Attribute(x + "Name") == "SettingsDownloadsPage");
        Assert.Equal("{Binding ParallelEffectiveText}", (string?)line.Attribute("Text"));
    }

    [Fact]
    public void EffectiveParallelLimitIsTheSmallerDraftValueAndEmptyForInvalidDrafts()
    {
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        List<string?> changed = new();
        ((INotifyPropertyChanged)editor).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(string.Empty, editor.ParallelEffectiveText);
        editor.MaxParallelDownloads = 90;
        editor.ParallelDownloadsSafetyLimit = 32;
        Assert.Equal(Texts.T("settings.parallelEffective", 32), editor.ParallelEffectiveText);
        Assert.Contains("32", editor.ParallelEffectiveText);
        editor.MaxParallelDownloads = 8;
        Assert.Equal(Texts.T("settings.parallelEffective", 8), editor.ParallelEffectiveText);
        Assert.Contains(nameof(SettingsEditor.ParallelEffectiveText), changed);

        changed.Clear();
        editor.ParallelDownloadsSafetyLimit = 4;
        Assert.Contains(nameof(SettingsEditor.ParallelEffectiveText), changed);
        Assert.Contains("4", editor.ParallelEffectiveText);

        foreach (decimal? invalid in new decimal?[] { null, 0, 2.5m, -3 })
        {
            editor.MaxParallelDownloads = invalid;
            Assert.Equal(string.Empty, editor.ParallelEffectiveText);
        }
        editor.MaxParallelDownloads = 8;
        editor.ParallelDownloadsSafetyLimit = 257;
        Assert.Equal(string.Empty, editor.ParallelEffectiveText);

        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.DoesNotContain("settings.parallelEffective", Texts.T("settings.parallelEffective", 5));
            Assert.Contains("5", Texts.T("settings.parallelEffective", 5));
        }
    }

    // How each language words "a lower limit deletes nothing"; the page says it once, in its introduction.
    private static readonly (string Language, string Intro, string[] Variants)[] LowerLimitSentence =
    {
        ("en", "Lowering a limit never deletes anything", new[] { "Lowering" }),
        ("de", "Eine kleinere Grenze löscht nichts", new[] { "kleinere Grenze" }),
        ("es", "Reducir un límite nunca borra nada", new[] { "Reducir el límite", "Reducir un límite", "Bajar el límite", "Reducirlo" }),
        ("fr", "Abaisser une limite ne supprime jamais rien", new[] { "Abaisser la limite", "Abaisser une limite", "Réduire une limite" }),
        ("ru", "Уменьшение лимита ничего не удаляет", new[] { "Уменьшение лимита", "Меньший предел" }),
    };

    [Fact]
    public void TheLowerLimitSentenceStandsOnceInTheIntroductionAndInNoHintOfThePage()
    {
        XDocument markup = XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement page = Assert.Single(markup.Descendants(), e => (string?)e.Attribute(x + "Name") == "SettingsWorkerBudgetsPage");
        XElement intro = Assert.Single(page.Descendants(), e => (string?)e.Attribute(x + "Name") == "StorageLimitsIntroText");
        // The introduction comes before the first limit row of the page.
        XElement firstRow = page.Descendants().First(e => (string?)e.Attribute("Classes") == "settings-row");
        Assert.True(intro.IsBefore(firstRow));
        string[] hints = page.Descendants().Select(e => (string?)e.Attribute("Text"))
            .Where(text => text != null && text.StartsWith("{Binding ") && text.EndsWith("Hint}"))
            .Select(text => text!["{Binding ".Length..^1]).Distinct().ToArray();
        Assert.True(hints.Length >= 20, string.Join(", ", hints));
        // The hint properties only look up a text; an uninitialized editor is enough to read them without a window.
        object editor = RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        foreach ((string language, string sentence, string[] variants) in LowerLimitSentence)
        {
            Texts.SetLanguage(language);
            Assert.Contains(sentence, Texts.T("budgets.intro"));
            foreach (string hint in hints)
            {
                string text = (string)typeof(SettingsEditor).GetProperty(hint)!.GetValue(editor)!;
                foreach (string variant in variants)
                    Assert.False(text.Contains(variant, StringComparison.Ordinal), $"{language} {hint} repeats the introduction: {text}");
            }
        }
    }

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
