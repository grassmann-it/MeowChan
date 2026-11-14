using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan;

// Overview first (14.11.2026): the page opens on the rule overview; Create and Edit open this in-page wizard instead of a dialog,
// like the Sources and Plugins details. The wizard stays open across page switches, so a draft and its step survive exactly
// as the single editor did before; every draft lives in the page's draft table, never in the wizard state.
public partial class RulesPage
{
    private readonly RuleWizardState _wizard = new();
    // The overview row (or Create button) that opened the wizard; Close returns focus there.
    private MediaRuleScope? _wizardReturnScope;
    private bool _wizardFocusClaimed;

    public bool IsWizardOpen => _wizard.IsOpen;
    public bool IsOverviewVisible => !_wizard.IsOpen;
    internal RuleWizardStep WizardStep => _wizard.Step;
    public string WizardStepLabel => !_wizard.IsOpen ? string.Empty
        : Texts.T("rules.wizard.stepOf", (int)_wizard.Step, RuleWizardState.StepCount, StepTitle(_wizard.Step));

    private static string StepTitle(RuleWizardStep step) => Texts.T(step switch
    {
        RuleWizardStep.Scope => "rules.wizard.stepScope",
        RuleWizardStep.Settings => "rules.wizard.stepSettings",
        _ => "rules.wizard.stepReview"
    });

    /// <summary>The wizard's heading: "New rule" until a scope is chosen, then "New rule: …" or "Edit rule: …".</summary>
    public string EditingLabel => !_wizard.HasScope ? Texts.T("rules.wizard.new")
        : Texts.T(FindRule(_confirmed, _draft.Scope) == null ? "rules.wizard.newScope" : "rules.wizard.edit", FormatScope(_draft.Scope));

    /// <summary>"Create rule" (and "Start from template"): step 1 with the scope fields; nothing is drafted until a scope is chosen.</summary>
    internal void OpenNewRuleWizard(bool revealTemplates = false)
    {
        if (_ended || !IsEditable) return;
        DeactivateRuleDecisions();
        _wizardReturnScope = null;
        _wizard.OpenNew(revealTemplates);
        _statusKey = null; _overviewStatus = null;
        ClearOperationError();
        RebuildRuleSuggestions();
        RaiseAll();
        FocusTemplateControl(NewRuleScopeKindInput, () => _wizard.IsOpen && _wizard.Step == RuleWizardStep.Scope);
    }

    /// <summary>Opens <paramref name="scope"/> in step 2 from the overview and moves focus to the step's first field.</summary>
    private void OpenRuleFromOverview(MediaRuleScope scope)
    {
        if (_ended || !IsEditable) return;
        _overviewStatus = null;
        EditScope(scope);
        if (!_wizard.IsOpen || _draft.Scope != scope) return;
        _wizardReturnScope = scope;
        FocusFirstWizardField();
    }

    /// <summary>Shows <paramref name="step"/> when it is reachable (steps 2 and 3 need a chosen scope).</summary>
    internal bool ShowWizardStep(RuleWizardStep step, bool focus = true)
    {
        if (_ended || !_wizard.IsOpen) return false;
        if (step == RuleWizardStep.Scope && _wizard.Step != step) RebuildRuleSuggestions();
        bool changed = _wizard.GoTo(step);
        if (changed) { DeactivateRuleDecisions(); RaiseAll(); }
        if (focus && _wizard.Step == step) FocusFirstWizardField();
        return _wizard.Step == step;
    }

    /// <summary>Back to the overview. The draft stays (discard is its own decision); focus returns to its row.</summary>
    internal void CloseWizard()
    {
        if (_ended || !_wizard.IsOpen) return;
        RuleExtensionsInput.FlushPendingText();
        DeactivateRuleDecisions();
        MediaRuleScope? target = _wizardReturnScope ?? (_wizard.HasScope ? _draft.Scope : null);
        _wizard.Close();
        _wizardReturnScope = null;
        _statusKey = null;
        RaiseAll();
        FocusOverviewRow(target);
    }

    /// <summary>
    /// Save from the wizard. A successful save closes the wizard onto the overview, which confirms it ("Rule for … saved", a polite
    /// live status like Enable/Disable) and focuses the saved rule's row (a new rule: its new row). A refused or failed save (validation,
    /// revision conflict, store error) stays in the wizard exactly as before, with its field focus and error.
    /// </summary>
    internal async Task<bool> SaveAndReturnToOverviewAsync()
    {
        MediaRuleScope scope = _draft.Scope;
        bool saved = await SaveCurrentAsync();
        if (!saved || _ended || !_wizard.IsOpen || _draft.Scope != scope) return saved;
        _overviewStatus = ("rules.overview.saved", FormatScope(scope));
        _wizardReturnScope = scope; // focus goes to the saved rule, not to the row the wizard was opened from
        CloseWizard();
        return true;
    }

    private void FocusFirstWizardField()
    {
        // "Start from template" already sent focus to the template list of step 2.
        if (_wizardFocusClaimed) { _wizardFocusClaimed = false; return; }
        Control target = _wizard.Step switch
        {
            RuleWizardStep.Scope => NewRuleScopeKindInput,
            RuleWizardStep.Settings => RuleEnabledInput,
            _ => RulePreviewHeading
        };
        RuleWizardStep step = _wizard.Step;
        FocusTemplateControl(target, () => _wizard.IsOpen && _wizard.Step == step);
    }

