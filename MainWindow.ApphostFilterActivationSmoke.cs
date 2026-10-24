using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.Rules;

namespace MeowChan;

public partial class MainWindow
{
    // Only the explicitly selected apphost source-activation smoke calls these helpers.
    private const string AuthorFilterId = "meowchan.example.image-dimensions";
    private const string AuthorFilterTemplateName = "Author source production filter";

    private static async Task<string> WriteAuthorFilterSourceAsync(string pluginDir)
    {
        string delivered = Path.Combine(AppContext.BaseDirectory, "plugins", "sources", "ImageDimensionsFilter.cs");
        string installed = Path.Combine(pluginDir, "author filter with spaces.cs");
        RequireWorkflow(File.Exists(delivered) && !File.Exists(installed), "Author filter source is missing or already installed.");
        byte[] bytes = await File.ReadAllBytesAsync(delivered);
        await File.WriteAllBytesAsync(installed, bytes);
        byte[] installedBytes = await File.ReadAllBytesAsync(installed);
        RequireWorkflow(bytes.SequenceEqual(installedBytes), "Author filter source copy differs.");
        return installed;
    }

    private async Task ConfigureAuthorFilterThroughTemplateAsync()
    {
        List<string> checks = new();
        RulesPage page = RulesPage;
        RequireWorkflow(_session.IsPaused && _session.GetThreadWorkSnapshots().Count == 0,
            "Filter UI setup must precede any production watch.");
        RequireWorkflow(NavigateWorkspace(WorkspacePage.Rules), "Rules navigation failed.");
        page.EditScope(MediaRuleScope.Global);
        await page.RefreshPluginFiltersAsync();
        await WaitWorkflowAsync(() => page.IsEditable && page.CanExecuteTemplateCommand("rules.template-create"), "author filter rule editor");
        UpdateLayout();

        ComboBox mode = page.GetVisualDescendants().OfType<ComboBox>().Single(control =>
            AutomationProperties.GetName(control) == Texts.T("plugins.ruleFilter"));
        mode.SelectedIndex = (int)MediaImageFilterMode.Plugin;
        await FlushAuthorFilterUiAsync();
        PluginSettingsEditor ruleEditor = Editor(template: false);
        RequireWorkflow(ruleEditor.Draft.Schema.Version == "1", "Author filter schema differs.");
        SetText(ruleEditor, "minimumWidth", "1.5", isSet: true);
        string initialRevision = _session.MediaRules.Revision;
        await page.SaveCurrentAsync(); // No yield between text and Save: read the actual raw value.
        RequireWorkflow(page.HasError && _session.MediaRules.Revision == initialRevision
            && ((TextBox)ruleEditor.FindField("minimumWidth")!).Text == "1.5",
            "Invalid dynamic filter text was committed or discarded.");
        checks.Add("dynamic-integer-error-no-rule-write");
        SetText(ruleEditor, "minimumWidth", "3", isSet: true);
        SetEnum(ruleEditor, "orientation", "any");
        await page.SaveCurrentAsync();
        MediaImageFilterRule firstRule = SavedFilter();
        RequireWorkflow(!page.HasError && firstRule.PluginId == AuthorFilterId && firstRule.Parameters["minimumWidth"] == "3"
            && firstRule.Parameters["orientation"] == "any", "Dynamic form values did not reach the production rule store.");
        string formRevision = _session.MediaRules.Revision;
        checks.Add("dynamic-form-real-rule-store");

        await page.ExecuteTemplateCommandAsync("rules.template-create");
        await FlushAuthorFilterUiAsync();
        Named<TextBox>("RuleTemplateName").Text = AuthorFilterTemplateName;
        Named<CheckBox>("templates-includeFilter").IsChecked = true;
        PluginSettingsEditor templateEditor = Editor(template: true);
        SetText(templateEditor, "minimumWidth", "5", isSet: true);
        SetText(templateEditor, "minimumHeight", "0", isSet: true);
        SetEnum(templateEditor, "orientation", "landscape");
        SetText(templateEditor, "aspectRatio", "", isSet: false);
        SetText(templateEditor, "aspectRatioTolerance", "0", isSet: true);
        ClickTemplate("templates-save");
        await WaitWorkflowAsync(() => !page.IsBusy && !page.GetVisualDescendants().OfType<TextBox>().Any(control => control.Name == "RuleTemplateName"),
            "author filter template publication");
        RuleTemplate template = (await _session.LoadRuleTemplatesAsync()).Catalog.Templates.Single(value => value.Name == AuthorFilterTemplateName);
        RequireWorkflow(template.Types == null && template.SizeLimit == null && template.ImageFilter != null,
            "Author template changed groups outside its filter.");
        MediaImageFilterRule templateFilter = template.ImageFilter ?? throw new InvalidOperationException("Missing template filter.");
        CheckFinalParameters(templateFilter);
        RequireWorkflow(_session.MediaRules.Revision == formRevision && SavedFilter().Parameters["minimumWidth"] == "3",
            "Publishing a template wrote the live rule.");
        checks.Add("filter-only-template-preserves-explicit-zero-and-unset");

        ComboBox templates = Named<ComboBox>("RuleTemplateList");
        templates.SelectedItem = templates.Items.Cast<object>().Single(value => value.ToString()!.StartsWith(AuthorFilterTemplateName + " ·", StringComparison.Ordinal));
        await page.ExecuteTemplateCommandAsync("rules.template-preview");
        await WaitWorkflowAsync(() => page.CanExecuteTemplateCommand("rules.template-apply"), "author filter template preview");
        RequireWorkflow(_session.MediaRules.Revision == formRevision, "Preview wrote the live rule.");
        await page.ExecuteTemplateCommandAsync("rules.template-apply");
        RequireWorkflow(_session.MediaRules.Revision == formRevision && SavedFilter().Parameters["minimumWidth"] == "3",
            "Template Apply bypassed explicit rule Save.");
        await page.SaveCurrentAsync();
        RequireWorkflow(!page.HasError && _session.MediaRules.Revision != formRevision, "Applied template did not save as a real rule.");
        MediaImageFilterRule finalRule = SavedFilter();
        CheckFinalParameters(finalRule);
        RequireWorkflow(finalRule.Parameters.Count == templateFilter.Parameters.Count
            && templateFilter.Parameters.All(pair => finalRule.Parameters.TryGetValue(pair.Key, out string? value) && value == pair.Value),
            "Template parameters changed between Apply and production rule Save.");
        checks.Add("preview-apply-explicit-rule-save");
        string rulesFile = Path.Combine(_session.ProfileDirectory, "media_rules.json");
        File.WriteAllText(Path.Combine(_session.ProfileDirectory, "apphost-filter-template.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, completed = true, checks, pluginId = finalRule.PluginId, filterKey = finalRule.FilterKey,
            schema = finalRule.SchemaVersion, initialRevision, formRevision, finalRevision = _session.MediaRules.Revision,
            templateId = template.Id, templateName = template.Name, templateParameters = templateFilter.Parameters,
            ruleParameters = finalRule.Parameters, rulesSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(rulesFile))).ToLowerInvariant(),
            scope = "Actual RulesPage dynamic fields and template editor; real session rule/template stores; production download asserted by the enclosing source activation workflow."
        }, new JsonSerializerOptions { WriteIndented = true }));
        RequireWorkflow(NavigateWorkspace(WorkspacePage.Downloads), "Downloads navigation failed after filter setup.");

        MediaImageFilterRule SavedFilter() => _session.MediaRules.Rules.Single(rule => rule.Scope == MediaRuleScope.Global).ImageFilter;
        T Named<T>(string name) where T : Control => page.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        PluginSettingsEditor Editor(bool template) => page.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(editor =>
            editor.Draft.Schema.Fields.Any(field => field.Key == "minimumWidth")
            && (AutomationProperties.GetAutomationId(editor.FindField("minimumWidth")!)?.StartsWith("PluginField:FilterRule:template%3A", StringComparison.Ordinal) == true) == template);
        void ClickTemplate(string name)
        {
            Button button = Named<Button>(name);
            RequireWorkflow(button.IsEffectivelyVisible && button.IsEffectivelyEnabled, "Template action is unavailable: " + name);
            button.Focus(NavigationMethod.Tab); button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }
        static void SetText(PluginSettingsEditor editor, string key, string value, bool isSet)
        {
            string label = editor.Draft.Schema.Fields.Single(field => field.Key == key).Label;
            editor.GetVisualDescendants().OfType<CheckBox>().Single(control =>
                AutomationProperties.GetName(control) == label + ": " + Texts.T("plugins.valueSet")).IsChecked = isSet;
            ((TextBox)editor.FindField(key)!).Text = value;
        }
        static void SetEnum(PluginSettingsEditor editor, string key, string value)
        {
            ComboBox choices = (ComboBox)editor.FindField(key)!;
            choices.SelectedItem = choices.Items.Cast<ComboBoxItem>().Single(choice => Equals(choice.Tag, value));
        }
        static void CheckFinalParameters(MediaImageFilterRule filter)
        {
            RequireWorkflow(filter.Mode == MediaImageFilterMode.Plugin && filter.PluginId == AuthorFilterId && filter.FilterKey == "dimensions"
                && filter.SchemaVersion == "1" && filter.Parameters.Count == 4
                && filter.Parameters["minimumWidth"] == "5" && filter.Parameters["minimumHeight"] == "0"
                && filter.Parameters["orientation"] == "landscape" && filter.Parameters["aspectRatioTolerance"] == "0"
                && !filter.Parameters.ContainsKey("aspectRatio"), "Final template/rule filter parameters differ.");
        }
    }

    private static async Task FlushAuthorFilterUiAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
}
