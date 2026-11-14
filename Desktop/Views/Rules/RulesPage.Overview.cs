using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Rules;

namespace MeowChan;

// The overview the page opens on: saved rules by scope with what each overrides, unsaved drafts (they survive page switches as
// before) and the built-in baseline. Its actions reuse the editor's own paths: Edit/Continue open the wizard, Enable/Disable
// saves the scope's draft through SaveCurrentAsync, Delete saves the catalog without the rule through the same store callback.
public partial class RulesPage
{
    private string? _overviewFingerprint;
    private (string Key, string Scope)? _overviewStatus;
    private readonly Dictionary<(MediaRuleScope Scope, string Action), Button> _overviewButtons = new();
    private readonly List<DraftDiscardConfirmation> _overviewConfirmations = new();

    internal bool IsOverviewConfirmationOpen => _overviewConfirmations.Any(confirmation => confirmation.IsOpen);

    /// <summary>The overview as the model sees it (tests and smokes read the same rows the page renders).</summary>
    internal RuleOverview CurrentOverview => RulesOverviewModel.Build(_confirmed,
        _drafts.Values.Where(draft => draft.Dirty).Select(draft => new RuleOverviewDraft(draft.Scope, DescribeDraft(draft), draft.Stale)),
        FilterDisplayName);

    private string OverviewStatusMessage => !_available || !_hasConfirmed ? Texts.T("rules.unavailable") : _busy ? Texts.T("rules.saving")
        : _overviewStatus is { } status ? Texts.T(status.Key, status.Scope) : Texts.T("rules.changedScopes", _confirmed.Rules.Count);

    /// <summary>The rule a draft would save, without the side effects of BuildDraft (no filter validation, no error display).</summary>
    private static MediaRule? DescribeDraft(RuleDraft draft)
    {
        try
        {
            MediaTypeSelection types = draft.ExtensionInput.Build(draft.TypesMode);
            long? bytes = null;
            if (draft.SizeMode == MediaSizeLimitMode.Limited)
            {
                if (!draft.TryReadBytes(out long value)) return null;
                bytes = value;
            }
            return new MediaRule(draft.Scope, draft.Enabled, types, new MediaSizeLimit(draft.SizeMode, bytes)) { ImageFilter = draft.ImageFilter };
        }
        catch (ArgumentException) { return null; }
    }

    private string FilterDisplayName(MediaImageFilterRule filter) =>
        FilterChoices().FirstOrDefault(choice => choice.PluginId == filter.PluginId && choice.Filter.Key == filter.FilterKey)?.Label
            ?? filter.FilterKey ?? filter.PluginId ?? string.Empty;

