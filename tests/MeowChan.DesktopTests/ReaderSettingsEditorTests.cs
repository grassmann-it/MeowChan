using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

/// <summary>Markup, bounds, choices and language-refresh coverage for the Reader P1 editor.</summary>
[Collection("Localization")]
public sealed class ReaderSettingsEditorTests : IDisposable
{
    private static readonly string[] Languages = { "en", "de", "es", "fr", "ru", "cs", "it", "ja", "ko", "pl", "pt-BR", "zh-Hans" };
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace A = "https://github.com/avaloniaui";

    private static readonly (string Property, string LabelKey, string Minimum, string Maximum, string Page)[] Numbers =
    {
        (nameof(Config.ReaderThumbnailWidth), "settings.reader.thumbnailWidth", nameof(SettingsEditor.ReaderThumbnailWidthMinimum), nameof(SettingsEditor.ReaderThumbnailWidthMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderFoldLines), "settings.reader.foldLines", nameof(SettingsEditor.ReaderFoldLinesMinimum), nameof(SettingsEditor.ReaderFoldLinesMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderPreviewLines), "settings.reader.previewLines", nameof(SettingsEditor.ReaderPreviewLinesMinimum), nameof(SettingsEditor.ReaderPreviewLinesMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderPreviewDelayMs), "settings.reader.previewDelay", nameof(SettingsEditor.ReaderPreviewDelayMsMinimum), nameof(SettingsEditor.ReaderPreviewDelayMsMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderReplyStep), "settings.reader.replyStep", nameof(SettingsEditor.ReaderReplyStepMinimum), nameof(SettingsEditor.ReaderReplyStepMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderReplyLines), "settings.reader.replyLines", nameof(SettingsEditor.ReaderReplyLinesMinimum), nameof(SettingsEditor.ReaderReplyLinesMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderPageSize), "settings.reader.pageSize", nameof(SettingsEditor.ReaderPageSizeMinimum), nameof(SettingsEditor.ReaderPageSizeMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderThreadingDepth), "settings.reader.threadingDepth", nameof(SettingsEditor.ReaderThreadingDepthMinimum), nameof(SettingsEditor.ReaderThreadingDepthMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.PostFilterRuleLimit), "settings.limits.postFilterRules", nameof(SettingsEditor.PostFilterRuleLimitMinimum), nameof(SettingsEditor.PostFilterRuleLimitMaximum), "SettingsWorkerBudgetsPage"),
        (nameof(Config.HiddenPostLimit), "settings.limits.hiddenPosts", nameof(SettingsEditor.HiddenPostLimitMinimum), nameof(SettingsEditor.HiddenPostLimitMaximum), "SettingsWorkerBudgetsPage"),
    };

    private static readonly (string Name, string Minimum, string Maximum, string Key)[] ChoiceAndGalleryNumbers =
    {
        ("GalleryFitMaxUpscalePercentInput", nameof(SettingsEditor.GalleryFitMaxUpscalePercentMinimum), nameof(SettingsEditor.GalleryFitMaxUpscalePercentMaximum), "settings.gallery.fitUpscalePercent"),
    };

    public ReaderSettingsEditorTests() { Texts.Initialize(); Texts.SetLanguage("en"); }
    public void Dispose() { Texts.Initialize(); Texts.SetLanguage("en"); }

    [Fact]
    public void EveryNumericReaderFieldUsesComponentBoundRangesAndHasAccessibleMarkup()
    {
        XDocument markup = Markup();
        foreach ((string property, string key, string minimum, string maximum, string pageName) in Numbers)
            AssertNumeric(markup, property + "Input", property, key, minimum, maximum, pageName);
        foreach ((string name, string minimum, string maximum, string key) in ChoiceAndGalleryNumbers)
            AssertNumeric(markup, name, "GalleryFitMaxUpscalePercent", key, minimum, maximum, "SettingsReadingMediaPage");

        XElement expander = Named(markup, "ReaderLongPostsSettings");
        Assert.Equal("False", (string?)expander.Attribute("IsExpanded"));
        Assert.Equal("{Binding ReaderLongPostsTitle}", (string?)expander.Attribute("Header"));
        Assert.Equal("{Binding IsGalleryFitLimited}", (string?)Named(markup, "GalleryFitMaxUpscalePercentInput")
            .Ancestors().Select(ancestor => ancestor.Attribute("IsVisible")).First(attribute => attribute != null));
        Assert.Equal("{Binding IsGalleryFitLimited}", (string?)Named(markup, "GalleryFitMaxUpscalePercentInput").Attribute("IsEnabled"));
    }

