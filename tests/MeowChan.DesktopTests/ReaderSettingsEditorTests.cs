using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia.Controls;
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
        (nameof(Config.ReadingHistoryLimit), "settings.reader.historyLimit", nameof(SettingsEditor.ReadingHistoryLimitMinimum), nameof(SettingsEditor.ReadingHistoryLimitMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderTextlessMediaWidth), "settings.reader.textlessWidth", nameof(SettingsEditor.ReaderTextlessMediaWidthMinimum), nameof(SettingsEditor.ReaderTextlessMediaWidthMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderFocusedMediaScalePercent), "settings.reader.focusedScale", nameof(SettingsEditor.ReaderFocusedMediaScalePercentMinimum), nameof(SettingsEditor.ReaderFocusedMediaScalePercentMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderHoverEnlargeSize), "settings.reader.hoverSize", nameof(SettingsEditor.ReaderHoverEnlargeSizeMinimum), nameof(SettingsEditor.ReaderHoverEnlargeSizeMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderHoverEnlargeDelayMs), "settings.reader.hoverDelay", nameof(SettingsEditor.ReaderHoverEnlargeDelayMsMinimum), nameof(SettingsEditor.ReaderHoverEnlargeDelayMsMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderMediaSidebarWidth), "settings.reader.mediaSidebarWidth", nameof(SettingsEditor.ReaderMediaSidebarWidthMinimum), nameof(SettingsEditor.ReaderMediaSidebarWidthMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderMediaSidebarMinimumWidth), "settings.reader.mediaSidebarMinimumWidth", nameof(SettingsEditor.ReaderMediaSidebarMinimumWidthMinimum), nameof(SettingsEditor.ReaderMediaSidebarMinimumWidthMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderInlineVideoMaxPlayers), "settings.reader.inlineVideoPlayers", nameof(SettingsEditor.ReaderInlineVideoMaxPlayersMinimum), nameof(SettingsEditor.ReaderInlineVideoMaxPlayersMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderInlineVideoMaxFileMiB), "settings.reader.inlineVideoMaxFile", nameof(SettingsEditor.ReaderInlineVideoMaxFileMiBMinimum), nameof(SettingsEditor.ReaderInlineVideoMaxFileMiBMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderMediaStripLimit), "settings.reader.mediaStripLimit", nameof(SettingsEditor.ReaderMediaStripLimitMinimum), nameof(SettingsEditor.ReaderMediaStripLimitMaximum), "SettingsReadingMediaPage"),
        (nameof(Config.ReaderMediaGridTextLimit), "settings.reader.mediaGridText", nameof(SettingsEditor.ReaderMediaGridTextLimitMinimum), nameof(SettingsEditor.ReaderMediaGridTextLimitMaximum), "SettingsReadingMediaPage"),
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
        GetInput(editor, nameof(Config.GalleryFitMaxUpscalePercent)).Text =
            editor.GalleryFitMaxUpscalePercent?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
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

    [Theory]
    [MemberData(nameof(ReaderSettingsPersistenceTests.Numbers), MemberType = typeof(ReaderSettingsPersistenceTests))]
    public void RawReaderNumbersCannotBeReplacedByAnAcceptedOrClampedValue(string name, int fallback, int min, int max)
    {
        // Unlike the existing ApplyReaderDraft bounds test, keep the bound value
        // valid throughout: only the raw text is rejected here.
        _ = name;
        foreach (decimal accepted in new[] { (decimal)fallback, min, max })
        {
            foreach (string? raw in new[] { null, "", "not a number", "79228162514264337593543950336" })
                Assert.Equal("settings.fieldNumber", SettingsEditor.NumericDraftError(raw, NumberStyles.Number,
                    CultureInfo.InvariantCulture, false, accepted, min, max, true));
            foreach (decimal rejected in new[] { min - 1m, max + 1m, min + 0.5m })
                Assert.Equal("settings.fieldInteger", SettingsEditor.NumericDraftError(rejected.ToString(CultureInfo.InvariantCulture),
                    NumberStyles.Number, CultureInfo.InvariantCulture, false, accepted, min, max, true));
        }
        foreach (int boundary in new[] { min, max })
            Assert.Null(SettingsEditor.NumericDraftError(boundary.ToString(CultureInfo.InvariantCulture), NumberStyles.Number,
                CultureInfo.InvariantCulture, false, boundary, min, max, true));
        Assert.Equal("settings.fieldNumber", SettingsEditor.NumericDraftError(fallback.ToString(CultureInfo.InvariantCulture),
            NumberStyles.Number, CultureInfo.InvariantCulture, true, fallback, min, max, true));
    }

    [Theory]
    [InlineData("not a number")]
    [InlineData("")]
    [InlineData("99")]
    [InlineData("1601")]
    [InlineData("725.5")]
    public void HidingARejectedGalleryEditPreservesTheSavedCapNotTheStaleAcceptedValue(string raw)
    {
        SettingsEditor editor = CreateValidDraft();
        Get<Config>(editor, "_config").GalleryFitMaxUpscalePercent = 350;
        editor.GalleryFitMaxUpscalePercent = 725; // last accepted edit, not saved
        NumericUpDown input = GetInput(editor, nameof(Config.GalleryFitMaxUpscalePercent));
        input.Value = 725;
        input.Text = raw;
        editor.SelectedGalleryFitUpscale = editor.GalleryFitUpscaleChoices.Single(item => item.Value == "Unlimited");
        Texts.SetLanguage("de");
        Invoke(editor, "RefreshChoiceLabels");
        Config candidate = new();
        Invoke(editor, "ApplyReaderDraft", candidate);
        Assert.False(candidate.GalleryFitUpscaleLimited);
        Assert.Equal(350, candidate.GalleryFitMaxUpscalePercent);
        Assert.Equal(725m, editor.GalleryFitMaxUpscalePercent); // capture does not mutate the draft
        Assert.Equal(raw, input.Text);
        Assert.Equal(350, Get<Config>(editor, "_config").GalleryFitMaxUpscalePercent);
        Invoke(editor, "LoadReaderDraft", Get<Config>(editor, "_config"));
        Assert.Equal(350m, editor.GalleryFitMaxUpscalePercent);
        Assert.Equal("350", input.Text);
    }

    [Fact]
    public void ReloadReaderDraftDiscardsAllRejectedTextAndChoicesUsingTheCurrentSavedConfig()
    {
        SettingsEditor editor = CreateValidDraft();
        Config saved = Get<Config>(editor, "_config");
        foreach (object[] row in ReaderSettingsPersistenceTests.Numbers())
        {
            string name = (string)row[0];
            typeof(Config).GetProperty(name)!.SetValue(saved, (int)row[2] + 1);
            GetInput(editor, name).Text = "rejected draft";
            typeof(SettingsEditor).GetProperty(name)!.SetValue(editor, null);
        }
        saved.ReaderDateStyle = "RelativeToNow";
        saved.ReaderSpoilers = "Always";
        saved.GalleryFitUpscaleLimited = false;
        // This is the production reader reset used by CancelChanges and the
        // successful-save reload, without bootstrapping a native SettingsWindow.
        Invoke(editor, "LoadReaderDraft", saved);
        foreach (object[] row in ReaderSettingsPersistenceTests.Numbers())
        {
            string name = (string)row[0];
            decimal expected = (int)row[2] + 1;
            Assert.Equal(expected, typeof(SettingsEditor).GetProperty(name)!.GetValue(editor));
            Assert.Equal(expected, GetInput(editor, name).Value);
            Assert.Equal(expected.ToString(CultureInfo.CurrentCulture), GetInput(editor, name).Text);
        }
        Assert.Equal("RelativeToNow", editor.SelectedReaderDateStyle!.Value);
        Assert.Equal("Always", editor.SelectedReaderSpoilers!.Value);
        Assert.False(editor.IsGalleryFitLimited);
    }

    public static IEnumerable<object[]> QuickReaderSettingIds() =>
        ReaderQuickSettings.Fields.Select(field => new object[] { field.Id });

    [Theory]
    [MemberData(nameof(QuickReaderSettingIds))]
    public void ConfirmedQuickSettingRefreshesOnlyItsOwnEditorAndKeepsUnrelatedDrafts(string id)
    {
        SettingsEditor editor = CreateValidDraft();
        Invoke(editor, "RefreshChoiceLabels");
        Config saved = Get<Config>(editor, "_config");
        QuickSettingField field = ReaderQuickSettings.Fields.Single(item => item.Id == id);
        string value = field switch
        {
            QuickIntegerField number => number.Maximum.ToString(CultureInfo.InvariantCulture),
            QuickChoiceField choice => choice.Choices.First(item => item.Value != DraftValue(editor, id)).Value,
            _ => (!bool.Parse(field.Read(saved))).ToString(),
        };
        Assert.True(field.TryCreateChange(value, out Action<Config> confirm));
        confirm(saved); // The popover has saved this field, not the page's other drafts.

        // Both a valid edit and rejected raw text must survive a different setting's confirmation.
        editor.ReaderReplyStep = 17;
        GetInput(editor, nameof(Config.ReaderReplyStep)).Value = 17;
        editor.ReaderFoldLines = null;
        GetInput(editor, nameof(Config.ReaderFoldLines)).Text = "unfinished draft";
        if (field is QuickIntegerField)
            GetInput(editor, id).Text = "replaced by confirmed value";
        Dictionary<string, string> before = ReaderQuickSettings.Fields.ToDictionary(item => item.Id, item => DraftValue(editor, item.Id));
        Dictionary<string, string?> raw = ReaderSettingsPersistenceTests.Numbers()
            .ToDictionary(row => (string)row[0], row => GetInput(editor, (string)row[0]).Text);
        string savedBefore = System.Text.Json.JsonSerializer.Serialize(saved);

        editor.RefreshConfirmedSettings(new[] { id });

        Assert.Equal(value, DraftValue(editor, id));
        foreach (QuickSettingField other in ReaderQuickSettings.Fields.Where(item => item.Id != id))
            Assert.Equal(before[other.Id], DraftValue(editor, other.Id));
        foreach (var pair in raw)
        {
            if (pair.Key == id)
            {
                Assert.Equal((decimal)int.Parse(value, CultureInfo.InvariantCulture), GetInput(editor, id).Value);
                Assert.Equal(value, GetInput(editor, id).Text);
            }
            else Assert.Equal(pair.Value, GetInput(editor, pair.Key).Text);
        }
        Assert.Null(editor.ReaderFoldLines);
        Assert.Equal(17m, editor.ReaderReplyStep);
        Assert.Equal(savedBefore, System.Text.Json.JsonSerializer.Serialize(saved));
        if (id == SettingsFieldId.ReadingFontSize)
            Assert.Equal(value, Get<string>(editor, "_initialReadingFontSize"));
    }

    [Fact]
    public void ConfirmedReaderBatchRefreshesAllNamedFieldsWithoutReloadingTheDraft()
    {
        SettingsEditor editor = CreateValidDraft();
        Config saved = Get<Config>(editor, "_config");
        saved.ReaderMediaSidebar = true;
        saved.ReaderMediaSidebarSide = Config.ReaderMediaSidebarSideRight;
        saved.ReaderMediaSidebarWidth = 760;
        saved.ReaderInlineVideo = true;
        saved.ReaderInlineVideoAutoplayMuted = false;
        editor.ReaderInlineVideoAutoplayMuted = true; // Confirmation must also propagate false.
        editor.ReaderPreviewDelayMs = 123;
        GetInput(editor, nameof(Config.ReaderPreviewDelayMs)).Value = 123;
        editor.ReaderFoldLines = null;
        GetInput(editor, nameof(Config.ReaderFoldLines)).Text = "unfinished draft";

        editor.RefreshConfirmedSettings(new[] { SettingsFieldId.ReaderMediaSidebar, SettingsFieldId.ReaderMediaSidebarSide,
            SettingsFieldId.ReaderMediaSidebarWidth, SettingsFieldId.ReaderInlineVideo, SettingsFieldId.ReaderInlineVideoAutoplayMuted });

        Assert.True(editor.ReaderMediaSidebar);
        Assert.Equal(Config.ReaderMediaSidebarSideRight, editor.SelectedReaderMediaSidebarSide!.Value);
        Assert.Equal(760m, editor.ReaderMediaSidebarWidth);
        Assert.Equal("760", GetInput(editor, nameof(Config.ReaderMediaSidebarWidth)).Text);
        Assert.True(editor.ReaderInlineVideo);
        Assert.False(editor.ReaderInlineVideoAutoplayMuted);
        Assert.Equal(123m, editor.ReaderPreviewDelayMs);
        Assert.Null(editor.ReaderFoldLines);
        Assert.Equal("unfinished draft", GetInput(editor, nameof(Config.ReaderFoldLines)).Text);
    }

    private static string DraftValue(SettingsEditor editor, string id)
    {
        PropertyInfo property = typeof(SettingsEditor).GetProperty(id) ?? typeof(SettingsEditor).GetProperty("Selected" + id)!;
        return property.GetValue(editor) switch
        {
            SettingsChoice choice => choice.Value,
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            object value => value.ToString()!,
            _ => string.Empty,
        };
    }

    // CLR-only entry into the same reader capture used by Save. Host tests use
    // this to verify editor -> candidate -> disk, not just Config -> Config.
    internal static Config CaptureDraft(Config saved, Config desired)
    {
        SettingsEditor editor = CreateValidDraft();
        Set(editor, "_config", saved);
        foreach (object[] row in ReaderSettingsPersistenceTests.Numbers())
        {
            string name = (string)row[0];
            int value = (int)typeof(Config).GetProperty(name)!.GetValue(desired)!;
            typeof(SettingsEditor).GetProperty(name)!.SetValue(editor, (decimal?)value);
            GetInput(editor, name).Value = value;
            GetInput(editor, name).Text = value.ToString(CultureInfo.InvariantCulture);
        }
        Invoke(editor, "RefreshReaderChoices", desired.ReaderDateStyle, desired.ReaderSpoilers,
            desired.GalleryFitUpscaleLimited ? "Limited" : "Unlimited");
        Config candidate = saved.Clone();
        Invoke(editor, "ApplyReaderDraft", candidate);
        return candidate;
    }

    private static SettingsEditor CreateValidDraft()
    {
        SettingsEditor editor = CreateEditor();
        Config defaults = new();
        foreach (object[] row in ReaderSettingsPersistenceTests.Numbers())
        {
            string name = (string)row[0];
            int value = (int)typeof(Config).GetProperty(name)!.GetValue(defaults)!;
            typeof(SettingsEditor).GetProperty(name)!.SetValue(editor, (decimal?)value);
            Set(editor, name + "Input", new SettingsNumberInput
            {
                Name = name + "Input", Minimum = (int)row[2], Maximum = (int)row[3],
                NumberFormat = CultureInfo.InvariantCulture.NumberFormat, Value = value,
                Text = value.ToString(CultureInfo.InvariantCulture)
            });
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
    private static NumericUpDown GetInput(SettingsEditor editor, string property) => Get<NumericUpDown>(editor, property + "Input");
    private static T Get<T>(SettingsEditor editor, string field) => (T)typeof(SettingsEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
    private static void Set(SettingsEditor editor, string field, object? value) => typeof(SettingsEditor).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, value);
    private static void Invoke(SettingsEditor editor, string method, params object?[] args) => typeof(SettingsEditor).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(editor, args);
}
