using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan.DesktopTests;

/// <summary>
/// Overview first (14.11.2026): the Rules page opens on its overview (saved rules by scope, drafts, built-in defaults) and edits in
/// an in-page wizard. The model and wizard state are pure; the visual proof lives in the rules smokes (rules-settings-captures,
/// work-views-rules, ui-refinement/poweruser).
/// </summary>
[Collection("Localization")]
public sealed class RulesOverviewTests
{
    [Fact]
    public void AnEmptyCatalogIsAnEmptyOverviewWithTheBuiltInBaseline()
    {
        InEnglish(() =>
        {
            RuleOverview overview = RulesOverviewModel.Build(MediaRuleSet.Default, Array.Empty<RuleOverviewDraft>());
            Assert.True(overview.IsEmpty);
            Assert.Empty(overview.Groups);
            Assert.Equal(0, overview.SavedCount);
            Assert.NotEmpty(overview.Defaults);
            // The baseline is what a rule without own values resolves to, read-only and never "inherit".
            EffectiveMediaRule builtIn = new MediaRuleResolver(MediaRuleSet.Default).Resolve(MediaRuleScope.Global);
            Assert.Equal(RulesOverviewModel.TypesChip(builtIn.Types), overview.Defaults[0].Text);
            Assert.DoesNotContain(overview.Defaults, chip => chip.Text == Texts.T("rules.overview.inheritsAll"));
        });
    }

    [Fact]
    public void SavedRulesAreGroupedFromBroadToSpecificAndSortedByIdentity()
    {
        InEnglish(() =>
        {
            MediaRuleSet rules = new("r1", new[]
            {
                new MediaRule(MediaRuleScope.ForThread("4chan", "g", "9")),
                new MediaRule(MediaRuleScope.ForSource("zzz")),
                new MediaRule(MediaRuleScope.ForBoard("4chan", "g")),
                new MediaRule(MediaRuleScope.Global),
                new MediaRule(MediaRuleScope.ForSource("4chan")),
                new MediaRule(MediaRuleScope.ForBoard("4chan", "a"))
            });
            RuleOverview overview = RulesOverviewModel.Build(rules, Array.Empty<RuleOverviewDraft>());
            Assert.False(overview.IsEmpty);
            Assert.Equal(6, overview.SavedCount);
            Assert.Equal(new[] { MediaRuleScopeKind.Global, MediaRuleScopeKind.Source, MediaRuleScopeKind.Board, MediaRuleScopeKind.Thread },
                overview.Groups.Select(group => group.Kind));
            Assert.Equal(new[] { "4chan", "zzz" }, overview.Groups[1].Rows.Select(row => row.Scope.Source));
            Assert.Equal(new[] { "a", "g" }, overview.Groups[2].Rows.Select(row => row.Scope.Board));
            Assert.Equal(Texts.T("rules.board"), overview.Groups[2].Title);
            // Under its heading a row names only the identity; the kind is the group's title.
            Assert.Equal(new[] { "4chan/a", "4chan/g" }, overview.Groups[2].Rows.Select(row => row.ScopeLabel));
            Assert.Equal(Texts.T("rules.global"), overview.Groups[0].Rows.Single().ScopeLabel);
            Assert.All(overview.Groups.SelectMany(group => group.Rows), row => Assert.True(row.Saved && !row.HasDraft));
        });
    }