    private void OnCreateRule(object? sender, RoutedEventArgs e) => OpenNewRuleWizard();
    private void OnStartFromTemplate(object? sender, RoutedEventArgs e) => OpenNewRuleWizard(revealTemplates: true);
    private void OnCloseWizard(object? sender, RoutedEventArgs e) => CloseWizard();
    private void OnWizardBack(object? sender, RoutedEventArgs e)
    {
        if (_wizard.Step == RuleWizardStep.Scope) return;
        ShowWizardStep(_wizard.Step == RuleWizardStep.Review ? RuleWizardStep.Settings : RuleWizardStep.Scope);
    }
    private void OnWizardNext(object? sender, RoutedEventArgs e)
    {
        RuleExtensionsInput.FlushPendingText();
        if (_wizard.Step == RuleWizardStep.Settings) ShowWizardStep(RuleWizardStep.Review);
    }
    private void OnWizardStepClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int step) && step is >= 1 and <= RuleWizardState.StepCount)
        {
            RuleExtensionsInput.FlushPendingText();
            ShowWizardStep((RuleWizardStep)step);
        }
    }

    /// <summary>Called when a scope was opened for editing (overview, context action, origin or breadcrumb navigation).</summary>
    private void EnterWizardForScope()
    {
        _wizard.OpenEdit();
        if (_wizard.RevealTemplates) { _wizard.RevealTemplates = false; RevealTemplatesInStep(); }
    }

    private void RevealTemplatesInStep()
    {
        _templatesRevealed = true;
        _stateOpened.Remove("templates");
        RuleTemplatesExpander.IsExpanded = true;
        _wizardFocusClaimed = true;
        FocusTemplateControl(_templateList, () => _wizard.IsOpen && _wizard.Step == RuleWizardStep.Settings);
    }

    private void RefreshWizardPresentation()
    {
        if (_ended) return;
        bool open = _wizard.IsOpen;
        RuleWizardStep step = _wizard.Step;
        RuleWizardScopeStep.IsVisible = open && step == RuleWizardStep.Scope;
        RuleWizardSettingsStep.IsVisible = open && step == RuleWizardStep.Settings;
        RuleWizardReviewStep.IsVisible = open && step == RuleWizardStep.Review;
        RuleWizardBackButton.IsVisible = open && step != RuleWizardStep.Scope;
        RuleWizardNextButton.IsVisible = open && step == RuleWizardStep.Settings;
        OpenNewRuleScopeButton.IsVisible = open && step == RuleWizardStep.Scope;
        // Discard and Save belong to the chosen scope's draft: steps 2 and 3. In step 1 nothing is drafted yet.
        bool draftSteps = open && step != RuleWizardStep.Scope;
        DiscardRuleButton.IsVisible = draftSteps;
        SaveRuleButton.IsVisible = draftSteps;
        RuleWizardBackButton.IsEnabled = IsEditable;
        RuleWizardNextButton.IsEnabled = IsEditable;
        foreach ((Button chip, RuleWizardStep value) in new[]
        {
            (RuleWizardStepScope, RuleWizardStep.Scope), (RuleWizardStepSettings, RuleWizardStep.Settings), (RuleWizardStepReview, RuleWizardStep.Review)
        })
        {
            bool current = open && step == value;
            bool attention = value == RuleWizardStep.Review && _wizard.HasScope && PreviewNeedsAttention;
            // The mark is a word, not a colour: a review step with an invalid field or a changed saved rule says so on the chip.
            chip.Content = Texts.T("rules.wizard.stepChip", (int)value, StepTitle(value))
                + (attention ? " · " + Texts.T("rules.wizard.needsAttention") : string.Empty);
            chip.Classes.Set("chip-selected", current);
            chip.IsEnabled = IsEditable && _wizard.CanGoTo(value);
            string name = Texts.T("rules.wizard.stepOf", (int)value, RuleWizardState.StepCount, StepTitle(value))
                + (current ? " · " + Texts.T("rules.wizard.currentStep") : string.Empty)
                + (attention ? " · " + Texts.T("rules.wizard.needsAttention") : string.Empty);
            AutomationProperties.SetName(chip, name);
            ToolTip.SetTip(chip, name);
        }
    }

    private void ApplyWizardLabels()
    {
        ApplyIconLabel(CreateRuleButton, "plus", Texts.T("rules.overview.create"));
        ApplyIconLabel(CreateFirstRuleButton, "plus", Texts.T("rules.overview.create"));
        ApplyIconLabel(StartFromTemplateButton, "copy", Texts.T("rules.overview.startFromTemplate"));
        ApplyIconLabel(RuleWizardBackButton, "arrow-left", Texts.T("rules.wizard.back"));
        ApplyIconLabel(RuleWizardNextButton, "arrow-right", Texts.T("rules.wizard.review"));
        ApplyIconLabel(OpenNewRuleScopeButton, "arrow-right", Texts.T("rules.wizard.next"));
        ApplyIconLabel(RuleWizardCloseButton, "x", Texts.T("rules.wizard.close"));
        CommandBindings.EnsureIconBeforeText(RuleGoToInvalidFieldButton, "crosshair"); // Its label is bound (GoToFieldLabel); only the icon is added.
        AutomationProperties.SetName(RuleWizardCloseButton, Texts.T("rules.wizard.closeName"));
        ToolTip.SetTip(RuleWizardCloseButton, Texts.T("rules.wizard.closeName") + " · Esc");
    }

    /// <summary>An action without a command of its own: catalog icon before the translated label, the label as accessible name.</summary>
    private static void ApplyIconLabel(Button button, string icon, string label)
    {
        if (button.ContentTemplate == null) CommandBindings.ShowIconBeforeText(button, icon);
        button.Content = label;
        AutomationProperties.SetName(button, label);
    }
}
