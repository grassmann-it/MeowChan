using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan.DesktopSmoke;

// Overview first (14.11.2026): the page opens on its overview; Create opens the wizard at step 1 with focus on its first field,
// Edit opens step 2, Close sits at the editor header's right edge and returns focus to the overview, Esc closes too.
internal static partial class RulesPageChecks
{
    private static async Task CheckOverviewFirstAsync(MainWindow window, RulesPage page)
    {
        if (page.IsWizardOpen) page.CloseWizard();
        await FlushAsync();
        Control overview = page.FindControl<Control>("RulesOverviewPanel")!;
        Control wizard = page.FindControl<Control>("RuleWizardPanel")!;
        Button create = page.FindControl<Button>("CreateRuleButton")!;
        await WaitLayoutAsync(window, () => overview.IsEffectivelyVisible && create.IsEffectivelyVisible);
        Require(page.IsOverviewVisible && !wizard.IsEffectivelyVisible && !page.FindControl<Control>("RulesFooterActions")!.IsEffectivelyVisible
            && page.FindControl<Control>("RulesOverviewDefaults")!.IsEffectivelyVisible,
            "The Rules page did not open on its overview with the built-in defaults and without editor actions.");
        Require(page.CurrentOverview.IsEmpty == page.FindControl<Control>("RulesOverviewEmpty")!.IsVisible,
            "The overview's empty state disagrees with its saved rules and drafts.");
        Program.CaptureWindow(window, "smoke-rules-overview.png");

        Click(create);
        await WaitLayoutAsync(window, () => page.IsWizardOpen && FocusWithin(window, page.FindControl<Control>("NewRuleScopeKindInput")!));
        Require(page.WizardStep == RuleWizardStep.Scope && !page.FindControl<Button>("SaveRuleButton")!.IsEffectivelyVisible
            && page.FindControl<Button>("OpenNewRuleScopeButton")!.IsEffectivelyVisible && page.EditingLabel == Texts.T("rules.wizard.new"),
            "Create rule did not open step 1 (scope) with Next and without Save.");
        Control header = page.FindControl<Control>("RuleWizardHeader")!;
        Button close = page.FindControl<Button>("RuleWizardCloseButton")!;
        double headerRight = header.TranslatePoint(new Point(header.Bounds.Width, 0), window)!.Value.X;
        double closeRight = close.TranslatePoint(new Point(close.Bounds.Width, 0), window)!.Value.X;
        Require(Math.Abs(headerRight - closeRight) <= 2, $"Close is not at the editor header's right edge: header ends {headerRight:0.#}, Close ends {closeRight:0.#}.");
        Program.CaptureWindow(window, "smoke-rules-wizard-scope.png");
        Click(close);
        await WaitLayoutAsync(window, () => page.IsOverviewVisible && FocusWithin(window, create));

        page.EditScope(MediaRuleScope.Global);
        await FlushAsync();
        Require(page.IsWizardOpen && page.WizardStep == RuleWizardStep.Settings && page.ActiveScope == MediaRuleScope.Global,
            "Editing a scope did not open the wizard at its settings step.");
        Click(page.FindControl<Button>("RuleWizardNextButton")!);
        await WaitLayoutAsync(window, () => page.WizardStep == RuleWizardStep.Review && page.FindControl<Control>("RulePreviewSection")!.IsEffectivelyVisible);
        Program.CaptureWindow(window, "smoke-rules-wizard-review.png");
        Click(page.FindControl<Button>("RuleWizardBackButton")!);
        await WaitLayoutAsync(window, () => page.WizardStep == RuleWizardStep.Settings && page.FindControl<Control>("RuleEnabledInput")!.IsEffectivelyVisible);
        KeyEventArgs escape = new() { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
        page.FindControl<Control>("RuleEnabledInput")!.RaiseEvent(escape);
        await FlushAsync();
        Require(escape.Handled && page.IsOverviewVisible, "Esc did not close the rule wizard.");
        Console.WriteLine("SMOKE rules overview: opens on the overview, Create opens step 1 with focus, Close at the header's right edge, Edit opens step 2, Esc closes.");
    }

    // Tom's decision 14.11.2026: Save closes the wizard onto the overview, a polite status says "Rule for ... saved", focus lands on the
    // saved rule's row (a new rule: its new row). Failed saves stay in the wizard (ThemeMigrationChecks asserts the refused case).
    private static async Task CheckSaveReturnedToOverviewAsync(MainWindow window, RulesPage page, MediaRuleScope scope)
    {
        string label = MeowChan.Desktop.Plugins.PluginGrantPresentation.ScopeLabel(scope);
        await WaitLayoutAsync(window, () => page.IsOverviewVisible && !page.IsWizardOpen
            && window.FocusManager?.GetFocusedElement() is Button { Name: "RuleOverview-edit" } focused
            && Avalonia.Automation.AutomationProperties.GetName(focused).EndsWith(label, StringComparison.Ordinal));
        Require(page.StatusMessage == Texts.T("rules.overview.saved", label)
            && page.FindControl<Control>("RuleStatusText")!.IsEffectivelyVisible
            && Avalonia.Automation.AutomationProperties.GetLiveSetting(page.FindControl<Control>("RuleStatusText")!) == Avalonia.Automation.AutomationLiveSetting.Polite,
            "The overview did not confirm the saved rule in its polite live status: " + page.StatusMessage);
        Require(page.CurrentOverview.Groups.SelectMany(group => group.Rows).Any(row => row.Scope == scope && row.Saved),
            "The saved rule has no row on the overview.");
    }

    private static void RequireTemplatedLabel(Button button, string expected) =>
        Require(button.ContentTemplate != null && button.Content as string == expected,
            $"{button.Name} lost its icon template or translated action label.");
}
