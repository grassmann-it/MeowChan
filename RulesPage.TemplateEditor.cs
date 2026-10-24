using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using MeowChan.Rules;

namespace MeowChan;
public partial class RulesPage
{
    /// <summary>Independent template editor, using the same raw field adapters as the rule editor.</summary>
    private sealed class TemplateEditorDraft
    {
        internal Guid Id = Guid.NewGuid();
        internal string Revision = string.Empty;
        internal string Name = string.Empty;
        internal bool IncludeTypes, IncludeSize, IncludeFilter;
        internal MediaTypeSelectionMode TypesMode;
        internal ExtensionTagDraft Extensions = new();
        internal MediaSizeLimitMode SizeMode;
        internal string Megabytes = "10";
        internal System.Globalization.CultureInfo Culture = Texts.Culture;
        internal MediaImageFilterRule Filter = MediaImageFilterRule.Inherit;
        internal PluginSettingsDraft? Parameters;
        internal bool SchemaAcknowledged;
        internal IReadOnlyList<FieldValidationError> ParameterErrors = Array.Empty<FieldValidationError>();
        internal PluginSettingsDraft? BoundParameters;
        internal string? Error;
        internal long ViewVersion;
        internal string Initial = string.Empty;
        internal string Fingerprint => Name + "\0" + IncludeTypes + IncludeSize + IncludeFilter + "\0" + TypesMode + Extensions.Fingerprint
            + "\0" + SizeMode + Megabytes + "\0" + Filter.Mode + Filter.PluginId + Filter.FilterKey + Filter.SchemaVersion
            + "\0" + string.Join("\0", Filter.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + p.Value))
            + "\0" + Parameters?.EditorFingerprint + SchemaAcknowledged;
    }
    private void OpenTemplateEditor(RuleTemplate? template)
    {
        if (_templates == null || !IsEditable) return;
        RuleExtensionsInput.FlushPendingText();
        Control? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        _templateEditorReturnFocus = focused != null && focused.GetVisualAncestors().Contains(this) ? focused : TemplateAction(template == null ? "create" : "edit");
        TemplateEditorDraft editor = new() { Revision = _templates.Revision };
        if (template == null)
        {
            editor.TypesMode = _draft.TypesMode; editor.Extensions = _draft.ExtensionInput.Clone();
            editor.SizeMode = _draft.SizeMode; editor.Megabytes = _draft.Megabytes; editor.Culture = _draft.NumberCulture;
            editor.Filter = _draft.ImageFilter; editor.Parameters = _draft.FilterParameters?.DeepClone(); editor.SchemaAcknowledged = _draft.FilterSchemaAcknowledged;
        }
        else
        {
            editor.Id = template.Id; editor.Name = template.Name;
            editor.IncludeTypes = template.Types != null; editor.IncludeSize = template.SizeLimit != null; editor.IncludeFilter = template.ImageFilter != null;
            editor.TypesMode = template.Types?.Mode ?? MediaTypeSelectionMode.Inherit; editor.Extensions.Tags.AddRange(template.Types?.Extensions ?? Array.Empty<string>());
            editor.SizeMode = template.SizeLimit?.Mode ?? MediaSizeLimitMode.Inherit;
            editor.Megabytes = template.SizeLimit?.Bytes is long bytes ? FormatMegabytes(bytes, editor.Culture) : "10";
            editor.Filter = template.ImageFilter ?? MediaImageFilterRule.Inherit;
        }
        InitializeTemplateParameters(editor);
        editor.Initial = editor.Fingerprint; _templateEditor = editor; _templatePreview = null;
        RefreshTemplatesPanel();
        FocusTemplateControl(_templateEditorView?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault(c => c.Name == "RuleTemplateName"), () => ReferenceEquals(editor, _templateEditor));
    }
    private void ReformatTemplateEditor()
    {
        if (_templateEditor is not { } editor) return;
        bool clean = editor.Fingerprint == editor.Initial;
        if (TryReadMegabytes(editor.Megabytes, editor.Culture, out long bytes))
        { editor.Megabytes = FormatMegabytes(bytes, Texts.Culture); editor.Culture = Texts.Culture; }
        if (clean) editor.Initial = editor.Fingerprint;
    }
    private void InitializeTemplateParameters(TemplateEditorDraft editor)
    {
        if (editor.Filter.Mode != MediaImageFilterMode.Plugin) return;
        FilterChoice? choice = FilterChoices().FirstOrDefault(f => f.PluginId == editor.Filter.PluginId && f.Filter.Key == editor.Filter.FilterKey);
        if (editor.Parameters == null && choice != null)
            editor.Parameters = CreateRuleParametersDraft(choice.Filter.Settings, editor.Filter.Parameters);
        if (editor.Parameters is not { } parameters || ReferenceEquals(editor.BoundParameters, parameters)) return;
        editor.BoundParameters = parameters;
        editor.ParameterErrors = Array.Empty<FieldValidationError>();
        parameters.Changed += () =>
        {
            if (!ReferenceEquals(editor.Parameters, parameters)) return;
            editor.ParameterErrors = Array.Empty<FieldValidationError>();
            if (ReferenceEquals(editor, _templateEditor) && _templateParameterEditor is { } boundEditor && ReferenceEquals(boundEditor.Draft, parameters))
            {
                boundEditor.ShowErrors(editor.ParameterErrors);
                if (_templateEditorErrorField == "RuleTemplateParameters")
                { editor.Error = null; UpdateTemplateEditorPresentation(); }
            }
        };
    }
    private Control? _templateEditorReturnFocus;
    private StackPanel? _templateEditorView;
    private TextBlock? _templateEditorErrorView, _templateEditorStaleView;
    private Button? _templateEditorSaveButton;
    private string? _templateEditorErrorField;
    private PluginSettingsEditor? _templateParameterEditor;
    private void UpdateTemplateEditorPresentation()
    {
        if (_templateEditor is not { } editor || _templateEditorView == null) return;
        _templateEditorView.IsEnabled = IsEditable;
        _templateEditorErrorView!.Text = editor.Error ?? string.Empty; _templateEditorErrorView.IsVisible = editor.Error != null;
        _templateEditorStaleView!.Text = Texts.T("templates.editorStale"); _templateEditorStaleView.IsVisible = _templates?.Revision != editor.Revision;
        _templateEditorSaveButton!.IsEnabled = IsEditable && _templates?.Revision == editor.Revision;
    }
    private void FocusTemplateEditorError(TemplateEditorDraft editor)
    {
        long decision = _ruleDecisionGeneration;
        string fingerprint = editor.Fingerprint;
        PluginSettingsDraft? parameters = editor.Parameters;
        long generation = parameters?.EditGeneration ?? -1;
        bool Current() => !_busy && decision == _ruleDecisionGeneration && ReferenceEquals(editor, _templateEditor)
            && ReferenceEquals(parameters, editor.Parameters) && generation == (parameters?.EditGeneration ?? -1) && fingerprint == editor.Fingerprint;
        PluginSettingsEditor? parameterEditor = _templateParameterEditor;
        Control? parameterTarget = _templateEditorErrorField == "RuleTemplateParameters" && parameterEditor != null && ReferenceEquals(parameterEditor.Draft, parameters)
            ? parameters?.Schema.Fields.Select(field => editor.ParameterErrors.Any(error => error.FieldKey == field.Key)
                ? parameterEditor.FindField(field.Key) : null).FirstOrDefault(field => field != null) : null;
        // Parameter errors share the measured input+local-error reveal used by plugin/source/rule forms.
        // Preserve the template decision/draft/edit fingerprint checks in the deferred focus callback.
        if (parameterTarget != null && parameterEditor != null && parameterEditor.TryFocusFirstError(Current)) return;
        Control? target = parameterTarget ?? (_templateEditorErrorField == null ? _templateEditorErrorView : _templateEditorView?.GetVisualDescendants()
            .OfType<Control>().FirstOrDefault(c => c.Name == _templateEditorErrorField));
        if (target is RuleExtensionsInput tags)
        {
            Dispatcher.UIThread.Post(() => { if (!_ended && Current() && tags.IsEffectivelyVisible) { tags.BringIntoView(); tags.FocusInput(); } }, DispatcherPriority.Loaded);
        }
        else FocusTemplateControl(target ?? _templateEditorErrorView, Current);
    }
    private void RenderTemplateEditor(StackPanel parent)
    {
        TemplateEditorDraft editor = _templateEditor!;
        _templateParameterEditor = null;
        StackPanel panel = new() { Spacing = 8, IsEnabled = IsEditable }; _templateEditorView = panel; parent.Children.Add(new Border { Child = panel, Padding = new Avalonia.Thickness(12), BorderThickness = new Avalonia.Thickness(1) });
        panel.Children.Add(TemplateText(Texts.T("templates.editor"), true));
        TextBox name = new() { Name = "RuleTemplateName", Text = editor.Name, MaxLength = 120, PlaceholderText = Texts.T("templates.name") };
        AutomationProperties.SetName(name, Texts.T("templates.name")); name.TextChanging += (_, _) => editor.Name = name.Text ?? string.Empty; panel.Children.Add(name);
        AddTemplateInclude(panel, "templates.includeTypes", editor.IncludeTypes, value => editor.IncludeTypes = value);
        ComboBox types = new() { Name = "RuleTemplateTypesMode", ItemsSource = TypesModeChoices, SelectedIndex = (int)editor.TypesMode, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(types, TypesLabel); types.SelectionChanged += (_, _) => { if (types.SelectedIndex >= 0) editor.TypesMode = (MediaTypeSelectionMode)types.SelectedIndex; }; panel.Children.Add(types);
        RuleExtensionsInput tags = new() { Name = "RuleTemplateExtensions" }; tags.Bind(editor.Extensions); panel.Children.Add(tags);
        AddTemplateInclude(panel, "templates.includeSize", editor.IncludeSize, value => editor.IncludeSize = value);
        ComboBox sizes = new() { Name = "RuleTemplateSizeMode", ItemsSource = SizeModeChoices, SelectedIndex = (int)editor.SizeMode, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(sizes, SizeLabel); sizes.SelectionChanged += (_, _) => { if (sizes.SelectedIndex >= 0) editor.SizeMode = (MediaSizeLimitMode)sizes.SelectedIndex; }; panel.Children.Add(sizes);
        TextBox megabytes = new() { Name = "RuleTemplateMegabytes", Text = editor.Megabytes }; AutomationProperties.SetName(megabytes, MegabytesLabel);
        megabytes.TextChanging += (_, _) => { editor.Megabytes = megabytes.Text ?? string.Empty; editor.Culture = Texts.Culture; }; panel.Children.Add(megabytes);
        AddTemplateInclude(panel, "templates.includeFilter", editor.IncludeFilter, value => editor.IncludeFilter = value);
        ComboBox modes = new() { Name = "RuleTemplateFilterMode", ItemsSource = new[] { Texts.T("plugins.filterInherit"), Texts.T("plugins.filterDisabled"), Texts.T("plugins.filterPlugin") }, SelectedIndex = (int)editor.Filter.Mode,
            HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(modes, Texts.T("plugins.ruleFilter"));
        modes.SelectionChanged += (_, _) =>
        {
            if (modes.SelectedIndex < 0 || modes.SelectedIndex == (int)editor.Filter.Mode) return;
            MediaImageFilterMode mode = (MediaImageFilterMode)modes.SelectedIndex;
            if (mode == MediaImageFilterMode.Plugin)
            {
                FilterChoice? choice = FilterChoices().FirstOrDefault();
                if (choice == null) { editor.Error = Texts.T("plugins.noFilters"); _templateEditorErrorField = "RuleTemplateFilterMode"; RefreshTemplatesPanel(); FocusTemplateEditorError(editor); return; }
                editor.Filter = new(mode, choice.PluginId, choice.Filter.Key, choice.Filter.Settings.Version);
                editor.Parameters = null; InitializeTemplateParameters(editor);
            }
            else { editor.Filter = mode == MediaImageFilterMode.Inherit ? MediaImageFilterRule.Inherit : MediaImageFilterRule.Disabled; editor.Parameters = null; }
            editor.ViewVersion++; RefreshTemplatesPanel();
            FocusTemplateControl(_templateEditorView?.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == "RuleTemplateFilterMode"), () => ReferenceEquals(editor, _templateEditor));
        }; panel.Children.Add(modes);
        if (editor.Filter.Mode == MediaImageFilterMode.Plugin)
        {
            panel.Children.Add(TemplateText(editor.Filter.PluginId + "/" + editor.Filter.FilterKey + " @" + editor.Filter.SchemaVersion));
            FilterChoice[] choices = FilterChoices();
            ComboBox filters = new() { Name = "RuleTemplateFilterChoice", ItemsSource = choices, SelectedItem = choices.FirstOrDefault(f => f.PluginId == editor.Filter.PluginId && f.Filter.Key == editor.Filter.FilterKey), HorizontalAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(filters, Texts.T("plugins.filterChoice"));
            filters.SelectionChanged += (_, _) =>
            {
                if (filters.SelectedItem is not FilterChoice choice) return;
                if (editor.Filter.PluginId == choice.PluginId && editor.Filter.FilterKey == choice.Filter.Key) return;
                editor.Filter = new(MediaImageFilterMode.Plugin, choice.PluginId, choice.Filter.Key, choice.Filter.Settings.Version);
                editor.Parameters = null; editor.SchemaAcknowledged = false; InitializeTemplateParameters(editor); editor.ViewVersion++; RefreshTemplatesPanel();
                FocusTemplateControl(_templateEditorView?.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(c => c.Name == "RuleTemplateFilterChoice"), () => ReferenceEquals(editor, _templateEditor));
            }; panel.Children.Add(filters);
            if (editor.Parameters != null)
            {
                if (editor.Filter.SchemaVersion != editor.Parameters.Schema.Version || editor.Parameters.UnmappedValues.Count > 0)
                {
                    panel.Children.Add(TemplateText(Texts.T("plugins.ruleSchemaChanged", editor.Filter.SchemaVersion, editor.Parameters.Schema.Version)));
                    AddTemplateButton(panel, "plugins.acceptSchema", () => { editor.SchemaAcknowledged = true; editor.Parameters.AcknowledgeRemovedFields(); editor.ViewVersion++; RefreshTemplatesPanel(); FocusTemplateEditorError(editor); return Task.CompletedTask; });
                }
                _templateParameterEditor = new PluginSettingsEditor(editor.Parameters, "template:" + editor.Id.ToString("N"));
                _templateParameterEditor.ShowErrors(editor.ParameterErrors);
                panel.Children.Add(_templateParameterEditor);
            }
            else panel.Children.Add(TemplateText(Texts.T("templates.incompatible")));
            // Always retain and display every original parameter, even with no installed plugin/schema.
            foreach (var pair in editor.Filter.Parameters) panel.Children.Add(TemplateText(pair.Key + " = " + pair.Value));
            panel.Children.Add(TemplateText(Texts.T("templates.noGrants")));
        }
        _templateEditorErrorView = TemplateText(editor.Error ?? string.Empty); _templateEditorErrorView.Name = "RuleTemplateEditorError";
        _templateEditorErrorView.Focusable = true; _templateEditorErrorView.IsVisible = editor.Error != null; panel.Children.Add(_templateEditorErrorView);
        _templateEditorStaleView = TemplateText(Texts.T("templates.editorStale")); _templateEditorStaleView.IsVisible = _templates?.Revision != editor.Revision;
        panel.Children.Add(_templateEditorStaleView);
        WrapPanel actions = new() { ItemSpacing = 8, LineSpacing = 6 }; panel.Children.Add(actions);
        Button cancel = AddTemplateButton(actions, "templates.cancel", () => { RequestCancelTemplateEditor(); return Task.CompletedTask; });
        _templateEditorConfirmation = new(cancel);
        _templateEditorSaveButton = AddTemplateButton(actions, "templates.save", async () => await SaveTemplateEditorAsync(), IsEditable && _templates?.Revision == editor.Revision);
    }
    private static void AddTemplateInclude(StackPanel panel, string key, bool value, Action<bool> set)
    {
        CheckBox check = new() { Name = key.Replace('.', '-'), Content = Texts.T(key), IsChecked = value }; check.IsCheckedChanged += (_, _) => set(check.IsChecked == true); panel.Children.Add(check);
    }
    private void RequestCancelTemplateEditor()
    {
        if (_templateEditor is not { } editor || !IsEditable) return;
        void Close() { if (ReferenceEquals(editor, _templateEditor)) { _templateEditor = null; RefreshTemplatesPanel(); FocusTemplateControl(_templateEditorReturnFocus ?? TemplateAction("create"), () => _templateEditor == null); } }
        if (editor.Fingerprint == editor.Initial) Close();
        else _templateEditorConfirmation?.Open(editor.Name.Length == 0 ? Texts.T("templates.editor") : editor.Name, Close);
    }
    private async Task SaveTemplateEditorAsync()
    {
        if (_templateEditor is not { } editor || !IsEditable || _templates == null || editor.Revision != _templates.Revision) return;
        long decision = _ruleDecisionGeneration;
        RuleDraft target = _draft;
        bool Current() => !_ended && decision == _ruleDecisionGeneration && ReferenceEquals(target, _draft) && ReferenceEquals(editor, _templateEditor);
        editor.Error = null;
        _templateEditorErrorField = "RuleTemplateName";
        try
        {
            string trimmedName = editor.Name.Trim();
            if (trimmedName.Length is < 1 or > 120 || trimmedName.Any(char.IsControl))
                throw new ArgumentException(Texts.T("templates.name"));
            _templateEditorErrorField = "RuleTemplateExtensions";
            MediaTypeSelection? types = editor.IncludeTypes ? editor.Extensions.Build(editor.TypesMode) : null;
            _templateEditorErrorField = "RuleTemplateMegabytes";
            MediaSizeLimit? size = null;
            if (editor.IncludeSize)
            {
                long? bytes = null;
                if (editor.SizeMode == MediaSizeLimitMode.Limited)
                {
                    if (!TryReadMegabytes(editor.Megabytes, editor.Culture, out long parsed)) throw new ArgumentException(Texts.T("rules.invalidSize"));
                    bytes = parsed;
                }
                size = new(editor.SizeMode, bytes);
            }
            _templateEditorErrorField = "RuleTemplateFilterMode";
            MediaImageFilterRule? filter = editor.IncludeFilter ? editor.Filter : null;
            if (filter?.Mode == MediaImageFilterMode.Plugin)
            {
                if (editor.Parameters == null) throw new ArgumentException(Texts.T("templates.incompatible"));
                if (filter.SchemaVersion != editor.Parameters.Schema.Version && !editor.SchemaAcknowledged) throw new ArgumentException(Texts.T("plugins.invalidFilterSettings"));
                SettingsSnapshot settings = editor.Parameters.Snapshot(out var errors);
                editor.ParameterErrors = errors;
                _templateParameterEditor?.ShowErrors(errors);
                if (errors.Count > 0) { _templateEditorErrorField = "RuleTemplateParameters"; throw new ArgumentException(string.Join("; ", errors.Select(e => e.Message))); }
                filter = new(MediaImageFilterMode.Plugin, filter.PluginId, filter.FilterKey, settings.SchemaVersion,
                    settings.Values.ToDictionary(v => v.Key, v => v.Kind == SettingKind.Integer ? v.Integer!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        : v.Kind == SettingKind.Boolean ? v.Boolean == true ? "true" : "false" : v.Text!, StringComparer.Ordinal));
            }
            _templateEditorErrorField = "RuleTemplateName";
            RuleTemplate value = new(editor.Id, editor.Name, types, size, filter);
            string fingerprint = editor.Fingerprint;
            long? pluginRevision = _pluginProfile?.Revision;
            PluginSettingsDraft? parameterDraft = editor.Parameters;
            long parameterGeneration = parameterDraft?.EditGeneration ?? -1;
            SetBusy(true);
            TemplateFilterValidation validation;
            try { validation = await ValidateTemplateFilterFieldsAsync(value); }
            finally { if (!_ended) SetBusy(false); }
            if (!Current() || fingerprint != editor.Fingerprint || pluginRevision != _pluginProfile?.Revision
                || !ReferenceEquals(parameterDraft, editor.Parameters) || parameterGeneration != (editor.Parameters?.EditGeneration ?? -1)) return;
            editor.ParameterErrors = validation.Errors;
            _templateParameterEditor?.ShowErrors(validation.Errors);
            if (validation.Message != null) { _templateEditorErrorField = "RuleTemplateParameters"; throw new ArgumentException(validation.Message); }
            var items = _templates.Templates.Where(t => t.Id != value.Id).Append(value).ToArray();
            _ = new RuleTemplateSet(editor.Revision, items); // Name/identity checks before any host operation.
            if (await PublishTemplatesAsync(items, editor.Revision) && ReferenceEquals(editor, _templateEditor))
            { _templateEditor = null; _selectedTemplate = value.Id; RefreshTemplatesPanel(); FocusTemplateControl(TemplateAction("edit"), () => _templateEditor == null); }
            else if (ReferenceEquals(editor, _templateEditor))
            { editor.Error = _templateError; _templateEditorErrorField = null; RefreshTemplatesPanel(); FocusTemplateEditorError(editor); }
        }
        catch (Exception exception) { if (Current()) { editor.Error = exception.Message; RefreshTemplatesPanel(); FocusTemplateEditorError(editor); } }
    }
}