    private void RefreshOverview()
    {
        if (_ended || _wizard.IsOpen || IsOverviewConfirmationOpen) return;
        RuleOverview overview = CurrentOverview;
        bool templates = _templates?.Templates.Count > 0;
        string fingerprint = RulesOverviewModel.Fingerprint(overview) + "|" + IsEditable + "|" + _narrowRules + "|" + templates
            + "|" + _available + "|" + _hasConfirmed;
        if (fingerprint == _overviewFingerprint) return;
        _overviewFingerprint = fingerprint;
        (MediaRuleScope Scope, string Action)? focused = FocusedOverviewAction();

        _overviewButtons.Clear();
        foreach (DraftDiscardConfirmation confirmation in _overviewConfirmations) confirmation.Close();
        _overviewConfirmations.Clear();

        bool ready = _available && _hasConfirmed;
        RulesOverviewEmpty.IsVisible = overview.IsEmpty;
        RulesOverviewEmptyText.Text = Texts.T(ready ? "rules.overview.empty" : "rules.unavailable");
        StartFromTemplateButton.IsVisible = ready && templates;
        CreateFirstRuleButton.IsVisible = ready;

        RulesOverviewDrafts.Children.Clear();
        RulesOverviewDrafts.IsVisible = overview.Drafts.Count > 0;
        if (overview.Drafts.Count > 0)
        {
            RulesOverviewDrafts.Children.Add(SectionTitle(Texts.Count("rules.overview.draftsCount", overview.Drafts.Count)));
            RulesOverviewDrafts.Children.Add(Help(Texts.T("rules.overview.draftsHint")));
            foreach (RuleOverviewRow row in overview.Drafts) RulesOverviewDrafts.Children.Add(OverviewCard(row, draftRow: true));
        }

        RulesOverviewSaved.Children.Clear();
        RulesOverviewSaved.IsVisible = overview.SavedCount > 0;
        if (overview.SavedCount > 0)
        {
            RulesOverviewSaved.Children.Add(SectionTitle(Texts.Count("rules.overview.savedCount", overview.SavedCount)));
            foreach (RuleOverviewGroup group in overview.Groups)
            {
                TextBlock heading = new() { Text = group.Title, TextWrapping = TextWrapping.Wrap };
                heading.Classes.Add("workspace-field-label");
                RulesOverviewSaved.Children.Add(heading);
                foreach (RuleOverviewRow row in group.Rows) RulesOverviewSaved.Children.Add(OverviewCard(row, draftRow: false));
            }
        }

        RulesOverviewDefaults.Children.Clear();
        RulesOverviewDefaults.Children.Add(SectionTitle(Texts.T("rules.overview.defaults")));
        Border defaults = new() { Child = Chips(overview.Defaults) };
        defaults.Classes.Add("workspace-card");
        AutomationProperties.SetName(defaults, Texts.T("rules.overview.defaults") + ": " + string.Join(", ", overview.Defaults.Select(chip => chip.Text)));
        RulesOverviewDefaults.Children.Add(defaults);
        RulesOverviewDefaults.Children.Add(Help(Texts.T("rules.overview.defaultsHint")));

        if (focused is { } target) FocusOverviewRow(target.Scope, target.Action);
    }

    private Border OverviewCard(RuleOverviewRow row, bool draftRow)
    {
        bool narrow = _narrowRules;
        Grid grid = new()
        {
            ColumnDefinitions = new ColumnDefinitions(narrow ? "*" : "*,Auto"),
            RowDefinitions = new RowDefinitions(narrow ? "Auto,Auto" : "Auto"),
            ColumnSpacing = 12, RowSpacing = 8
        };
        StackPanel info = new() { Spacing = 4 };
        TextBlock title = new() { Text = row.ScopeLabel, TextWrapping = TextWrapping.Wrap };
        title.Classes.Add("workspace-field-label");
        info.Children.Add(title);
        info.Children.Add(Chips(row.Chips));
        grid.Children.Add(info);

        WrapPanel actions = new()
        {
            ItemSpacing = 8, LineSpacing = 8, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Right
        };
        Grid.SetColumn(actions, narrow ? 0 : 1);
        Grid.SetRow(actions, narrow ? 1 : 0);
        grid.Children.Add(actions);
        MediaRuleScope scope = row.Scope;
        if (row.HasDraft) AddOverviewAction(actions, scope, "continue", "edit-2", Texts.T("rules.overview.continue"), () => OpenRuleFromOverview(scope));
        else AddOverviewAction(actions, scope, "edit", "edit-2", Texts.T("rules.overview.edit"), () => OpenRuleFromOverview(scope));
        if (row.Saved && !row.HasDraft)
            AddOverviewAction(actions, scope, "toggle", "power", Texts.T(row.Enabled ? "rules.overview.disable" : "rules.overview.enable"),
                () => _ = ToggleRuleAsync(scope));
        if (draftRow)
        {
            Button discard = AddOverviewAction(actions, scope, "discard", "rotate-ccw", Texts.T("rules.discard"), () => { });
            DraftDiscardConfirmation confirmation = new(discard);
            _overviewConfirmations.Add(confirmation);
            discard.Click += (_, _) => RequestDiscardDraft(scope, confirmation);
        }
        else
        {
            Button delete = AddOverviewAction(actions, scope, "delete", "x", Texts.T("rules.overview.delete"), () => { });
            DraftDiscardConfirmation confirmation = new(delete);
            _overviewConfirmations.Add(confirmation);
            delete.Click += (_, _) => RequestDeleteRule(scope, confirmation);
        }

        Border card = new() { Child = grid };
        card.Classes.Add("workspace-card");
        AutomationProperties.SetName(card, FormatScope(row.Scope) + ": " + string.Join(", ", row.Chips.Select(chip => chip.Text)));
        return card;
    }