    [Fact]
    public void DefaultsAndComponentBoundsAreExposedWithoutASecondLimitTable()
    {
        Config defaults = new();
        SettingsEditor editor = CreateEditor();
        foreach ((string property, _, string minimumName, string maximumName, _) in Numbers)
        {
            PropertyInfo configProperty = typeof(Config).GetProperty(property)!;
            PropertyInfo minimum = typeof(SettingsEditor).GetProperty(minimumName)!;
            PropertyInfo maximum = typeof(SettingsEditor).GetProperty(maximumName)!;
            FieldInfo configMinimum = typeof(Config).GetField(property + "Minimum", BindingFlags.NonPublic | BindingFlags.Static)!;
            FieldInfo configMaximum = typeof(Config).GetField(property + "Maximum", BindingFlags.NonPublic | BindingFlags.Static)!;
            Assert.InRange((int)configProperty.GetValue(defaults)!, (int)minimum.GetValue(editor)!, (int)maximum.GetValue(editor)!);
            Assert.Equal(configMinimum.GetRawConstantValue(), minimum.GetValue(editor));
            Assert.Equal(configMaximum.GetRawConstantValue(), maximum.GetValue(editor));
        }
        Assert.Equal(GalleryFitPreferences.MaxUpscalePercentMinimum, typeof(SettingsEditor).GetProperty(nameof(SettingsEditor.GalleryFitMaxUpscalePercentMinimum))!.GetValue(editor));
        Assert.Equal(GalleryFitPreferences.MaxUpscalePercentMaximum, typeof(SettingsEditor).GetProperty(nameof(SettingsEditor.GalleryFitMaxUpscalePercentMaximum))!.GetValue(editor));
    }

    [Fact]
    public void ReaderChoicesHaveStableSemanticValuesAndLanguageRefreshPreservesDrafts()
    {
        SettingsEditor editor = CreateEditor();
        Invoke(editor, "RefreshReaderChoices", "SinceOp", "Hover", "Limited");
        Assert.Equal(new[] { "System", "Long", "SinceOp", "RelativeToNow" }, editor.ReaderDateStyleChoices.Select(item => item.Value));
        Assert.Equal(new[] { "Hide", "Hover", "Always" }, editor.ReaderSpoilersChoices.Select(item => item.Value));
        Assert.Equal(new[] { "Unlimited", "Limited" }, editor.GalleryFitUpscaleChoices.Select(item => item.Value));
        Assert.Equal("SinceOp", editor.SelectedReaderDateStyle!.Value);
        Assert.Equal("Hover", editor.SelectedReaderSpoilers!.Value);
        Assert.True(editor.IsGalleryFitLimited);

        Texts.SetLanguage("de");
        Invoke(editor, "RefreshReaderChoiceLabels");
        Assert.Equal("SinceOp", editor.SelectedReaderDateStyle!.Value);
        Assert.Equal("Hover", editor.SelectedReaderSpoilers!.Value);
        Assert.Equal("Limited", editor.SelectedGalleryFitUpscale!.Value);
        Assert.Equal(Texts.T("settings.reader.dateStyle.sinceOp"), editor.SelectedReaderDateStyle.Label);
        Assert.Equal(Texts.T("settings.reader.spoilers.hover"), editor.SelectedReaderSpoilers.Label);
        Assert.Equal(Texts.T("settings.gallery.fitUpscale.limited"), editor.SelectedGalleryFitUpscale.Label);
    }

