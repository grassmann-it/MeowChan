using System.Reflection;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia.Controls;
using MeowChan;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan.DesktopTests;

// Pure presentation and source-contract coverage, not a native-window test.
// Language subscriptions are a necessary lifecycle contract; the test does not
// claim that the callback alone refreshes every materialized label. See the report.
[Collection("Localization")]
public sealed class SecondaryLanguageRefreshRegressionTests : IDisposable
{
    private readonly string _previousLanguage = Texts.Language;

    public SecondaryLanguageRefreshRegressionTests()
    {
        Texts.Initialize();
        Texts.SetLanguage("en");
    }

    public void Dispose()
    {
        Texts.Initialize();
        Texts.SetLanguage(_previousLanguage);
    }

    [Theory]
    [InlineData("Desktop/Views/Admission/BoardAdmissionWindow.cs")]
    [InlineData("Desktop/MediaRatingDialog.cs")]
    [InlineData("Desktop/MediaRatingsManagerDialog.cs")]
    [InlineData("Desktop/Views/About/AboutWindow.axaml.cs")]
    [InlineData("Desktop/StartupErrorWindow.cs")]
    [InlineData("Desktop/Views/Settings/FirstRunSetupWindow.axaml.cs")]
    [InlineData("Desktop/Views/Admission/ExperimentalActivationDialog.axaml.cs")]
    [InlineData("Desktop/Views/Collections/PostBookmarksDialog.axaml.cs")]
    [InlineData("Desktop/Views/Collections/ReaderGroupsDialog.axaml.cs")]
    [InlineData("Desktop/Views/Collections/SavedArchiveSearchesDialog.axaml.cs")]
    [InlineData("Desktop/ArchiveCleanupDialog.cs")]
    public void LocalizedSecondaryWindowOwnsAMatchedLanguageSubscription(string path)
    {
        string source = ReadSource(path);
        // Necessary lifecycle contract only: this does not prove that every
        // materialized label is updated or that callbacks dispatch correctly.
        Match subscription = Regex.Match(source, @"Texts\.Changed\s*\+=\s*(\w+)\s*;");
        Assert.True(subscription.Success,
            $"{path}: constructor-time translations have no Texts.Changed subscription.");
        string handler = Regex.Escape(subscription.Groups[1].Value);
        Assert.True(Regex.IsMatch(source, @"Texts\.Changed\s*-=\s*" + handler + @"\s*;"),
            $"{path}: language subscription must be released at lifetime end.");
    }

    [Fact]
    public void ExistingCaptionButtonCanBeRelabeledWithoutReplacingIt()
    {
        Button button = new();
        foreach (var (language, expected) in new[]
        {
            ("en", "Close"), ("de", "Schließen"), ("fr", "Fermer"), ("en", "Close")
        })
        {
            Texts.SetLanguage(language);
            WindowChromeBar.SetLabel(button, "action.close");
            Assert.Equal(expected, AutomationProperties.GetName(button));
            Assert.Equal(expected, ToolTip.GetTip(button));
        }
    }

    [Fact]
    public void SettingsLabelGettersReadTheCurrentLanguageRatherThanCachingIt()
    {
        // Only CLR text getters: no XAML, storage, application or Window startup.
        var editor = (SettingsEditor)RuntimeHelpers.GetUninitializedObject(typeof(SettingsEditor));
        foreach (string language in new[] { "en", "de", "fr", "en" })
        {
            Texts.SetLanguage(language);
            Assert.Equal(Texts.T("settings.title"), editor.SettingsTitle);
            Assert.Equal(Texts.T("settings.downloadPath"), editor.DownloadPathLabel);
            Assert.Equal(Texts.T("resume.forget"), editor.ForgetReadingPositionLabel);
        }
    }

