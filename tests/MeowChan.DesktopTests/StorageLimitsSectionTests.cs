using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;
using MeowChan.Downloader;

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
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

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
            Assert.Equal(language, Texts.Language);
            string page = Texts.T("settings.storageLimits");
            Assert.NotEqual("settings.storageLimits", page);
            Assert.NotEqual("budgets.advanced", Texts.T("budgets.advanced"));
            Assert.NotEqual(page, Texts.T("budgets.advanced"));
            Assert.Contains(page, Texts.T("setup.reserve", 3));
            Assert.DoesNotContain("Worker", Texts.T("setup.reserve", 3), StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(Texts.Own("budgets.intro")));
        }
        Texts.SetLanguage("de");
        Assert.Equal("Speicher und Grenzen", Texts.T("settings.storageLimits"));
        Assert.Equal("Erweitert: Pluginprozesse", Texts.T("budgets.advanced"));
    }

    [Fact]
    public void MarkupTitlesThePageWithTheLimitsNameAndKeepsTheUserLimitsOutOfTheAdvancedExpander()
    {
        XDocument markup = LoadMarkup();
        XElement page = Named(markup, "SettingsWorkerBudgetsPage");
        Assert.Equal("{Binding StorageLimitsLabel}", (string?)Named(markup, "SettingsWorkerBudgetsPageTitle").Attribute("Text"));
        XElement expander = Named(markup, "SettingsWorkerBudgetsHeading");
        Assert.Equal("Expander", expander.Name.LocalName);
        Assert.Equal("{Binding AdvancedBudgetsLabel}", (string?)expander.Attribute("Header"));
        XElement limitsGroup = Named(markup, "SettingsLimitsGroup");
        Assert.Contains(page, limitsGroup.Ancestors());
        Assert.DoesNotContain(expander, limitsGroup.Ancestors());

        // Pin the plugin boundary, not the number or wording of user-limit rows.
        string[] pluginInputs = { "ProductionWorkersInput", "WorkerIdleMinutesInput", "SourceHeapMiBInput", "FilterHeapMiBInput" };
        Assert.Contains(page, expander.Ancestors());
        Assert.Equal(pluginInputs.Order(StringComparer.Ordinal), expander.Descendants()
            .Where(e => e.Name.LocalName == "SettingsNumberInput")
            .Select(e => (string)e.Attribute(X + "Name")!).Order(StringComparer.Ordinal));
        foreach (string name in new[]
        {
            "ReserveInput", "BooruCatalogMiBInput", "FavoriteEntryLimitInput", "PostBookmarkLimitInput",
            "PostFilterRuleLimitInput", "HiddenPostLimitInput", "TagQueueLimitInput", "FolderSourceMaxFilesInput",
            "ArchiveTagFilterTagsPerListInput", "UrlTextExportLimitMiBInput", "SlashTagSuggestionCountInput",
            "VideoScrubCacheLimitMiBInput", "QuickTagSuggestionCountInput", "LocalTaggerManifestKiBInput",
            "LocalTaggerWeightsMiBInput", "LocalTaggerInputTensorMiBInput"
        })
        {
            XElement field = Named(markup, name);
            Assert.Contains(name == "ReserveInput" ? page : limitsGroup, field.Ancestors());
            Assert.DoesNotContain(expander, field.Ancestors());
        }

        // The effective line stays outside Downloads' optional group; the cap may be inside it.
        XElement downloads = Named(markup, "SettingsDownloadsPage");
        XElement line = Named(markup, "ParallelEffectiveLine");
        Assert.Contains(downloads, line.Ancestors());
        Assert.DoesNotContain(line.Ancestors(), e => e.Name.LocalName == "Expander");
        Assert.Equal("{Binding ParallelEffectiveText}", (string?)line.Attribute("Text"));
        foreach ((string name, string property) in new[]
        {
            ("ParallelDownloadsInput", nameof(SettingsEditor.MaxParallelDownloads)),
            ("ParallelDownloadsSafetyInput", nameof(SettingsEditor.ParallelDownloadsSafetyLimit))
        })
        {
            XElement field = Named(markup, name);
            Assert.Contains(downloads, field.Ancestors());
            Assert.Equal($"{{Binding {property}, Mode=TwoWay}}", (string?)field.Attribute("Value"));
        }
    }

    [Fact]
    public void EffectiveParallelLimitFollowsBothDraftsAndNotifiesOnEachChange()
    {
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        List<string?> changed = new();
        ((INotifyPropertyChanged)editor).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.Equal(string.Empty, editor.ParallelEffectiveText);
        editor.MaxParallelDownloads = 90;
        editor.ParallelDownloadsSafetyLimit = 32;
        Assert.Equal(Texts.Count("settings.parallelEffective", 32), editor.ParallelEffectiveText);
        Assert.Contains("32", editor.ParallelEffectiveText);
        changed.Clear();
        editor.MaxParallelDownloads = 8;
        Assert.Equal(Texts.Count("settings.parallelEffective", 8), editor.ParallelEffectiveText);
        Assert.Contains(nameof(SettingsEditor.ParallelEffectiveText), changed);

        changed.Clear();
        editor.ParallelDownloadsSafetyLimit = 4;
        Assert.Contains(nameof(SettingsEditor.ParallelEffectiveText), changed);
        Assert.Equal(Texts.Count("settings.parallelEffective", 4), editor.ParallelEffectiveText);

        // Raising either draft is live too, including when the other draft becomes the limiting value.
        changed.Clear();
        editor.ParallelDownloadsSafetyLimit = 64;
        Assert.Contains(nameof(SettingsEditor.ParallelEffectiveText), changed);
        Assert.Equal(Texts.Count("settings.parallelEffective", 8), editor.ParallelEffectiveText);
        changed.Clear();
        editor.MaxParallelDownloads = 90;
        Assert.Contains(nameof(SettingsEditor.ParallelEffectiveText), changed);
        Assert.Equal(Texts.Count("settings.parallelEffective", 64), editor.ParallelEffectiveText);

        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            Assert.Equal(language, Texts.Language);
            Assert.DoesNotContain("settings.parallelEffective", editor.ParallelEffectiveText);
            Assert.Equal(Texts.Count("settings.parallelEffective", 64), editor.ParallelEffectiveText);
            Assert.Contains("64", editor.ParallelEffectiveText);
        }
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(1, DownloadQueue.MaximumParallelDownloadsSafetyLimit, 1)]
    [InlineData(Config.MaxParallelDownloadsMaximum, 1, 1)]
    [InlineData(Config.MaxParallelDownloadsMaximum, DownloadQueue.MaximumParallelDownloadsSafetyLimit, Config.MaxParallelDownloadsMaximum)]
    [InlineData(8, 8, 8)]
    public void EffectiveParallelLimitAcceptsInclusiveBoundsAndEqualDrafts(int requested, int cap, int expected)
    {
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        editor.MaxParallelDownloads = requested;
        editor.ParallelDownloadsSafetyLimit = cap;
        Assert.Equal(Texts.Count("settings.parallelEffective", expected), editor.ParallelEffectiveText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EffectiveParallelLimitRejectsInvalidDraftOnEitherSideAndRecovers(bool invalidSafetyLimit)
    {
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        List<string?> changed = new();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        decimal maximum = invalidSafetyLimit ? DownloadQueue.MaximumParallelDownloadsSafetyLimit : Config.MaxParallelDownloadsMaximum;
        foreach (decimal? invalid in new decimal?[] { null, 0, -3, 2.5m, maximum + 1, decimal.MinValue, decimal.MaxValue })
        {
            editor.MaxParallelDownloads = 8;
            editor.ParallelDownloadsSafetyLimit = 4;
            changed.Clear();
            if (invalidSafetyLimit) editor.ParallelDownloadsSafetyLimit = invalid;
            else editor.MaxParallelDownloads = invalid;
            Assert.Contains(nameof(SettingsEditor.ParallelEffectiveText), changed);
            Assert.Equal(string.Empty, editor.ParallelEffectiveText);

            changed.Clear();
            if (invalidSafetyLimit) editor.ParallelDownloadsSafetyLimit = 4;
            else editor.MaxParallelDownloads = 8;
            Assert.Contains(nameof(SettingsEditor.ParallelEffectiveText), changed);
            Assert.Equal(Texts.Count("settings.parallelEffective", 4), editor.ParallelEffectiveText);
        }
    }

    // The general non-deletion promise belongs in one introduction. Field-specific preservation
    // hints (Reader P1/Wave4) may explain what lowering that particular limit does.
    private static readonly (string Language, string Sentence)[] LowerLimitSentence =
    {
        ("en", "Lowering a limit never deletes anything"),
        ("de", "Eine kleinere Grenze löscht nichts"),
        ("es", "Reducir un límite nunca borra nada"),
        ("fr", "Abaisser une limite ne supprime jamais rien"),
        ("ru", "Уменьшение лимита ничего не удаляет"),
    };

    [Fact]
    public void PageHasOneIntroductionWithTheNonDeletionPromiseBeforeItsLimits()
    {
        XDocument markup = LoadMarkup();
        XElement page = Named(markup, "SettingsWorkerBudgetsPage");
        XElement intro = Named(markup, "StorageLimitsIntroText");
        Assert.Same(intro, Assert.Single(markup.Descendants(), e => (string?)e.Attribute("Text") == "{Binding StorageLimitsIntro}"));
        Assert.Contains(page, intro.Ancestors());
        Assert.DoesNotContain(intro.Ancestors(), e => e.Name.LocalName == "Expander");

        string[] hints = page.Descendants().Select(e => (string?)e.Attribute("Text"))
            .Where(text => text != null && text.StartsWith("{Binding ") && text.EndsWith("Hint}"))
            .Select(text => text!["{Binding ".Length..^1]).Distinct().ToArray();
        XElement pluginExpander = Named(markup, "SettingsWorkerBudgetsHeading");
        XElement taggerLimits = Named(markup, "SettingsLocalTaggerLimits");
        XElement taggerRestartHint = Assert.Single(taggerLimits.Elements(),
            e => (string?)e.Attribute("Text") == "{Binding BudgetRestartHint}");
        foreach (XElement field in page.Descendants().Where(e => e.Name.LocalName == "SettingsNumberInput"
            && !e.Ancestors().Contains(pluginExpander)))
        {
            string name = (string)field.Attribute(X + "Name")!;
            Assert.True(intro.IsBefore(field), $"{name} precedes the introduction");
            if (field.Ancestors().Contains(taggerLimits))
                Assert.True(taggerRestartHint.IsBefore(field), $"{name} precedes the Local Tagger restart hint");
            else
                Assert.Contains(name[..^"Input".Length] + "Hint", hints);
        }

        // These getters only look up texts and constants; no window or profile is needed.
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        foreach ((string language, string sentence) in LowerLimitSentence)
        {
            Texts.SetLanguage(language);
            Assert.Equal(language, Texts.Language);
            Assert.Equal(Texts.T("budgets.intro"), editor.StorageLimitsIntro);
            Assert.Contains(sentence, editor.StorageLimitsIntro);
            Assert.Equal(1, editor.StorageLimitsIntro.Split(sentence, StringSplitOptions.None).Length - 1);
            foreach (string hint in hints)
            {
                var property = typeof(SettingsEditor).GetProperty(hint);
                Assert.NotNull(property);
                string text = Assert.IsType<string>(property.GetValue(editor));
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language} {hint} is empty");
                Assert.DoesNotContain(sentence, text, StringComparison.Ordinal);
                Assert.DoesNotContain(editor.StorageLimitsIntro, text, StringComparison.Ordinal);
            }
        }
    }

    private static XDocument LoadMarkup() =>
        XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));

    private static XElement Named(XContainer markup, string name) =>
        Assert.Single(markup.Descendants(), e => (string?)e.Attribute(X + "Name") == name);

    private static string RepositoryRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
}
