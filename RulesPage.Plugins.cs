using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan;

public partial class RulesPage
{
    private sealed record FilterChoice(string PluginId, FilterDescription Filter, string Label)
    { public override string ToString() => Label; }
    private sealed class GrantTarget { internal string Source = string.Empty; internal string Board = string.Empty; }
    private readonly Dictionary<MediaRuleScope, GrantTarget> _grantTargets = new();
    private PluginManagementServices? _pluginServices;
    private PluginProfileSnapshot? _pluginProfile;
    private RuleDraft? _filterRenderedDraft;
    private PluginSettingsEditor? _filterEditor;
    private bool _filterRefreshing;
    private readonly CancellationTokenSource _filterLifetime = new();
    internal void AttachPluginServices(PluginManagementServices services) => _pluginServices = services;
    internal async Task RefreshPluginFiltersAsync()
    {
        if (_ended || _pluginServices == null) return;
        try
        {
            PluginProfileSnapshot profile = await _pluginServices.ProfileStore.ReadAsync(_filterLifetime.Token);
            if (_ended) return;
            if (_pluginProfile?.Revision != profile.Revision)
                foreach (RuleDraft draft in _drafts.Values)
                    if (draft.TemplateUndo != null) { draft.TemplateUndo = null; draft.TemplateUndoInvalidated = true; }
            _pluginProfile = profile; _filterRenderedDraft = null; RefreshFilterEditor();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_ended) { _operationErrorKey = "rules.loadFailed"; _operationErrorDetail = exception.Message; RaiseAll(); } }
    }
    private void RefreshFilterEditor()
    {
        if (_ended || this.FindControl<StackPanel>("RulePluginPanel") is not { } panel || _filterRefreshing) return;
        if (_filterRenderedDraft == _draft)
        { panel.IsEnabled = IsEditable && RuleEnabled; return; }
        _filterRefreshing = true;
        try
        {
            _filterRenderedDraft = _draft; panel.Children.Clear(); _filterEditor = null;
            panel.IsEnabled = IsEditable && RuleEnabled;
            panel.Children.Add(FilterLabel(Texts.T("plugins.ruleFilter")));
            ComboBox mode = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
                ItemsSource = new[] { Texts.T("plugins.filterInherit"), Texts.T("plugins.filterDisabled"), Texts.T("plugins.filterPlugin") }, SelectedIndex = (int)_draft.ImageFilter.Mode };
            AutomationProperties.SetName(mode, Texts.T("plugins.ruleFilter"));
            mode.SelectionChanged += (_, _) =>
            {
                if (_filterRefreshing || mode.SelectedIndex < 0) return;
                MediaImageFilterMode selected = (MediaImageFilterMode)mode.SelectedIndex;
                if (selected == MediaImageFilterMode.Plugin)
                {
                    FilterChoice? first = FilterChoices().FirstOrDefault();
                    if (_draft.LastPluginFilter != null) _draft.ImageFilter = _draft.LastPluginFilter;
                    else if (first != null) _draft.ImageFilter = new(selected, first.PluginId, first.Filter.Key, first.Filter.Settings.Version);
                    else { _operationErrorKey = "plugins.noFilters"; RaiseAll(); return; }
                }
                else
                {
                    if (_draft.ImageFilter.Mode == MediaImageFilterMode.Plugin) _draft.LastPluginFilter = _draft.ImageFilter;
                    _draft.ImageFilter = selected == MediaImageFilterMode.Inherit ? MediaImageFilterRule.Inherit : MediaImageFilterRule.Disabled;
                }
                _filterRenderedDraft = null; DraftChanged(); RefreshFilterEditor();
            };
            panel.Children.Add(mode);
            panel.Children.Add(FilterLabel(Texts.T("plugins.ruleEffect")));
            if (_draft.ImageFilter.Mode != MediaImageFilterMode.Plugin) return;
            FilterChoice[] choices = FilterChoices();
            FilterChoice? selectedFilter = choices.FirstOrDefault(choice => choice.PluginId == _draft.ImageFilter.PluginId && choice.Filter.Key == _draft.ImageFilter.FilterKey);
            ComboBox select = new() { ItemsSource = choices, SelectedItem = selectedFilter, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
            AutomationProperties.SetName(select, Texts.T("plugins.filterChoice"));
            select.SelectionChanged += (_, _) =>
            {
                if (_filterRefreshing || select.SelectedItem is not FilterChoice choice) return;
                _draft.ImageFilter = new(MediaImageFilterMode.Plugin, choice.PluginId, choice.Filter.Key, choice.Filter.Settings.Version);
                _draft.FilterParameters = null; _draft.FilterValidationErrors = Array.Empty<FieldValidationError>(); _draft.FilterSchemaAcknowledged = false; _filterRenderedDraft = null; DraftChanged(); RefreshFilterEditor();
            };
            panel.Children.Add(select);
            if (selectedFilter == null)
            {
                panel.Children.Add(FilterLabel(Texts.T("plugins.filterMissing", _draft.ImageFilter.PluginId, _draft.ImageFilter.FilterKey)));
                return;
            }
            if (_draft.FilterParameters == null)
            {
                _draft.FilterParameters = CreateRuleParametersDraft(selectedFilter.Filter.Settings, _draft.ImageFilter.Parameters);
                BindFilterParameterChanges(_draft);
            }
            else if (_draft.FilterParameters.Schema.Version != selectedFilter.Filter.Settings.Version)
            {
                _draft.FilterParameters = _draft.FilterParameters.RebindSchema(selectedFilter.Filter.Settings);
                _draft.FilterSchemaAcknowledged = false;
            }
            if (_draft.ImageFilter.SchemaVersion != selectedFilter.Filter.Settings.Version && !_draft.FilterSchemaAcknowledged)
            {
                panel.Children.Add(FilterLabel(Texts.T("plugins.ruleSchemaChanged", _draft.ImageFilter.SchemaVersion, selectedFilter.Filter.Settings.Version)));
                panel.Children.Add(FilterLabel(string.Join("\n", _draft.ImageFilter.Parameters.Select(pair => pair.Key + " = " + pair.Value))));
                Button accept = new() { Content = Texts.T("plugins.acceptSchema") };
                accept.Click += (_, _) => { _draft.FilterSchemaAcknowledged = true; _draft.FilterParameters.AcknowledgeRemovedFields(); _filterRenderedDraft = null; DraftChanged(); };
                panel.Children.Add(accept);
            }
            BindFilterParameterChanges(_draft);
            _filterEditor = new(_draft.FilterParameters, FilterScopeIdentity(_draft.Scope)); _filterEditor.ShowErrors(_draft.FilterValidationErrors); panel.Children.Add(_filterEditor);
            panel.Children.Add(FilterLabel(selectedFilter.Filter.Description));
            panel.Children.Add(FilterLabel(Texts.T("plugins.filterFormats", string.Join(", ", selectedFilter.Filter.SupportedMimeTypes))));
            AddGrantControls(panel, selectedFilter);
        }
        finally { _filterRefreshing = false; }
    }
    private void BindFilterParameterChanges(RuleDraft draft)
    {
        if (draft.FilterParameters == null || ReferenceEquals(draft.BoundFilterParameters, draft.FilterParameters)) return;
        draft.BoundFilterParameters = draft.FilterParameters;
        draft.FilterParameters.Changed += () =>
        {
            draft.FilterValidationErrors = Array.Empty<FieldValidationError>();
            if (ReferenceEquals(draft, _draft)) DraftChanged();
            else { draft.Dirty = true; draft.Generation++; draft.TemplateUndo = null; }
        };
    }
    private FilterChoice[] FilterChoices() => (_pluginProfile?.Plugins ?? Array.Empty<MeowChan.PluginHost.Activation.PluginActivationSnapshot>())
        .Where(plugin => plugin.Active != null).SelectMany(plugin => plugin.Active!.Description.Filters.Select(filter => new FilterChoice(plugin.PluginId, filter,
            plugin.Active.Description.Name + " · " + filter.Name))).OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase).ToArray();
    private static PluginSettingsDraft CreateRuleParametersDraft(SettingsSchema schema, IReadOnlyDictionary<string, string> parameters)
    {
        List<SettingValue> saved = new();
        foreach (SettingDefinition field in schema.Fields)
        {
            if (!parameters.TryGetValue(field.Key, out string? text)) continue;
            if (field.Kind == SettingKind.Integer && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
                saved.Add(new(field.Key, field.Kind, Integer: number));
            else if (field.Kind == SettingKind.Boolean && bool.TryParse(text, out bool boolean)) saved.Add(new(field.Key, field.Kind, Boolean: boolean));
            else if (field.Kind is SettingKind.Text or SettingKind.Enum or SettingKind.Uri) saved.Add(new(field.Key, field.Kind, Text: text));
        }
        PluginSettingsDraft draft = new(schema, SettingScope.FilterRule, new(schema.Version, saved), applyDefaults: false);
        foreach ((string key, string text) in parameters) draft.SetInitialText(key, text);
        return draft;
    }
    private MediaImageFilterRule BuildImageFilter() => BuildImageFilter(_draft);
    private MediaImageFilterRule BuildImageFilter(RuleDraft draft)
    {
        if (draft.ImageFilter.Mode != MediaImageFilterMode.Plugin || draft.FilterParameters == null) return draft.ImageFilter;
        if (draft.ImageFilter.SchemaVersion != draft.FilterParameters.Schema.Version && !draft.FilterSchemaAcknowledged)
            throw new RuleInputException("plugins.invalidFilterSettings");
        SettingsSnapshot settings = draft.FilterParameters.Snapshot(out IReadOnlyList<FieldValidationError> errors);
        draft.FilterValidationErrors = errors.Concat(draft.FilterValidationErrors.Where(error => !errors.Contains(error))).ToArray();
        if (ReferenceEquals(draft, _draft)) _filterEditor?.ShowErrors(draft.FilterValidationErrors);
        if (errors.Count > 0) throw new RuleInputException("plugins.invalidFilterSettings");
        Dictionary<string, string> parameters = settings.Values.ToDictionary(value => value.Key, value => value.Kind switch
        {
            SettingKind.Integer => value.Integer!.Value.ToString(CultureInfo.InvariantCulture),
            SettingKind.Boolean => value.Boolean == true ? "true" : "false",
            _ => value.Text!
        }, StringComparer.Ordinal);
        return new(MediaImageFilterMode.Plugin, draft.ImageFilter.PluginId, draft.ImageFilter.FilterKey, settings.SchemaVersion, parameters);
    }
    private async Task<bool> ValidateRuleFilterAsync(MediaRule rule)
    {
        if (!rule.Enabled || rule.ImageFilter.Mode != MediaImageFilterMode.Plugin) return true;
        MeowChan.PluginHost.Activation.PluginReleaseSnapshot? release = _pluginProfile?.Plugins
            .FirstOrDefault(plugin => plugin.PluginId == rule.ImageFilter.PluginId)?.Active;
        FilterDescription? filter = release?.Description.Filters.FirstOrDefault(filter => filter.Key == rule.ImageFilter.FilterKey);
        if (_pluginServices == null || release == null || filter == null)
        {
            // Existing unavailable references can be retained while other rule fields are edited.
            if (FindRule(_draft.Baseline, _draft.Scope) is { } previous && ImageFiltersEqual(previous.ImageFilter, rule.ImageFilter)) return true;
            _operationErrorKey = "plugins.noFilters"; FocusFilterSettingError(); return false;
        }
        // Never replace the rule's claimed version merely by reconstructing it for validation.
        if (rule.ImageFilter.SchemaVersion != filter.Settings.Version)
        {
            _operationErrorKey = "plugins.invalidFilterSettings"; FocusFilterSettingError(); return false;
        }
        PluginSettingsDraft parameters = CreateRuleParametersDraft(filter.Settings, rule.ImageFilter.Parameters);
        SettingsSnapshot settings = parameters.Snapshot(out IReadOnlyList<FieldValidationError> errors);
        if (errors.Count == 0)
        {
            await using MeowChan.PluginHost.Contracts.IPluginWorkerClient worker = await _pluginServices.StartTestWorkerAsync(release.Artifact, release.Configuration, _filterLifetime.Token);
            // Keep defaults implicit in the rule store, but validate the values production will consume.
            SettingsSnapshot effectiveSettings = PluginSettingsMigration.WithDefaults(filter.Settings, settings);
            errors = await worker.ValidateSettingsAsync(new(SettingScope.FilterRule, effectiveSettings, FilterKey: filter.Key), _filterLifetime.Token);
        }
        if (_ended) return false;
        _draft.FilterValidationErrors = errors;
        if (errors.Count == 0) return true;
        _filterEditor?.ShowErrors(errors);
        _operationErrorKey = "plugins.invalidFilterSettings";
        _operationErrorDetail = string.Join("; ", errors.Select(error => error.Message));
        FocusFilterSettingError();
        return false;
    }
    private static bool ImageFiltersEqual(MediaImageFilterRule first, MediaImageFilterRule second) => first.Mode == second.Mode
        && first.PluginId == second.PluginId && first.FilterKey == second.FilterKey && first.SchemaVersion == second.SchemaVersion
        && first.Parameters.Count == second.Parameters.Count && first.Parameters.All(pair => second.Parameters.TryGetValue(pair.Key, out string? value) && value == pair.Value);
    private static TextBlock FilterLabel(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
    private void AddGrantControls(StackPanel panel, FilterChoice filter)
    {
        panel.Children.Add(FilterLabel(Texts.T("plugins.grantScope", FormatScope(_draft.Scope), filter.Filter.RequiredAccess)));
        if (!_grantTargets.TryGetValue(_draft.Scope, out GrantTarget? target))
            _grantTargets[_draft.Scope] = target = new() { Source = _draft.Scope.Source ?? string.Empty, Board = _draft.Scope.Board ?? string.Empty };
        TextBox source = new() { Text = target.Source, IsReadOnly = _draft.Scope.Source != null,
            PlaceholderText = Texts.T("plugins.grantSource") };
        AutomationProperties.SetName(source, Texts.T("plugins.grantSource")); panel.Children.Add(source);
        TextBox board = new() { Text = target.Board, IsReadOnly = _draft.Scope.Board != null,
            PlaceholderText = Texts.T("plugins.grantBoard") };
        AutomationProperties.SetName(board, Texts.T("plugins.grantBoard")); panel.Children.Add(board);
        TextBlock status = FilterLabel(string.Empty); status.Name = "RuleFilterGrantStatus"; panel.Children.Add(status);
        StackPanel origins = new() { Spacing = 6 }; panel.Children.Add(origins);
        MediaRuleScope ruleScope = _draft.Scope;
        PluginGrantScope Scope() => new(source.Text?.Trim() ?? string.Empty,
            string.IsNullOrWhiteSpace(board.Text) ? null : board.Text.Trim(), FilterScopeIdentity(ruleScope));
        void Status()
        {
            PluginGrantScope scope = Scope();
            bool allowed = _pluginProfile != null && PluginGrantAuthorization.Resolve(_pluginProfile, filter.PluginId, filter.Filter.Key,
                scope.SourceId, scope.BoardId, scope.RuleId).Allows(filter.Filter.RequiredAccess);
            status.Text = Texts.T(allowed ? "plugins.accessGranted" : "plugins.accessMissing");
            origins.Children.Clear();
            foreach (PluginGrantSnapshot grant in (_pluginProfile?.Grants ?? Array.Empty<PluginGrantSnapshot>()).Where(grant =>
                grant.PluginId == filter.PluginId && grant.FilterKey == filter.Filter.Key && grant.Access != FilterDataAccess.None
                && grant.Scope.SourceId == scope.SourceId && (grant.Scope.BoardId == null || grant.Scope.BoardId == scope.BoardId)
                && (grant.Scope.RuleId == null || grant.Scope.RuleId == scope.RuleId)))
            {
                string origin = PluginGrantPresentation.Describe(grant, _confirmed.Rules.Select(rule => rule.Scope).Append(ruleScope), effective: true);
                origins.Children.Add(FilterLabel(origin));
                Button revoke = new() { Content = Texts.T("plugins.revokeGrant"), Tag = grant.Scope };
                AutomationProperties.SetName(revoke, Texts.T("plugins.revokeGrant") + ": " + origin);
                revoke.Click += async (_, _) => await ChangeRuleGrantAsync(filter, grant.Scope, FilterDataAccess.None);
                origins.Children.Add(revoke);
            }
        }
        source.TextChanged += (_, _) => { target.Source = source.Text ?? string.Empty; Status(); };
        board.TextChanged += (_, _) => { target.Board = board.Text ?? string.Empty; Status(); }; Status();
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 6 };
        Button allow = new() { Content = Texts.T("plugins.grantRequired") };
        allow.Click += async (_, _) => await ChangeRuleGrantAsync(filter, Scope(), filter.Filter.RequiredAccess);
        actions.Children.Add(allow); panel.Children.Add(actions);
    }
    private static string FilterScopeIdentity(MediaRuleScope scope) => MeowChan.PluginHost.Filters.FilterRuleIdentity.ForScope(scope);
    private async Task ChangeRuleGrantAsync(FilterChoice filter, PluginGrantScope scope, FilterDataAccess access)
    {
        if (_ended || _busy || _pluginServices == null || _pluginProfile == null) return;
        if (string.IsNullOrWhiteSpace(scope.SourceId)) { _operationErrorKey = "plugins.grantSourceRequired"; RaiseAll(); return; }
        SetBusy(true); ClearOperationError();
        try
        {
            _pluginProfile = await _pluginServices.ProfileStore.SetGrantAsync(filter.PluginId, filter.Filter.Key, scope, access, _pluginProfile.Revision, _filterLifetime.Token);
            if (_ended) return;
            _filterRenderedDraft = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { if (!_ended) { _operationErrorKey = "rules.saveFailed"; _operationErrorDetail = exception.Message; } }
        finally { if (!_ended) { SetBusy(false); RefreshFilterEditor(); } }
    }
}