    private Button AddOverviewAction(Panel panel, MediaRuleScope scope, string action, string icon, string label, Action execute)
    {
        Button button = new() { Name = "RuleOverview-" + action, IsEnabled = IsEditable };
        CommandBindings.ShowIconBeforeText(button, icon);
        button.Content = label;
        string name = label + " · " + FormatScope(scope);
        AutomationProperties.SetName(button, name);
        ToolTip.SetTip(button, name);
        button.Click += (_, _) => { if (IsEditable) execute(); };
        panel.Children.Add(button);
        _overviewButtons[(scope, action)] = button;
        return button;
    }

    private static WrapPanel Chips(IEnumerable<RuleChip> chips)
    {
        WrapPanel panel = new() { ItemSpacing = 8, LineSpacing = 4 };
        foreach (RuleChip chip in chips)
        {
            TextBlock text = new() { Text = chip.Text, TextWrapping = TextWrapping.Wrap };
            text.Classes.Add("chip-label");
            Border badge = new() { Child = text, VerticalAlignment = VerticalAlignment.Center };
            badge.Classes.AddRange(new[] { "chip", "chip-badge", chip.Tone switch
            {
                RuleChipTone.Success => "chip-step-done",
                RuleChipTone.Warning => "chip-warning",
                _ => "chip-neutral"
            } });
            panel.Children.Add(badge);
        }
        return panel;
    }

    private static TextBlock SectionTitle(string text)
    {
        TextBlock title = new() { Text = text, TextWrapping = TextWrapping.Wrap };
        title.Classes.Add("section-title");
        return title;
    }

    private static TextBlock Help(string text)
    {
        TextBlock help = new() { Text = text, TextWrapping = TextWrapping.Wrap };
        help.Classes.Add("workspace-help");
        return help;
    }