    [Fact]
    public void SettingsRefreshReappliesCommandsAndSectionNavigationWithoutResettingTheDraft()
    {
        string refresh = Between(ReadSource("Desktop/Views/Settings/SettingsEditor.axaml.cs"),
            "private void OnLanguageChanged(", "private void ApplyCommandIcons(");
        Assert.Contains("if (_isClosed)", refresh);
        Assert.Contains("RefreshChoiceLabels();", refresh);
        string choices = Between(ReadSource("Desktop/Views/Settings/SettingsEditor.axaml.cs"),
            "private void RefreshChoiceLabels()", "private void RefreshChoices(");
        Assert.Contains("RefreshChoices(", choices);
        Assert.Contains("ApplyCommandIcons();", refresh);
        Assert.Contains("RefreshSectionNavigation();", refresh);
        Assert.Contains("Dispatcher.UIThread.Post(Refresh)", refresh);
        Assert.DoesNotContain("ResetNumber(", refresh);
        Assert.DoesNotContain("CancelChanges();", refresh);
    }

    [Fact]
    public void AboutRefreshReappliesTabsLicenseMetadataAndUpdates()
    {
        string source = ReadSource("Desktop/Views/About/AboutWindow.axaml.cs");
        string apply = Between(source, "private void ApplyTexts()", "private void OnTextsChanged(");
        foreach (string target in new[] { "Title", "AboutTab.Header", "CreditsTab.Header", "LicensesTab.Header",
            "LicenseSearchBox.PlaceholderText", "CloseButton.Content" })
            Assert.Contains(target + " = Texts.T(", apply);
        Assert.Contains("UpdateSelection();", apply);
        Assert.Contains("RefreshUpdates();", apply);
        string callback = Between(source, "private void OnTextsChanged(", "protected override void OnKeyDown(");
        Assert.Contains("Dispatcher.UIThread.Post(", callback);
        Assert.Contains("if (!_closed)", callback);
        Assert.Contains("ApplyTexts();", callback);
    }

    [Fact]
    public void RulesRefreshInvalidatesMaterializedFiltersAndReappliesChoiceAndCommandLabels()
    {
        string source = ReadSource("Desktop/Views/Rules/RulesPage.axaml.cs");
        string refresh = Between(source, "private void OnLanguageChanged(", "private void ApplyCommandIcons(");
        foreach (string operation in new[] { "RefreshChoiceLabels();", "_filterRenderedDraft = null;",
            "UpdatePreview();", "RaiseAll();", "ApplyCommandIcons();", "Dispatcher.UIThread.Post(Refresh)" })
            Assert.Contains(operation, refresh);
        string templates = ReadSource("Desktop/Views/Rules/RulesPage.Templates.cs");
        Assert.Contains("_renderedPreviewLanguage != Texts.Language", templates);
        Assert.Contains("_renderedEditorLanguage != Texts.Language", templates);
        Assert.Contains("LocalizedTemplateError(\"templates.previewStale\")", templates);
        Assert.Contains("RawTemplateError(exception.Message)", templates);
        string editor = ReadSource("Desktop/Views/Rules/RulesPage.TemplateEditor.cs");
        Assert.Contains("internal TemplateErrorState? Error", editor);
        Assert.Contains("editor.Error?.Render()", editor);
        Assert.Contains("catch (RuleInputException exception)", editor);
        Assert.Contains("catch (Exception exception)", editor);
        Assert.DoesNotContain("editor.Error = Texts.T(", editor);
    }