    [Fact]
    public void ChipsSummariseOnlyWhatARuleOverrides()
    {
        InEnglish(() =>
        {
            MediaRule own = new(MediaRuleScope.ForBoard("4chan", "g"), true, new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { "jpg", "png" }),
                new MediaSizeLimit(MediaSizeLimitMode.Limited, 20L * 1024 * 1024)) { ImageFilter = MediaImageFilterRule.Disabled };
            Assert.Equal(new[] { ".jpg, .png", "≤ 20 MiB", "Image filter: off" }, RulesOverviewModel.RuleChips(own).Select(chip => chip.Text));

            MediaRule inherits = new(MediaRuleScope.Global, false);
            Assert.Equal(new[] { "Inherits all values" }, RulesOverviewModel.RuleChips(inherits).Select(chip => chip.Text));

            MediaRule many = new(MediaRuleScope.Global, types: new MediaTypeSelection(MediaTypeSelectionMode.Selected, new[] { "jpg", "png", "gif", "webm", "mp4", "webp" }),
                sizeLimit: MediaSizeLimit.Unlimited);
            Assert.Equal(new[] { ".gif, .jpg, .mp4, .png +2", "No size limit" }, RulesOverviewModel.RuleChips(many).Select(chip => chip.Text));
            Assert.Equal("No media", RulesOverviewModel.TypesChip(new MediaTypeSelection(MediaTypeSelectionMode.Selected)));
            MediaImageFilterRule plugin = new(MediaImageFilterMode.Plugin, "meowchan.example", "blur", "1");
            Assert.Equal("Image filter: Blur", RulesOverviewModel.FilterChip(plugin, _ => "Blur"));

            RuleOverview overview = RulesOverviewModel.Build(new MediaRuleSet("r", new[] { own, inherits }), Array.Empty<RuleOverviewDraft>());
            RuleOverviewRow disabled = overview.Groups[0].Rows.Single();
            Assert.False(disabled.Enabled);
            Assert.Equal(new RuleChip("Disabled", RuleChipTone.Warning), disabled.Chips[0]);
            Assert.Equal(new RuleChip("Active", RuleChipTone.Success), overview.Groups[1].Rows.Single().Chips[0]);
        });
    }

    [Fact]
    public void DraftsStayDiscoverableWithTheirStateAndMarkTheirSavedRule()
    {
        InEnglish(() =>
        {
            MediaRuleScope saved = MediaRuleScope.ForBoard("4chan", "g");
            MediaRuleScope fresh = MediaRuleScope.ForThread("4chan", "g", "1");
            MediaRuleScope invalid = MediaRuleScope.ForSource("broken");
            MediaRuleSet rules = new("r", new[] { new MediaRule(saved) });
            RuleOverview overview = RulesOverviewModel.Build(rules, new[]
            {
                new RuleOverviewDraft(fresh, new MediaRule(fresh, sizeLimit: MediaSizeLimit.Unlimited), false),
                new RuleOverviewDraft(saved, new MediaRule(saved, false), true),
                new RuleOverviewDraft(invalid, null, false)
            });
            Assert.False(overview.IsEmpty);
            // Drafts in the same broad-to-specific order; each names its state first.
            Assert.Equal(new[] { invalid, saved, fresh }, overview.Drafts.Select(row => row.Scope));
            Assert.Equal(new[] { "Not saved", "Invalid input" }, overview.Drafts[0].Chips.Select(chip => chip.Text));
            Assert.Equal(new[] { "Unsaved draft", "Saved rule changed meanwhile" }, overview.Drafts[1].Chips.Select(chip => chip.Text));
            Assert.Equal(new[] { "Not saved", "No size limit" }, overview.Drafts[2].Chips.Select(chip => chip.Text));
            Assert.All(overview.Drafts, row => Assert.True(row.HasDraft));
            RuleOverviewRow savedRow = overview.Groups.Single().Rows.Single();
            Assert.True(savedRow.HasDraft);
            Assert.Contains(new RuleChip("Unsaved draft", RuleChipTone.Warning), savedRow.Chips);
            // Only the drafted scope changes the fingerprint (the page rebuilds and restores focus only then).
            Assert.NotEqual(RulesOverviewModel.Fingerprint(overview),
                RulesOverviewModel.Fingerprint(RulesOverviewModel.Build(rules, Array.Empty<RuleOverviewDraft>())));
            Assert.Equal(RulesOverviewModel.Fingerprint(RulesOverviewModel.Build(rules, Array.Empty<RuleOverviewDraft>())),
                RulesOverviewModel.Fingerprint(RulesOverviewModel.Build(rules, Array.Empty<RuleOverviewDraft>())));
        });
    }

    [Fact]
    public void TheWizardOpensAtTheRightStepAndReachesSettingsOnlyWithAScope()
    {
        RuleWizardState wizard = new();
        Assert.False(wizard.IsOpen);
        wizard.OpenNew();
        Assert.True(wizard.IsOpen);
        Assert.Equal(RuleWizardStep.Scope, wizard.Step);
        Assert.False(wizard.CanGoTo(RuleWizardStep.Settings));
        Assert.False(wizard.GoTo(RuleWizardStep.Review));
        Assert.False(wizard.Next()); // step 1 continues by choosing its scope, not by Next.

        wizard.OpenEdit(); // a scope was chosen (or Edit on the overview): straight to step 2
        Assert.Equal(RuleWizardStep.Settings, wizard.Step);
        Assert.True(wizard.Next());
        Assert.Equal(RuleWizardStep.Review, wizard.Step);
        Assert.True(wizard.Back());
        Assert.Equal(RuleWizardStep.Settings, wizard.Step);
        Assert.True(wizard.Back());
        Assert.Equal(RuleWizardStep.Scope, wizard.Step);
        Assert.True(wizard.CanGoTo(RuleWizardStep.Review)); // the chips keep a chosen scope's steps reachable
        Assert.False(wizard.Back());

        wizard.OpenNew(revealTemplates: true);
        Assert.True(wizard.RevealTemplates);
        wizard.Close();
        Assert.False(wizard.IsOpen);
        Assert.False(wizard.RevealTemplates);
        Assert.False(wizard.CanGoTo(RuleWizardStep.Scope));
    }

    [Fact]
    public void DraftsSurviveClosingTheWizardAndLeavingThePage()
    {
        string wizard = ReadSource("Desktop/Views/Rules/RulesPage.Wizard.cs");
        string page = ReadSource("Desktop/Views/Rules/RulesPage.axaml.cs");
        string templates = ReadSource("Desktop/Views/Rules/RulesPage.Templates.cs");
        // Closing the wizard and leaving the page only close decisions; the draft table is never cleared there.
        Assert.DoesNotContain("_drafts", Between(wizard, "internal void CloseWizard()", "private void FocusFirstWizardField()"));
        Assert.DoesNotContain("_drafts", Between(templates, "internal void DeactivateRuleDecisions()", "private RuleTemplate? SelectedTemplate()"));
        Assert.DoesNotContain("_wizard.Close", Between(templates, "internal void DeactivateRuleDecisions()", "private RuleTemplate? SelectedTemplate()"));
        Assert.Contains("_rulesPage?.DeactivateRuleDecisions();", ReadSource("Desktop/Shell/MainWindow.Navigation.cs"));
        // Context actions (thread, board, source) open the retained editor in step 2.
        Assert.Contains("EnterWizardForScope();", Between(page, "public void EditScope(MediaRuleScope scope)", "private bool SelectScope("));
    }

    [Fact]
    public void ASuccessfulSaveReturnsToTheOverviewAndFocusesTheSavedRulesRow()
    {
        string wizard = ReadSource("Desktop/Views/Rules/RulesPage.Wizard.cs");
        string page = ReadSource("Desktop/Views/Rules/RulesPage.axaml.cs");
        string overview = ReadSource("Desktop/Views/Rules/RulesPage.Overview.cs");
        // The Save button goes through the wizard save; the plain save stays what the overview toggle and the smokes call.
        Assert.Contains("await SaveAndReturnToOverviewAsync();", Between(page, "private async void OnSaveRule(", "private void OnGoToInvalidField("));
        string save = Between(wizard, "internal async Task<bool> SaveAndReturnToOverviewAsync()", "private void FocusFirstWizardField()");
        // Only a catalog-accepted save closes the wizard; a refusal, conflict or store error returns false and stays in step 2/3.
        Assert.Contains("bool saved = await SaveCurrentAsync();", save);
        Assert.Contains("if (!saved ||", save);
        // The confirmation is the overview's polite status for the saved scope, and focus returns to that scope's row (new rule: new row),
        // not to the row the wizard was opened from.
        Assert.Contains("_overviewStatus = (\"rules.overview.saved\", FormatScope(scope));", save);
        Assert.Contains("_wizardReturnScope = scope;", save);
        Assert.True(save.IndexOf("_wizardReturnScope = scope;", StringComparison.Ordinal) < save.IndexOf("CloseWizard();", StringComparison.Ordinal));
        Assert.True(save.IndexOf("_overviewStatus =", StringComparison.Ordinal) < save.IndexOf("CloseWizard();", StringComparison.Ordinal));
        Assert.Contains("FocusOverviewRow(target);", Between(wizard, "internal void CloseWizard()", "internal async Task<bool> SaveAndReturnToOverviewAsync()"));
        Assert.Contains("_overviewStatus is { } status ? Texts.T(status.Key, status.Scope)", overview);
        // Success is reported only after the store accepted the rule; every refusal path of SaveCurrentAsync returns false.
        string plain = Between(page, "internal async Task<bool> SaveCurrentAsync()", "internal void DiscardCurrentDraft()");
        Assert.Equal(1, plain.Split("saveSucceeded = true;").Length - 1);
        Assert.Contains("return saveSucceeded;", plain);
        Assert.DoesNotContain("return true;", plain);
        // Discard keeps its behaviour: it does not leave the wizard.
        Assert.DoesNotContain("CloseWizard", Between(page, "internal void DiscardCurrentDraft()", "private async void OnSaveRule("));
    }

    [Fact]
    public void TheSavedConfirmationIsTranslatedAndNamesTheRule()
    {
        string original = Texts.Language;
        try
        {
            string[] languages = { "en", "de", "es", "fr", "ru", "zh-Hans", "ja", "ko", "pt-BR", "cs", "it", "pl" };
            foreach (string language in languages)
            {
                Texts.SetLanguage(language);
                string text = Texts.T("rules.overview.saved", "4chan/g");
                Assert.Contains("4chan/g", text);
                Assert.NotEqual("rules.overview.saved", text);
                if (language != "en") Assert.NotEqual("Rule for 4chan/g saved. Applies to new decisions.", text);
            }
            Texts.SetLanguage("en");
            Assert.Equal("Rule for 4chan/g saved. Applies to new decisions.", Texts.T("rules.overview.saved", "4chan/g"));
            Texts.SetLanguage("de");
            Assert.Equal("Regel für 4chan/g gespeichert. Gilt für neue Entscheidungen.", Texts.T("rules.overview.saved", "4chan/g"));
        }
        finally { Texts.SetLanguage(original); }
    }

    [Fact]
    public void EditorCloseSitsAtTheRightEdgeOfItsHeaderRowApartFromActions()
    {
        string xaml = ReadSource("Desktop/Views/Rules/RulesPage.axaml");
        string header = Between(xaml, "<Grid x:Name=\"RuleWizardHeader\"", "</Grid>");
        Assert.Contains("ColumnDefinitions=\"*,Auto,Auto\"", header);
        Assert.Matches(@"x:Name=""RuleWizardCloseButton""[^>]*Grid\.Column=""2""", header);
        // Save/Discard stay in the footer, never next to Close.
        Assert.DoesNotContain("SaveRuleButton", header);
        Assert.DoesNotContain("DiscardRuleButton", header);
    }

    [Fact]
    public void CountedHeadingsUseTheLanguagesPluralForms()
    {
        string original = Texts.Language;
        try
        {
            Texts.SetLanguage("en");
            Assert.Equal("1 unsaved draft", Texts.Count("rules.overview.draftsCount", 1));
            Assert.Equal("3 saved rules", Texts.Count("rules.overview.savedCount", 3));
            Texts.SetLanguage("pl");
            Assert.Equal("2 zapisane reguły", Texts.Count("rules.overview.savedCount", 2));
            Assert.Equal("5 zapisanych reguł", Texts.Count("rules.overview.savedCount", 5));
            Texts.SetLanguage("ru");
            Assert.Equal("3 несохранённых черновика", Texts.Count("rules.overview.draftsCount", 3));
            Texts.SetLanguage("cs");
            Assert.Equal("1 uložené pravidlo", Texts.Count("rules.overview.savedCount", 1));
            Texts.SetLanguage("de");
            Assert.Equal("1 ungespeicherter Entwurf", Texts.Count("rules.overview.draftsCount", 1));
            Assert.Equal("Regel für 4chan/g löschen?", Texts.T("rules.overview.deleteTitle", "4chan/g"));
        }
        finally { Texts.SetLanguage(original); }
    }

    private static void InEnglish(Action test)
    {
        string original = Texts.Language;
        try { Texts.SetLanguage("en"); test(); }
        finally { Texts.SetLanguage(original); }
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, end);
        return source[from..to];
    }

    private static string ReadSource(string path)
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, path);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }
        throw new FileNotFoundException(path);
    }
}
