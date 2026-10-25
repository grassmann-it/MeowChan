using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Commands;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan;
public partial class RulesPage
{
    private Func<Task<RuleTemplateLoadResult>>? _loadTemplates;
    private Func<IEnumerable<RuleTemplate>, string, Task<RuleTemplateSet>>? _saveTemplates;
    private RuleTemplateSet? _templates;
    private string? _templateError;
    private string? _templateWarning;
    private bool _templateLoading;
    private bool _templateApplyBusy;
    private Guid? _selectedTemplate;
    private TemplatePreview? _templatePreview;
    private TemplateEditorDraft? _templateEditor;
    private bool _renderingTemplates;
    private ComboBox? _templateList;
    private TextBlock? _templateCatalogErrorView, _templateCatalogWarningView, _templateEmptyView, _templateUndoView;
    private TextBlock? _templateTitleView, _templateHintView, _templatePreviewHeading;
    private Button? _templateReloadButton;
    private readonly Dictionary<string, Button> _templateActionButtons = new();
    private StackPanel? _templatePreviewHost, _templateEditorHost;
    private string? _templateListPresentation;
    private TemplatePreview? _renderedTemplatePreview;
    private string? _renderedPreviewLanguage;
    private TemplateEditorDraft? _renderedTemplateEditor;
    private long _renderedTemplateEditorVersion = -1;
    private string? _renderedEditorLanguage;
    private Control? _templatePreviewReturnFocus;
    private long _ruleDecisionGeneration;
    private DraftDiscardConfirmation? _templateDeleteConfirmation;
    private DraftDiscardConfirmation? _templateEditorConfirmation;
    private sealed record TemplatePreview(Guid Id, string CatalogRevision, RuleDraft Target, long Generation, string RuleRevision, long? PluginRevision, RuleDraft After);