    private (MediaRuleScope Scope, string Action)? FocusedOverviewAction()
    {
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not Control focused) return null;
        foreach (KeyValuePair<(MediaRuleScope Scope, string Action), Button> entry in _overviewButtons)
            if (ReferenceEquals(entry.Value, focused) || focused.GetVisualAncestors().Contains(entry.Value)) return entry.Key;
        return null;
    }

    /// <summary>Focus back on a row of the overview: the given action, else its Edit/Continue, else Create rule.</summary>
    private void FocusOverviewRow(MediaRuleScope? scope, string action = "edit")
    {
        Control target = CreateRuleButton;
        if (scope != null)
            foreach (string candidate in new[] { action, "edit", "continue" })
                if (_overviewButtons.TryGetValue((scope, candidate), out Button? button)) { target = button; break; }
        FocusTemplateControl(target, () => !_wizard.IsOpen);
    }

    /// <summary>Enable/Disable a saved rule: the scope's clean draft flips Enabled and saves through the editor's own save path.</summary>
    internal async Task ToggleRuleAsync(MediaRuleScope scope)
    {
        if (_ended || !IsEditable || FindRule(_confirmed, scope) is not { } rule) return;
        if (_drafts.TryGetValue(scope, out RuleDraft? existing) && existing.Dirty) { OpenRuleFromOverview(scope); return; }
        if (!SelectScope(scope) || _draft.Dirty) return;
        bool enable = !rule.Enabled;
        RuleEnabled = enable;
        await SaveCurrentAsync();
        if (_ended) return;
        if (_draft.Scope == scope && _draft.Dirty)
        {
            // Refused or failed: the editor shows why, with the flipped value still in the draft.
            EnterWizardForScope();
            _wizardReturnScope = scope;
            RaiseAll();
            if (HasError) FocusTemplateControl(RuleActionError, () => _wizard.IsOpen); else FocusFirstWizardField();
            return;
        }
        _statusKey = null;
        _overviewStatus = (enable ? "rules.overview.enabledStatus" : "rules.overview.disabledStatus", FormatScope(scope));
        RaiseAll();
        FocusOverviewRow(scope, "toggle");
    }

    private void RequestDeleteRule(MediaRuleScope scope, DraftDiscardConfirmation confirmation)
    {
        if (_ended || !IsEditable || FindRule(_confirmed, scope) == null) return;
        bool drafted = _drafts.TryGetValue(scope, out RuleDraft? draft) && draft.Dirty;
        confirmation.OpenAsync(FormatScope(scope), () => DeleteRuleAsync(scope), Texts.T("rules.overview.delete"),
            context: () => !_ended && IsEditable && IsEffectivelyVisible && !_wizard.IsOpen,
            completedFocus: () => CreateRuleButton,
            title: Texts.T("rules.overview.deleteTitle", FormatScope(scope)),
            body: Texts.T(drafted ? "rules.overview.deleteDraftBody" : "rules.overview.deleteBody"),
            cancelLabel: Texts.T("templates.cancel"));
    }

    /// <summary>Saves the catalog without the scope's rule (the same store callback as Save); its draft, if any, goes with it.</summary>
    internal async Task DeleteRuleAsync(MediaRuleScope scope)
    {
        if (_ended || !IsEditable) return;
        RefreshConfirmedRules();
        if (!IsEditable || FindRule(_confirmed, scope) == null) return;
        MediaRule[] proposed = _confirmed.Rules.Where(rule => rule.Scope != scope).ToArray();
        string revision = _confirmed.Revision;
        ClearOperationError();
        _statusKey = null; _overviewStatus = null;
        SetBusy(true);
        try
        {
            MediaRuleSet saved = await _saveRules(proposed, revision);
            if (_ended) return;
            if (_draft.Scope == scope) { _drafts[scope] = new RuleDraft(scope, saved); _draft = _drafts[scope]; }
            else _drafts.Remove(scope);
            AcceptConfirmed(saved);
            _overviewStatus = ("rules.overview.deleted", FormatScope(scope));
        }
        catch (Exception exception)
        {
            if (_ended) return;
            _operationErrorKey = "rules.saveFailed";
            _operationErrorDetail = exception.Message;
            try { AcceptConfirmed(_getRules()); } catch (Exception) { }
        }
        finally
        {
            SetBusy(false);
            if (!_ended) { UpdatePreview(); RefreshScopeChoices(); RaiseAll(); }
        }
    }

    private void RequestDiscardDraft(MediaRuleScope scope, DraftDiscardConfirmation confirmation)
    {
        if (_ended || !IsEditable || !_drafts.TryGetValue(scope, out RuleDraft? draft)) return;
        if (ReferenceEquals(draft, _draft)) RuleExtensionsInput.FlushPendingText();
        if (!draft.HasDiscardableChanges) { DiscardDraft(scope); return; }
        long generation = draft.Generation;
        confirmation.Open(FormatScope(scope), () =>
        {
            if (IsEditable && _drafts.TryGetValue(scope, out RuleDraft? current) && ReferenceEquals(current, draft)
                && current.Generation == generation) DiscardDraft(scope);
        }, context: () => !_ended && IsEditable && IsEffectivelyVisible && !_wizard.IsOpen, completedFocus: () => CreateRuleButton);
    }

    /// <summary>Discards one draft from the overview: the current one through the editor's own discard, any other by reloading it.</summary>
    internal void DiscardDraft(MediaRuleScope scope)
    {
        if (_ended || !IsEditable) return;
        if (scope == _draft.Scope) DiscardCurrentDraft();
        else
        {
            try
            {
                AcceptConfirmed(_getRules());
                if (FindRule(_confirmed, scope) == null) _drafts.Remove(scope);
                else _drafts[scope] = new RuleDraft(scope, _confirmed);
            }
            catch (Exception exception) { _operationErrorKey = "rules.loadFailed"; _operationErrorDetail = exception.Message; }
            UpdatePreview(); RefreshScopeChoices();
        }
        if (_operationErrorKey == null) _overviewStatus = ("rules.overview.discarded", FormatScope(scope));
        RaiseAll();
    }
}