    [Fact]
    public void AttachedShortcutEditorRelocalizesMaterializedRowsAndDetailLabelsInPlace()
    {
        string source = ReadSource("Desktop/Commands/ShortcutEditor.cs");
        Assert.Contains("Texts.Changed += OnLanguageChanged;", source);
        Assert.Contains("Texts.Changed -= OnLanguageChanged;", source);

        string localize = Between(source, "private void Localize()", "private void BuildFrame()");
        // Language changes must relabel the already-materialized presentation. A
        // list rebuild would replace the focused row, selection, detail controls,
        // and potentially the active capture lease.
        Assert.Contains("foreach (var refresh in _rowTexts) refresh();", localize);
        Assert.Contains("foreach (var refresh in _detailTexts) refresh();", localize);
        Assert.DoesNotContain("RefreshList();", localize);
        Assert.DoesNotContain("RefreshDetail();", localize);

        string list = Between(source, "private void RefreshList()", "private IReadOnlyList<ShortcutGesture> DraftGestures(");
        Assert.Contains("Text = command.Label", list);
        Assert.Contains("AutomationProperties.SetName(item, accessible)", list);
        Assert.Contains("void RefreshTexts()", list);
        Assert.Contains("label.Text = command.Label;", list);
        Assert.Contains("binding.Text = Binding();", list);
        Assert.Contains("stateText.Text = State();", list);
        Assert.Contains("AutomationProperties.SetName(item, Accessible());", list);
        Assert.Contains("RefreshDetail();", list);

        Assert.Contains("_rowTexts.Add(row.Refresh)", list);
        string detail = Between(source, "private void RefreshDetail()", "private void CaptureKey(");
        Assert.Contains("_detailTexts.Clear();", detail);
        Assert.Contains("!CommandCatalog.TryGet(_selected, out var command) || command is null", detail);
        Assert.Contains("var title = LocalizedText(() => command.Label, _detailTexts);", detail);
        Assert.Contains("Button(() => Texts.T(\"settings.shortcutsEditor.remove\")", detail);
        Assert.Contains("Button(() => Texts.T(\"settings.shortcutsEditor.recordAdditional\")", detail);
        Assert.Contains("Button(() => Texts.T(\"settings.shortcutsEditor.clearBinding\")", detail);
        Assert.Contains("Button(() => Texts.T(\"settings.shortcutsEditor.restoreDefault\")", detail);
        Assert.Contains("Button(() => Texts.T(\"settings.shortcutsEditor.applyRecording\")", detail);
        Assert.Contains("Button(() => Texts.T(\"settings.shortcutsEditor.takeOver\")", detail);
        Assert.Contains("Button(() => Texts.T(\"settings.shortcutsEditor.cancelRecording\")", detail);
        Assert.Equal(7, Regex.Matches(detail, @", _detailTexts\)\);").Count);

        string textHelper = Between(source, "private static TextBlock LocalizedText(", "private static Button Button(");
        Assert.Contains("void Refresh() => text.Text = label();", textHelper);
        Assert.Contains("refreshers.Add(Refresh);", textHelper);
        string buttonHelper = source[source.IndexOf("private static Button Button(", StringComparison.Ordinal)..];
        Assert.Contains("button.Content = text; AutomationProperties.SetName(button, text);", buttonHelper);
        Assert.Contains("refreshers.Add(Refresh);", buttonHelper);
    }

    [Theory]
    [InlineData("templates.name")]
    [InlineData("templates.previewStale")]
    [InlineData("templates.incompatible")]
    [InlineData("rules.invalidTypes")]
    [InlineData("rules.invalidSize")]
    [InlineData("plugins.noFilters")]
    [InlineData("plugins.invalidFilterSettings")]
    public void TheSameTemplateErrorStateRendersTheCurrentLanguage(string key)
    {
        object state = RulesMethod("LocalizedTemplateError", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object?[] { key, Array.Empty<object?>() })!;
        string english = RenderTemplateError(state);
        Texts.SetLanguage("de");
        Assert.Equal(Texts.T(key), RenderTemplateError(state));
        Assert.NotEqual(english, RenderTemplateError(state));
        Texts.SetLanguage("en");
        Assert.Equal(english, RenderTemplateError(state));
    }

    [Fact]
    public void ExternalTemplateDetailsAreNotGuessedToBeLocalizationKeysOrTranslatedText()
    {
        // Even text identical to an app-owned EN message must remain raw when it
        // originated at a store/worker boundary. Provenance, not text, selects rendering.
        foreach (string raw in new[] { "templates.previewStale", Texts.T("templates.name"), "Worker: Ä Ω\nIO: /raw/path" })
        {
            object state = RulesMethod("RawTemplateError", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, new object?[] { raw })!;
            foreach (string language in new[] { "de", "en", "de" })
            {
                Texts.SetLanguage(language);
                Assert.Equal(raw, RenderTemplateError(state));
            }
        }
    }