    internal void AttachTemplateServices(Func<Task<RuleTemplateLoadResult>> load, Func<IEnumerable<RuleTemplate>, string, Task<RuleTemplateSet>> save)
    { _loadTemplates = load; _saveTemplates = save; RefreshTemplatesPanel(); if (IsEditable) _ = LoadTemplatesAsync(); }
    internal bool IsTemplateDecisionOpen => _templateEditor != null || _templatePreview != null || _templateDeleteConfirmation?.IsOpen == true;
    internal bool IsRuleConfirmationOpen => _discardConfirmation.IsOpen
        || _templateDeleteConfirmation?.IsOpen == true || _templateEditorConfirmation?.IsOpen == true;
    internal bool TryDismissRuleDecision()
    {
        if (_templateEditorConfirmation?.IsOpen == true) { _templateEditorConfirmation.Close(true); return true; }
        if (_templateDeleteConfirmation?.IsOpen == true) { _templateDeleteConfirmation.Close(true); return true; }
        if (_discardConfirmation.IsOpen) { _discardConfirmation.Close(true); return true; }
        if (_templateEditor != null) { RequestCancelTemplateEditor(); return true; }
        if (_templatePreview != null) { CancelTemplatePreview(); return true; }
        return false;
    }
    internal bool CanExecuteTemplateCommand(string id) => IsEditable && !_templateLoading && !_templateApplyBusy && !IsRuleConfirmationOpen && (id switch
    {
        "rules.template-create" => _templates != null && _templateEditor == null,
        "rules.template-edit" or "rules.template-delete" or "rules.template-preview" => _templates != null && _selectedTemplate != null && _templateEditor == null,
        "rules.template-apply" => PreviewIsCurrent(),
        "rules.template-undo-apply" => _draft.TemplateUndo != null && !_draft.Stale && _templateEditor == null,
        _ => false
    });
    internal async Task ExecuteTemplateCommandAsync(string id)
    {
        if (!CanExecuteTemplateCommand(id)) return;
        switch (id)
        {
            case "rules.template-create": OpenTemplateEditor(null); break;
            case "rules.template-edit": OpenTemplateEditor(SelectedTemplate()); break;
            case "rules.template-delete": RequestDeleteTemplate(); break;
            case "rules.template-preview": await PreviewTemplateAsync(); break;
            case "rules.template-apply": await ApplyTemplateAsync(); break;
            case "rules.template-undo-apply": UndoTemplateApply(); break;
        }
    }
    internal void CancelTemplatePreview()
    {
        if (_templatePreview == null) return;
        long decisionGeneration = _ruleDecisionGeneration;
        _templatePreview = null; RefreshTemplatesPanel();
        FocusTemplateControl(_templatePreviewReturnFocus ?? TemplateAction("preview"), () => decisionGeneration == _ruleDecisionGeneration && _templatePreview == null && _templateEditor == null);
    }
    private Button? TemplateAction(string id) => _templateActionButtons.GetValueOrDefault(id);
    private void FocusTemplateControl(Control? control, Func<bool>? current = null)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_ended || current?.Invoke() == false || control == null || !control.IsAttachedToVisualTree()
                || !control.IsEffectivelyVisible || !control.IsEnabled) return;
            control.BringIntoView(); control.Focus();
        }, DispatcherPriority.Loaded);
    }
    internal void DeactivateRuleDecisions()
    {
        _ruleDecisionGeneration++;
        _discardConfirmation.Close(); _templateDeleteConfirmation?.Close(); _templateEditorConfirmation?.Close();
        _templatePreview = null;
    }
    private RuleTemplate? SelectedTemplate() => _templates?.Templates.FirstOrDefault(t => t.Id == _selectedTemplate);
    private async Task LoadTemplatesAsync()
    {
        if (_ended || _templateLoading || _loadTemplates == null || !IsEditable) return;
        _templateLoading = true; _templatePreview = null; RefreshTemplatesPanel();
        try
        {
            RuleTemplateLoadResult loaded = await _loadTemplates();
            if (_ended) return;
            _templates = loaded.Catalog; _templateWarning = loaded.Warning; _templateError = null;
            if (_selectedTemplate != null && !_templates.Templates.Any(t => t.Id == _selectedTemplate)) _selectedTemplate = null;
        }
        catch (Exception exception) { if (!_ended) { _templates = null; _templateError = exception.Message; } }
        finally { _templateLoading = false; if (!_ended) RefreshTemplatesPanel(); }
    }
    private void RefreshTemplatesPanel()
    {
        if (_ended || _renderingTemplates || this.FindControl<StackPanel>("RuleTemplatesPanel") is not { } panel) return;
        _renderingTemplates = true;
        try
        {
            // Retain the list, actions and field controls while selection, busy state or errors change.
            // Replacing focused controls here used to strand keyboard focus on a detached element.
            if (_templateList == null)
            {
                _templateTitleView = TemplateText(string.Empty, true); panel.Children.Add(_templateTitleView);
                _templateHintView = TemplateText(string.Empty); panel.Children.Add(_templateHintView);
                WrapPanel top = new() { ItemSpacing = 8, LineSpacing = 6 }; panel.Children.Add(top);
                _templateReloadButton = AddTemplateButton(top, "templates.reload", async () => await LoadTemplatesAsync());
                _templateCatalogErrorView = TemplateText(string.Empty); _templateCatalogErrorView.Classes.Add("workspace-error"); _templateCatalogErrorView.Name = "RuleTemplateCatalogError";
                _templateCatalogErrorView.Focusable = true; panel.Children.Add(_templateCatalogErrorView);
                _templateCatalogWarningView = TemplateText(string.Empty); _templateCatalogWarningView.Classes.Add("workspace-warning"); panel.Children.Add(_templateCatalogWarningView);
                _templateEmptyView = TemplateText(string.Empty); panel.Children.Add(_templateEmptyView);
                _templateList = new ComboBox { Name = "RuleTemplateList", HorizontalAlignment = HorizontalAlignment.Stretch };
                _templateList.SelectionChanged += (_, _) =>
                {
                    if (_renderingTemplates) return;
                    _selectedTemplate = (_templateList.SelectedItem as TemplateChoice)?.Id;
                    _templatePreview = null; RefreshTemplatesPanel();
                };
                panel.Children.Add(_templateList);
                WrapPanel actions = new(); actions.Classes.Add("workspace-actions"); panel.Children.Add(actions);
                foreach (string id in new[] { "create", "edit", "delete", "preview", "undo-apply" })
                {
                    string command = "rules.template-" + id;
                    Button button = AddTemplateButton(actions, "templates." + id, async () => await ExecuteTemplateCommandAsync(command));
                    _templateActionButtons.Add(id, button);
                    if (id == "delete") { button.Classes.Add("danger"); _templateDeleteConfirmation = new(button); }
                }
                _templateUndoView = TemplateText(string.Empty); panel.Children.Add(_templateUndoView);
                _templatePreviewHost = new StackPanel { Spacing = 8 }; panel.Children.Add(_templatePreviewHost);
                _templateEditorHost = new StackPanel { Spacing = 8 }; panel.Children.Add(_templateEditorHost);
            }
            _templateTitleView!.Text = Texts.T("templates.title"); _templateHintView!.Text = Texts.T("templates.hint");
            _templateReloadButton!.Content = Texts.T("templates.reload");
            _templateReloadButton.IsEnabled = !_templateLoading && IsEditable && _loadTemplates != null;
            _templateCatalogErrorView!.Text = _templateError == null ? string.Empty : Texts.T("templates.failed", _templateError);
            _templateCatalogErrorView.IsVisible = _templateError != null;
            _templateCatalogWarningView!.Text = _templateWarning == null ? string.Empty : Texts.T("templates.warning", _templateWarning);
            _templateCatalogWarningView.IsVisible = _templateWarning != null;
            _templateEmptyView!.Text = Texts.T(_templates == null ? "templates.loadHint" : "templates.empty");
            _templateEmptyView.IsVisible = _templates == null || _templates.Templates.Count == 0;
            _templateList.IsVisible = _templates != null;
            string presentation = (_templates?.Revision ?? "unavailable") + "\0" + Texts.Language + "\0" + _pluginProfile?.Revision;
            if (_templateListPresentation != presentation)
            {
                _templateListPresentation = presentation;
                _templateList.ItemsSource = _templates?.Templates.Select(t => new TemplateChoice(t.Id, t.Name + " · " + TemplateGroups(t)
                    + (TemplateCompatibilityError(t) == null ? "" : " · " + Texts.T("templates.incompatible")))).ToArray() ?? Array.Empty<TemplateChoice>();
            }
            _templateList.SelectedItem = (_templateList.ItemsSource as TemplateChoice[])?.FirstOrDefault(t => t.Id == _selectedTemplate);
            AutomationProperties.SetName(_templateList, Texts.T("templates.title"));
            foreach ((string id, Button button) in _templateActionButtons)
            {
                CommandBindings.Apply(button, "rules.template-" + id);
                button.IsEnabled = CanExecuteTemplateCommand("rules.template-" + id);
            }
            _templateUndoView!.Text = Texts.T("templates.undoUnavailable"); _templateUndoView.IsVisible = _draft.TemplateUndoInvalidated;
            if (!ReferenceEquals(_renderedTemplatePreview, _templatePreview) || _renderedPreviewLanguage != Texts.Language)
            {
                _renderedTemplatePreview = _templatePreview; _renderedPreviewLanguage = Texts.Language;
                _templatePreviewHost!.Children.Clear(); _templatePreviewHeading = null;
                if (_templatePreview != null) RenderTemplatePreview(_templatePreviewHost);
            }
            if (!ReferenceEquals(_renderedTemplateEditor, _templateEditor) || _renderedTemplateEditorVersion != (_templateEditor?.ViewVersion ?? -1)
                || _renderedEditorLanguage != Texts.Language)
            {
                _renderedTemplateEditor = _templateEditor; _renderedTemplateEditorVersion = _templateEditor?.ViewVersion ?? -1;
                _renderedEditorLanguage = Texts.Language; _templateEditorHost!.Children.Clear();
                if (_templateEditor != null) RenderTemplateEditor(_templateEditorHost);
            }
            UpdateTemplateEditorPresentation();
        }
        finally { _renderingTemplates = false; }
    }
    private sealed record TemplateChoice(Guid Id, string Label) { public override string ToString() => Label; }
    private static TextBlock TemplateText(string text, bool heading = false)
    {
        TextBlock label = new() { Text = text, TextWrapping = TextWrapping.Wrap, Focusable = heading };
        if (heading) label.Classes.Add("section-title");
        return label;
    }
    private static Button AddTemplateButton(Panel panel, string key, Func<Task> action, bool enabled = true)
    {
        Button button = new() { Name = key.Replace('.', '-'), Content = Texts.T(key), IsEnabled = enabled };
        AutomationProperties.SetName(button, Texts.T(key)); button.Click += async (_, _) => await action(); panel.Children.Add(button); return button;
    }
    private string TemplateGroups(RuleTemplate template) => string.Join(", ", new[] { template.Types == null ? null : TypesLabel,
        template.SizeLimit == null ? null : SizeLabel, template.ImageFilter == null ? null : Texts.T("plugins.ruleFilter") }.Where(v => v != null));
    private string? TemplateCompatibilityError(RuleTemplate template)
    {
        if (template.ImageFilter is not { Mode: MediaImageFilterMode.Plugin } filter) return null;
        var plugin = _pluginProfile?.Plugins.FirstOrDefault(p => p.PluginId == filter.PluginId && p.Enabled && p.Active != null);
        FilterDescription? description = plugin?.Active?.Description.Filters.FirstOrDefault(f => f.Key == filter.FilterKey);
        if (description == null || description.Settings.Version != filter.SchemaVersion) return Texts.T("templates.incompatible");
        PluginSettingsDraft parameters = CreateRuleParametersDraft(description.Settings, filter.Parameters);
        parameters.Snapshot(out IReadOnlyList<FieldValidationError> errors);
        return errors.Count == 0 ? null : string.Join("; ", errors.Select(e => e.Message));
    }
    private async Task PreviewTemplateAsync()
    {
        RuleTemplate? template = SelectedTemplate();
        if (template == null || _templates == null || !IsEditable || _draft.Stale) return;
        RuleExtensionsInput.FlushPendingText();
        Control? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        _templatePreviewReturnFocus = focused != null && focused.GetVisualAncestors().Contains(this) ? focused : TemplateAction("preview");
        string? incompatible = TemplateCompatibilityError(template);
        if (incompatible != null) { _templateError = incompatible; RefreshTemplatesPanel(); FocusTemplateControl(_templateCatalogErrorView); return; }
        RuleDraft target = _draft; long generation = target.Generation; string revision = _templates.Revision; long? plugins = _pluginProfile?.Revision;
        RuleDraft after = target.DeepClone(); ReplaceTemplateGroups(after, template);
        string? validation = await ValidateTemplateFilterAsync(template);
        if (validation != null) { if (!_ended) { _templateError = validation; RefreshTemplatesPanel(); FocusTemplateControl(_templateCatalogErrorView); } return; }
        if (_ended || !IsEditable || !ReferenceEquals(target, _draft) || target.Generation != generation || _templates?.Revision != revision || _pluginProfile?.Revision != plugins) return;
        _templateError = null; _templatePreview = new(template.Id, revision, target, generation, target.Baseline.Revision, plugins, after);
        RefreshTemplatesPanel();
        TemplatePreview opened = _templatePreview; FocusTemplateControl(_templatePreviewHeading, () => ReferenceEquals(opened, _templatePreview));
    }
    private async Task<string?> ValidateTemplateFilterAsync(RuleTemplate template) =>
        (await ValidateTemplateFilterFieldsAsync(template)).Message;
    private bool PreviewIsCurrent() => _templatePreview is { } p && IsEditable && !_draft.Stale && ReferenceEquals(p.Target, _draft)
        && p.Generation == _draft.Generation && p.RuleRevision == _draft.Baseline.Revision && p.CatalogRevision == _templates?.Revision && p.PluginRevision == _pluginProfile?.Revision;
    private void RenderTemplatePreview(StackPanel panel)
    {
        TemplatePreview preview = _templatePreview!;
        RuleTemplate? template = _templates?.Templates.FirstOrDefault(t => t.Id == preview.Id);
        if (template == null) return;
        _templatePreviewHeading = TemplateText(Texts.T("templates.previewTarget", template.Name, FormatScope(preview.Target.Scope)), true);
        _templatePreviewHeading.Name = "RuleTemplatePreviewHeading"; panel.Children.Add(_templatePreviewHeading);
        panel.Children.Add(TemplateText(Texts.T(preview.Target.Enabled ? "templates.targetEnabled" : "templates.targetDisabled")));
        foreach (string group in new[] { "types", "size", "filter" })
        {
            bool included = group == "types" ? template.Types != null : group == "size" ? template.SizeLimit != null : template.ImageFilter != null;
            string label = group == "types" ? TypesLabel : group == "size" ? SizeLabel : Texts.T("plugins.ruleFilter");
            panel.Children.Add(TemplateText(label + ": " + (included ? OwnGroup(preview.Target, group) + " → " + OwnGroup(preview.After, group) : Texts.T("templates.unchanged"))));
        }
        panel.Children.Add(TemplateText(Texts.T("templates.effectBefore", EffectiveDescription(preview.Target))));
        panel.Children.Add(TemplateText(Texts.T("templates.effectAfter", EffectiveDescription(preview.After))));
        panel.Children.Add(TemplateText(Texts.T("templates.replaceWarning")));
        WrapPanel actions = new(); actions.Classes.Add("workspace-actions"); panel.Children.Add(actions);
        AddTemplateButton(actions, "templates.cancel", () => { CancelTemplatePreview(); return Task.CompletedTask; });
        Button apply = AddTemplateButton(actions, "templates.apply",
            async () => await ExecuteTemplateCommandAsync("rules.template-apply"), CanExecuteTemplateCommand("rules.template-apply"));
        CommandBindings.Apply(apply, "rules.template-apply");
    }
    private string OwnGroup(RuleDraft draft, string group) => group switch
    {
        "types" => TypesModeChoices[(int)draft.TypesMode] + (draft.TypesMode == MediaTypeSelectionMode.Selected
            ? ": " + (draft.ExtensionInput.Tags.Count == 0 && draft.ExtensionInput.Rest.Length == 0 ? Texts.T("rules.noMedia") : string.Join(", ", draft.ExtensionInput.Tags) + " " + draft.ExtensionInput.Rest) : ""),
        "size" => SizeModeChoices[(int)draft.SizeMode] + (draft.SizeMode == MediaSizeLimitMode.Limited ? ": " + draft.Megabytes + " MB" : ""),
        _ => FilterOwnDescription(draft)
    };
    private static string FilterOwnDescription(RuleDraft draft)
    {
        string parameters = draft.FilterParameters == null
            ? string.Join("; ", draft.ImageFilter.Parameters.Select(p => p.Key + "=" + p.Value))
            : string.Join("; ", draft.FilterParameters.Schema.Fields.Select(f => f.Key + "=" + (draft.FilterParameters.IsSet(f.Key)
                ? draft.FilterParameters.Text(f.Key) : Texts.T("plugins.unset"))).Concat(draft.FilterParameters.UnmappedValues.Select(p => p.Key + "=" + p.Value)));
        return draft.ImageFilter.Mode + ": " + draft.ImageFilter.PluginId + "/" + draft.ImageFilter.FilterKey + " @" + draft.ImageFilter.SchemaVersion + " · " + parameters;
    }
    private string EffectiveDescription(RuleDraft draft)
    {
        try
        {
            MediaRule rule = BuildDraft(draft);
            EffectiveMediaRule effect = new MediaRuleResolver(new(draft.Baseline.Revision, draft.Baseline.Rules.Where(r => r.Scope != draft.Scope).Append(rule))).Resolve(draft.Scope);
            return (effect.Types.Mode == MediaTypeSelectionMode.All ? Texts.T("rules.allTypes") : effect.Types.Extensions.Count == 0 ? Texts.T("rules.noMedia") : string.Join(", ", effect.Types.Extensions))
                + " · " + (effect.SizeLimit.Mode == MediaSizeLimitMode.Unlimited ? Texts.T("rules.unlimited") : FormatMegabytes(effect.SizeLimit.Bytes!.Value, Texts.Culture) + " MB")
                + " · " + Texts.T("rules.origin", FormatOrigin(effect.TypesOrigin)) + " / " + FormatOrigin(effect.SizeLimitOrigin);
        }
        catch (RuleInputException) { return Texts.T("templates.unknownEffect"); }
    }
    internal static void ReplaceTemplateGroups(RuleDraft target, RuleTemplate template)
    {
        if (template.Types is { } types) { target.TypesMode = types.Mode; target.ExtensionInput = new(); target.ExtensionInput.Tags.AddRange(types.Extensions); }
        if (template.SizeLimit is { } size) { target.SizeMode = size.Mode; target.Megabytes = size.Bytes.HasValue ? FormatMegabytes(size.Bytes.Value, Texts.Culture) : "10"; target.NumberCulture = Texts.Culture; }
        if (template.ImageFilter is { } filter)
        {
            target.ImageFilter = filter; target.LastPluginFilter = null; target.FilterParameters = null; target.BoundFilterParameters = null;
            target.FilterSchemaAcknowledged = false; target.FilterValidationErrors = Array.Empty<FieldValidationError>();

        }
    }
    private async Task ApplyTemplateAsync()
    {
        RuleExtensionsInput.FlushPendingText(); RefreshConfirmedRules();
        if (_templateApplyBusy) return;
        if (!PreviewIsCurrent()) { _templatePreview = null; RefreshTemplatesPanel(); return; }
        TemplatePreview preview = _templatePreview!;
        _templateApplyBusy = true;
        try
        {
            // Recheck durable catalog and live plugin profile, without writing either store.
            if (_loadTemplates != null)
            {
                RuleTemplateLoadResult loaded = await _loadTemplates();
                if (_ended) return;
                if (loaded.Catalog.Revision != preview.CatalogRevision)
                { _templates = loaded.Catalog; _templateWarning = loaded.Warning; _templatePreview = null; _templateError = Texts.T("templates.previewStale"); return; }
            }
            if (_pluginServices != null)
            {
                var profile = await _pluginServices.ProfileStore.ReadAsync(_filterLifetime.Token);
                if (_ended) return;
                if (profile.Revision != preview.PluginRevision)
                { _pluginProfile = profile; _templatePreview = null; _templateError = Texts.T("templates.previewStale"); return; }
            }
            RuleExtensionsInput.FlushPendingText(); RefreshConfirmedRules();
            if (!PreviewIsCurrent()) { _templatePreview = null; _templateError = Texts.T("templates.previewStale"); return; }
            RuleDraft before = _draft.DeepClone();
            _draft.Restore(preview.After); _draft.Generation++; _draft.Dirty = true; _draft.TemplateUndo = before; _draft.TemplateUndoInvalidated = false;
            _templatePreview = null; _filterRenderedDraft = null; _statusKey = "templates.applied";
            UpdatePreview(); RefreshScopeChoices(); RaiseAll();
            RuleTemplate? applied = _templates?.Templates.FirstOrDefault(t => t.Id == preview.Id);
            if (applied?.Types != null) { RuleTypesModeInput.Focus(); RuleTypesModeInput.BringIntoView(); }
            else if (applied?.SizeLimit != null) { RuleSizeModeInput.Focus(); RuleSizeModeInput.BringIntoView(); }
            else FocusTemplateControl(RulePluginPanel.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Focusable && c.IsEnabled && c.IsEffectivelyVisible));
        }
        catch (Exception exception) { if (!_ended) { _templateError = exception.Message; _templatePreview = null; } }
        finally { _templateApplyBusy = false; if (!_ended) RefreshTemplatesPanel(); }
    }
    private void UndoTemplateApply()
    {
        if (!IsEditable || _draft.Stale || _draft.TemplateUndo is not { } before) return;
        _draft.Restore(before); _draft.Generation++; _draft.TemplateUndo = null; _draft.TemplateUndoInvalidated = false;
        _templatePreview = null; _filterRenderedDraft = null; _statusKey = null; UpdatePreview(); RefreshScopeChoices(); RaiseAll();
    }
    private void RequestDeleteTemplate()
    {
        RuleTemplate? template = SelectedTemplate(); if (template == null || _templates == null || _templateDeleteConfirmation == null) return;
        string revision = _templates.Revision;
        long decisionGeneration = _ruleDecisionGeneration; RuleDraft target = _draft;
        _templateDeleteConfirmation.OpenAsync(template.Name, async () =>
        {
            if (!IsEditable || _templates?.Revision != revision || SelectedTemplate()?.Id != template.Id) return;
            await PublishTemplatesAsync(_templates.Templates.Where(t => t.Id != template.Id), revision);
        }, Texts.T("templates.delete"), context: () => !_ended && IsEditable && IsEffectivelyVisible && decisionGeneration == _ruleDecisionGeneration && ReferenceEquals(target, _draft),
            completedFocus: () => _templates?.Templates.Count > 0 ? _templateList : TemplateAction("create"));
    }
    private async Task<bool> PublishTemplatesAsync(IEnumerable<RuleTemplate> templates, string revision)
    {
        if (_saveTemplates == null || !IsEditable) return false;
        SetBusy(true);
        try { RuleTemplateSet saved = await _saveTemplates(templates.ToArray(), revision); if (_ended) return false; _templates = saved; _templateError = null; return true; }
        catch (Exception exception) { if (!_ended) _templateError = exception.Message; return false; }
        finally { if (!_ended) { SetBusy(false); RefreshTemplatesPanel(); } }
    }
}