    [Theory]
    [MemberData(nameof(ReaderSettingsPersistenceTests.Numbers), MemberType = typeof(ReaderSettingsPersistenceTests))]
    public void ApplyReaderDraftUsesInclusiveBoundsAndRejectsUnavailableOrFractionalNumbers(string name, int fallback, int min, int max)
    {
        SettingsEditor editor = CreateValidDraft();
        var draft = typeof(SettingsEditor).GetProperty(name)!;
        var persisted = typeof(Config).GetProperty(name)!;
        Assert.Equal((decimal)fallback, draft.GetValue(editor));
        foreach (int number in new[] { min, max })
        {
            draft.SetValue(editor, (decimal?)number);
            Config candidate = new();
            Invoke(editor, "ApplyReaderDraft", candidate);
            Assert.Equal(number, persisted.GetValue(candidate));
        }
        foreach (decimal? number in new decimal?[] { null, min - 1, max + 1, min + 0.5m })
        {
            draft.SetValue(editor, number);
            var error = Assert.Throws<TargetInvocationException>(() => Invoke(editor, "ApplyReaderDraft", new Config()));
            Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
        }
    }

    [Theory]
    [InlineData(100d, 100)]
    [InlineData(725d, 725)]
    [InlineData(1600d, 1600)]
    [InlineData(null, 200)]
    [InlineData(99d, 200)]
    [InlineData(1601d, 200)]
    [InlineData(200.5, 200)]
    public void UnlimitedGalleryPreservesValidHiddenCapAndIgnoresInvalidHiddenDraft(double? draft, int expected)
    {
        SettingsEditor editor = CreateValidDraft();
        editor.SelectedGalleryFitUpscale = editor.GalleryFitUpscaleChoices.Single(item => item.Value == "Unlimited");
        editor.GalleryFitMaxUpscalePercent = draft.HasValue ? (decimal)draft.Value : null;
        Config candidate = new();
        Invoke(editor, "ApplyReaderDraft", candidate);
        Assert.False(candidate.GalleryFitUpscaleLimited);
        Assert.Equal(expected, candidate.GalleryFitMaxUpscalePercent);
        candidate.Validate();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PassiveLanguageRefreshDoesNotInvalidatePendingWorkOrHideTheGalleryCap(bool simulateBindingClears)
    {
        SettingsEditor editor = CreateValidDraft();
        editor.ReaderThumbnailWidth = 370;
        using var wait = new CancellationTokenSource();
        Set(editor, "_destinationWait", wait);
        Set(editor, "_draftRevision", 47L);
        List<bool> visibility = new();
        editor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SettingsEditor.IsGalleryFitLimited)) visibility.Add(editor.IsGalleryFitLimited);
            if (!simulateBindingClears) return;
            switch (e.PropertyName)
            {
                case nameof(SettingsEditor.ReaderDateStyleChoices): editor.SelectedReaderDateStyle = null; break;
                case nameof(SettingsEditor.ReaderSpoilersChoices): editor.SelectedReaderSpoilers = null; break;
                case nameof(SettingsEditor.GalleryFitUpscaleChoices): editor.SelectedGalleryFitUpscale = null; break;
            }
        };
        foreach (string language in new[] { "de", "en", "en", "fr" })
        {
            Texts.SetLanguage(language);
            visibility.Clear();
            Invoke(editor, "RefreshChoiceLabels");
            Assert.Equal(new[] { true }, visibility);
            Assert.Equal(47L, Get<long>(editor, "_draftRevision"));
            Assert.False(wait.IsCancellationRequested);
            Assert.False(Get<bool>(editor, "_refreshingLanguageLabels"));
            Assert.Equal(370m, editor.ReaderThumbnailWidth);
            Assert.Equal("SinceOp", editor.SelectedReaderDateStyle!.Value);
            Assert.Equal("Hover", editor.SelectedReaderSpoilers!.Value);
            Assert.Equal(Texts.T("settings.reader.dateStyle.sinceOp"), editor.SelectedReaderDateStyle.Label);
        }
        editor.SelectedReaderSpoilers = editor.ReaderSpoilersChoices.Single(item => item.Value == "Always");
        Assert.Equal(48L, Get<long>(editor, "_draftRevision"));
        Assert.True(wait.IsCancellationRequested);
    }

    private static SettingsEditor CreateValidDraft()
    {
        SettingsEditor editor = CreateEditor();
        Config defaults = new();
        foreach (object[] row in ReaderSettingsPersistenceTests.Numbers())
        {
            string name = (string)row[0];
            typeof(SettingsEditor).GetProperty(name)!.SetValue(editor, (decimal?)(int)typeof(Config).GetProperty(name)!.GetValue(defaults)!);
        }
        Invoke(editor, "RefreshReaderChoices", "SinceOp", "Hover", "Limited");
        return editor;
    }

    [Fact]
    public void AllReaderKeysAreUniqueAndTranslatedInEveryBundledPack()
    {
        string[] keys = ReaderSettingsCatalog.Entries.Select(entry => entry.Key).ToArray();
        Assert.Equal(keys.Length, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(ReaderSettingsCatalog.Entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.English));
            Assert.False(string.IsNullOrWhiteSpace(entry.German));
            Assert.NotEqual(entry.Key, entry.English);
        });
        foreach (string language in Languages)
        {
            Texts.SetLanguage(language);
            foreach (string key in keys)
            {
                string value = Texts.T(key, 1, 2);
                Assert.NotEqual(key, value);
                Assert.DoesNotContain("{0}", value);
                Assert.DoesNotContain("{1}", value);
            }
        }
        foreach (string pack in new[] { "cs", "es", "fr", "it", "ja", "ko", "pl", "pt-BR", "ru", "zh-Hans" })
        {
            string text = File.ReadAllText(PackPath(pack));
            foreach (string key in keys)
                Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(text, "\\\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\\\":").Count);
        }
    }

    private static void AssertNumeric(XDocument markup, string name, string property, string key, string minimum, string maximum, string pageName)
    {
        XElement input = Named(markup, name);
        XElement page = Named(markup, pageName);
        Assert.Contains(input.Ancestors(), ancestor => ReferenceEquals(ancestor, page));
        Assert.Equal("{Binding " + property + ", Mode=TwoWay}", (string?)input.Attribute("Value"));
        Assert.Equal("{Binding " + minimum + "}", (string?)input.Attribute("Minimum"));
        Assert.Equal("{Binding " + maximum + "}", (string?)input.Attribute("Maximum"));
        Assert.Equal("{Binding ElementName=" + property + "FieldLabel}", (string?)input.Attribute("AutomationProperties.LabeledBy"));
        Assert.Equal("{Binding " + property + "Label}", (string?)input.Attribute("AutomationProperties.Name"));
        Assert.Equal("{Binding " + property + "Help}", (string?)input.Attribute("AutomationProperties.HelpText"));
        Assert.Equal("{Binding " + property + "Label}", (string?)Named(markup, property + "FieldLabel").Attribute("Text"));
        Assert.Contains(page.Descendants(), node => (string?)node.Attribute("Text") == "{Binding " + property + "Hint}");
        Named(markup, name.Replace("Input", "InputError", StringComparison.Ordinal));
        Assert.NotEqual(key, Texts.T(key));
    }

    private static SettingsEditor CreateEditor()
    {
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        Set(editor, "_config", new Config());
        Set(editor, "_selectedTheme", new SettingsChoice("Dark", "old theme label"));
        Set(editor, "_selectedWorkspaceMode", new SettingsChoice("Advanced", "old mode label"));
        Set(editor, "_selectedReadingFontSize", new SettingsChoice("Large", "old font label"));
        Set(editor, "_selectedReaderPostWidth", new SettingsChoice("Full", "old width label"));
        Set(editor, "_selectedGalleryPlaybackMode", new SettingsChoice("Slideshow", "old gallery label"));
        Set(editor, "_selectedLanguage", new LanguageInfo("en", "old language label"));
        return editor;
    }

    private static XElement Named(XDocument markup, string name) =>
        Assert.Single(markup.Descendants(), element => (string?)element.Attribute(X + "Name") == name);

    private static XDocument Markup() => XDocument.Load(Path.Combine(RepositoryRoot(), "Desktop/Views/Settings/SettingsEditor.axaml"));
    private static string RepositoryRoot([CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../.."));
    private static string PackPath(string pack, [CallerFilePath] string path = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "../..", "Desktop/Localization/Packs", pack + ".json"));
    private static T Get<T>(SettingsEditor editor, string field) => (T)typeof(SettingsEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
    private static void Set(SettingsEditor editor, string field, object? value) => typeof(SettingsEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);
    private static void Invoke(SettingsEditor editor, string method, params object?[] args) => typeof(SettingsEditor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, args);
}