    [Theory]
    [InlineData("01.5000", false)]
    [InlineData("1,5000", false)]
    [InlineData("unfinished,-", true)]
    public void TemplateLanguageRefreshKeepsRawDraftCultureFingerprintAckAndRevision(string raw, bool dirty)
    {
        Type draftType = typeof(RulesPage).GetNestedType("TemplateEditorDraft", BindingFlags.NonPublic)!;
        object draft = Activator.CreateInstance(draftType, nonPublic: true)!;
        FieldInfo Field(string name) => draftType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        var culture = CultureInfo.GetCultureInfo("en");
        Field("Name").SetValue(draft, "  unfinished name  ");
        Field("Megabytes").SetValue(draft, raw);
        Field("Culture").SetValue(draft, culture);
        Field("SchemaAcknowledged").SetValue(draft, true);
        Field("Revision").SetValue(draft, "fixed-revision");
        PropertyInfo fingerprint = draftType.GetProperty("Fingerprint", BindingFlags.Instance | BindingFlags.NonPublic)!;
        string before = (string)fingerprint.GetValue(draft)!;
        string initial = dirty ? "previous input" : before;
        Field("Initial").SetValue(draft, initial);
        var page = (RulesPage)RuntimeHelpers.GetUninitializedObject(typeof(RulesPage));
        typeof(RulesPage).GetField("_templateEditor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, draft);
        foreach (string language in new[] { "de", "en", "de" })
        {
            Texts.SetLanguage(language);
            // Invoke the actual refresh preparation without constructing windows.
            RulesMethod("ReformatTemplateEditor", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(page, null);
            Assert.Equal(raw, Field("Megabytes").GetValue(draft));
            Assert.Same(culture, Field("Culture").GetValue(draft));
            Assert.Equal(before, fingerprint.GetValue(draft));
            Assert.Equal(initial, Field("Initial").GetValue(draft));
            Assert.Equal(true, Field("SchemaAcknowledged").GetValue(draft));
            Assert.Equal("fixed-revision", Field("Revision").GetValue(draft));
        }
    }

    [Fact]
    public void TemplateRefreshDoesNotRetrySaveWorkerValidationOrLoading()
    {
        string editor = ReadSource("Desktop/Views/Rules/RulesPage.TemplateEditor.cs");
        string preparation = Between(editor, "private void ReformatTemplateEditor()", "private void InitializeTemplateParameters(");
        string presentation = Between(editor, "private void UpdateTemplateEditorPresentation()", "private void FocusTemplateEditorError(");
        string templates = ReadSource("Desktop/Views/Rules/RulesPage.Templates.cs");
        string panel = Between(templates, "private void RefreshTemplatesPanel()", "private sealed record TemplateChoice(");
        foreach (string block in new[] { preparation, presentation })
        {
            Assert.DoesNotContain("await ", block);
            Assert.DoesNotContain("ValidateTemplateFilter", block);
            Assert.DoesNotContain("TemplateCompatibilityError", block);
            Assert.DoesNotContain("TemplateCompatibilityState", block);
            Assert.DoesNotContain("Snapshot", block);
            Assert.DoesNotContain("await _loadTemplates(", block);
            Assert.DoesNotContain("await _saveTemplates(", block);
        }
        // Panel setup installs user-action callbacks; those may await when clicked,
        // but rendering itself must not directly invoke service/worker boundaries.
        Assert.DoesNotContain("await _loadTemplates(", panel);
        Assert.DoesNotContain("await _saveTemplates(", panel);
        Assert.DoesNotContain("ValidateTemplateFilter", panel);
        Assert.DoesNotContain("TemplateCompatibilityError", panel);
        Assert.DoesNotContain("TemplateCompatibilityState", panel);
        Assert.DoesNotContain("Snapshot", panel);
        Assert.DoesNotContain("Texts.T(\"templates.previewStale\")", templates);
        Assert.Contains("throw new RuleInputException(\"templates.name\")", editor);
        Assert.Contains("editor.Error = RawTemplateError(exception.Message)", editor);
    }

    [Fact]
    public void ExplicitPluginProfileReplacementsRefreshTemplateCompatibilityCache()
    {
        string source = ReadSource("Desktop/Views/Rules/RulesPage.Plugins.cs");
        string refresh = Between(source, "internal async Task RefreshPluginFiltersAsync()", "private void RefreshFilterEditor()");
        Assert.Contains("_pluginProfile = profile; CacheTemplateCompatibility();", refresh);
        Assert.Contains("_filterRenderedDraft = null; RefreshFilterEditor();", refresh);

        string grant = Between(source, "private async Task ChangeRuleGrantAsync(", "catch (OperationCanceledException)");
        Assert.Contains("_pluginProfile = await _pluginServices.ProfileStore.SetGrantAsync", grant);
        int assignment = grant.IndexOf("_pluginProfile = await _pluginServices.ProfileStore.SetGrantAsync", StringComparison.Ordinal);
        int cache = grant.IndexOf("CacheTemplateCompatibility();", assignment, StringComparison.Ordinal);
        int lifetime = grant.IndexOf("if (_ended) return;", assignment, StringComparison.Ordinal);
        int presentation = grant.IndexOf("_filterRenderedDraft = null;", assignment, StringComparison.Ordinal);
        Assert.True(lifetime > assignment && cache > lifetime && presentation > cache,
            "The explicit grant/profile replacement must recapture compatibility after its lifetime check and before presentation invalidation.");
        Assert.Equal(2, Regex.Matches(source, @"_pluginProfile\s*=(?!=)").Count);
        Assert.Equal(2, Regex.Matches(source, @"CacheTemplateCompatibility\(\);").Count);
        string render = Between(source, "private void RefreshFilterEditor()", "private void BindFilterParameterChanges(");
        Assert.DoesNotContain("CacheTemplateCompatibility(", render);
    }

    [Fact]
    public void TemplateListRefreshReadsOnlyPreviouslyCapturedCompatibilityErrors()
    {
        string templates = ReadSource("Desktop/Views/Rules/RulesPage.Templates.cs");
        string panel = Between(templates, "private void RefreshTemplatesPanel()", "private sealed record TemplateChoice(");
        Assert.Contains("CachedTemplateError(t)", panel);
        Assert.DoesNotContain("CacheTemplateCompatibility(", panel);
        string cached = Between(templates, "private TemplateErrorState? CachedTemplateError(",
            "private TemplateErrorState? TemplateCompatibilityState(");
        Assert.Contains("_templateCompatibility.TryGetValue", cached);
        Assert.Contains("cached.Error", cached);
        Assert.Contains("cached.PluginRevision == _pluginProfile?.Revision", cached);
        foreach (string block in new[] { panel, cached })
        {
            Assert.DoesNotContain("TemplateCompatibilityError(", block);
            Assert.DoesNotContain("TemplateCompatibilityState(", block);
            Assert.DoesNotContain("Snapshot(", block);
            Assert.DoesNotContain("ValidateTemplateFilter", block);
        }
        string capture = Between(templates, "private void CacheTemplateCompatibility()",
            "private TemplateErrorState? CachedTemplateError(");
        Assert.Contains("TemplateCompatibilityState(template)", capture);
        string load = Between(templates, "private async Task LoadTemplatesAsync()", "private void RefreshTemplatesPanel()");
        Assert.Contains("CacheTemplateCompatibility();", load);
    }

    [Fact]
    public void DuplicateTemplateNamesUseHostErrorStateBeforeWorkerValidation()
    {
        string save = Between(ReadSource("Desktop/Views/Rules/RulesPage.TemplateEditor.cs"),
            "private async Task SaveTemplateEditorAsync()", "catch (RuleInputException exception)");
        int duplicate = save.IndexOf("string.Equals(t.Name, value.Name, StringComparison.OrdinalIgnoreCase)", StringComparison.Ordinal);
        int validation = save.IndexOf("await ValidateTemplateFilterFieldsAsync(value)", StringComparison.Ordinal);
        Assert.True(duplicate >= 0 && validation > duplicate);
        Assert.Contains("throw new RuleInputException(\"templates.name\")", save[duplicate..validation]);
    }

    private static MethodInfo RulesMethod(string name, BindingFlags flags) =>
        typeof(RulesPage).GetMethod(name, flags)!;

    private static string RenderTemplateError(object state) => (string)state.GetType()
        .GetMethod("Render", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(state, null)!;

    private static string Between(string source, string start, string end)
    {
        int first = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(first >= 0, "Source anchor missing: " + start);
        int last = source.IndexOf(end, first + start.Length, StringComparison.Ordinal);
        Assert.True(last > first, "Source anchor missing: " + end);
        return source[first..last];
    }

    private static string ReadSource(string path, [CallerFilePath] string sourcePath = "") =>
        File.ReadAllText(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../..", path)));
}
