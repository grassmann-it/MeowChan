using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;
using MeowChan.Rules;

namespace MeowChan;

// Work views (plan AP5, WV6, §1.8): what the view folds or moves under More. Data, validation, the heading of the edited
// scope and Save/Discard are never reduced; every folded block opens by itself while its state needs attention (R6).
public partial class RulesPage
{
    // A page without a main window (fixtures, tests) shows everything, as it did before work views; the window sets its own view.
    private WorkView _workView = WorkView.Advanced;
    private bool _templatesRevealed;
    // Blocks that were opened because of their state; the state opening them again only after it cleared (a user's close wins meanwhile).
    private readonly HashSet<string> _stateOpened = new(StringComparer.Ordinal);

    internal WorkView WorkView => _workView;

    /// <summary>The window applied <paramref name="view"/>: the folded blocks return to their default (R13); nothing is saved.</summary>
    internal void ApplyWorkView(WorkView view, bool viewChanged)
    {
        if (_ended) return;
        bool changed = viewChanged || view != _workView;
        _workView = view;
        if (changed) { _templatesRevealed = false; _stateOpened.Clear(); }
        RefreshWorkViewPresentation(changed);
    }

    private bool PluginFilterActive => _draft.ImageFilter.Mode != MediaImageFilterMode.Inherit;
    // Step 3 is never folded; a validation error or a changed saved rule names itself on the step chip instead (R6).
    private bool PreviewNeedsAttention => HasValidationError || HasPreviewBlocked;
    private bool TemplatesNeedAttention => _templateError != null || IsTemplateDecisionOpen;

    private void RefreshWorkViewPresentation(bool viewChanged = false)
    {
        if (_ended) return;
        WorkView view = _workView;
        string scope = _draft.Scope.ToString();
        WorkViewPresenter.ApplyExpander(RuleSavedScopesExpander, view, viewChanged);
        WorkViewGate.Apply(RuleBreadcrumbRow, view);
        WorkViewGate.Apply(OpenTypesOriginButton, view, CanOpenTypesOrigin);
        WorkViewGate.Apply(OpenSizeOriginButton, view, CanOpenSizeOrigin);
        ApplyStateful(RulePluginExpander, "filter|" + scope, viewChanged, PluginFilterActive);
        RefreshTemplatesPresentation(view, viewChanged);
        RefreshPluginFilterHeader();
        WorkViewMoreSurface.Configure(RulesMoreButton, MoreEntries().Count);
    }

    // R6: a block whose state needs attention opens when that state appears and on entering a view; between those the user's choice stays.
    private void ApplyStateful(Expander expander, string key, bool viewChanged, bool nonDefaultState)
    {
        bool fresh = nonDefaultState && _stateOpened.Add(key);
        if (!nonDefaultState) _stateOpened.Remove(key);
        WorkViewPresenter.ApplyExpander(expander, _workView, viewChanged, fresh || viewChanged && nonDefaultState);
    }

    private void RefreshTemplatesPresentation(WorkView view, bool viewChanged)
    {
        bool needed = _templatesRevealed || TemplatesNeedAttention;
        WorkViewGate.Apply(RuleTemplatesCard, view, nonDefaultState: needed);
        bool fresh = needed && _stateOpened.Add("templates");
        if (!needed) _stateOpened.Remove("templates");
        Presence normal = WorkViewPolicy.Resolve("rules.templates", view);
        if (viewChanged) RuleTemplatesExpander.IsExpanded = normal is Presence.Direct or Presence.Required || needed;
        else if (fresh) RuleTemplatesExpander.IsExpanded = true;
        RuleTemplatesHeader.Text = Texts.T("templates.title");
    }

    // The block's own title; "active" or "disabled" says what a folded block holds (R6: an active filter is visible in the header).
    private void RefreshPluginFilterHeader()
    {
        string state = _draft.ImageFilter.Mode switch
        {
            MediaImageFilterMode.Plugin => Texts.Count("workview.activeCount", 1),
            MediaImageFilterMode.Disabled => Texts.T("plugins.filterDisabled"),
            _ => string.Empty
        };
        RulePluginHeader.Text = Texts.T("plugins.ruleFilter") + (state.Length > 0 ? " · " + state : string.Empty);
    }

    /// <summary>What this view keeps under More right now (also the count the button announces).</summary>
    private List<WorkViewMoreSurface.Entry> MoreEntries()
    {
        List<WorkViewMoreSurface.Entry> entries = new();
        WorkView view = _workView;
        if (WorkViewPolicy.Resolve("rules.scope.breadcrumb", view) == Presence.Overflow && CanOpenParent)
            entries.Add(new(WorkViewPolicy.Get("rules.scope.breadcrumb").OverflowOrder, "rules.open-parent", () => ParentScopeLabel,
                () => OnOpenParentScope(this, new RoutedEventArgs()), () => CanOpenParent));
        foreach ((Button button, string id, Func<string> label) in new[]
        {
            (OpenTypesOriginButton, "rules.preview.origin-types", (Func<string>)(() => OpenTypesOriginLabel)),
            (OpenSizeOriginButton, "rules.preview.origin-size", () => OpenSizeOriginLabel)
        })
            if (WorkViewPolicy.Resolve(id, view) == Presence.Overflow && WorkViewElement.GetStateVisible(button))
                entries.Add(new(WorkViewPolicy.Get(id).OverflowOrder, "rules.open-origin", label,
                    () => CommandBindings.InvokeAction(button), () => button.IsEnabled));
        if (WorkViewPolicy.Resolve("rules.templates", view) == Presence.Overflow && !RuleTemplatesCard.IsVisible)
            entries.Add(new(WorkViewPolicy.Get("rules.templates").OverflowOrder, null, () => Texts.T("templates.title"),
                RevealTemplates, () => IsEditable));
        return entries;
    }

    private void OnRulesMoreClick(object? sender, RoutedEventArgs e) =>
        WorkViewMoreSurface.Open(RulesMoreButton, MoreEntries());

    /// <summary>The "Templates" entry of More: shows the block for this view (until the view changes) and moves focus to its list.</summary>
    internal void RevealTemplates()
    {
        if (_ended || !IsEditable) return;
        if (_wizard.IsOpen && _wizard.HasScope) ShowWizardStep(RuleWizardStep.Settings, focus: false);
        _templatesRevealed = true;
        _stateOpened.Remove("templates");
        RefreshWorkViewPresentation();
        FocusTemplateControl(_templateList, () => _templatesRevealed);
    }

    private void InitializeWorkViewPresentation()
    {
        // The narrow layout of the rule rows reaches rows inside a block only once its content exists: re-apply on opening.
        foreach (Expander expander in new[] { RuleSavedScopesExpander, RulePluginExpander })
            expander.PropertyChanged += (_, args) =>
            {
                if (args.Property == Expander.IsExpandedProperty && !_ended)
                    Dispatcher.UIThread.Post(() => ApplyRuleRowLayout(), DispatcherPriority.Loaded);
            };
    }
}
