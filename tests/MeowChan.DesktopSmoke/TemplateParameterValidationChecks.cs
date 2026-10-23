using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.Rules;

namespace MeowChan.DesktopSmoke;

internal static partial class PluginManagementChecks
{
    private static async Task CheckTemplateParameterValidationAsync(MainWindow window, PluginManagementServices management)
    {
        window.FindControl<MenuItem>("RulesMenuItem")!.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!;
        object? retained = host.Content;
        string language = Texts.Language;
        RuleTemplateSet catalog = RuleTemplateSet.Empty;
        int writes = 0, workerStarts = 0;
        TaskCompletionSource? gate = null;
        PluginManagementServices delayed = new(management.ProfileStore, management.Compiler, management.Inventory, management.PluginDirectory,
            management.DescribeTestAsync, async (artifact, configuration, token) =>
            {
                workerStarts++;
                Task? wait = gate?.Task;
                if (wait != null) await wait.WaitAsync(token);
                return await management.StartTestWorkerAsync(artifact, configuration, token);
            }, management.ProbeAsync);
        MediaRuleSet rules = new("template-validation-rules", new[] { new MediaRule(MediaRuleScope.Global)
        {
            ImageFilter = new(MediaImageFilterMode.Plugin, "fixture.management", "fixture-filter", "2",
                new Dictionary<string, string> { ["rule-label"] = "allowed", ["rule-count"] = "2" })
        } });
        RulesPage page = new(() => rules, (_, _) => throw new InvalidOperationException("Template validation wrote a rule."));
        page.AttachPluginServices(delayed);
        page.AttachTemplateServices(() => Task.FromResult(new RuleTemplateLoadResult(catalog, null, false)), (values, revision) =>
        {
            Require(revision == catalog.Revision, "Template validation lost its catalog revision.");
            writes++; catalog = new(Guid.NewGuid().ToString("N"), values); return Task.FromResult(catalog);
        });
        host.Content = page;
        try
        {
            page.IsAvailable = true; page.EditScope(MediaRuleScope.Global); await page.RefreshPluginFiltersAsync();
            await Program.WaitUntilAsync(() => page.CanExecuteTemplateCommand("rules.template-create"));
            await page.ExecuteTemplateCommandAsync("rules.template-create"); await FlushTemplateAsync();
            Named<TextBox>("RuleTemplateName").Text = "Validated filter";
            Named<CheckBox>("templates-includeFilter").IsChecked = true;
            TextBox count = Count();
            count.Text = "1.5"; Save(); // No dispatcher yield: Snapshot must see current raw input.
            await Program.WaitUntilAsync(() => TemplateFieldFocused(count));
            Require(writes == 0 && workerStarts == 0 && count.Text == "1.5"
                && AutomationProperties.GetHelpText(count)?.Contains(Texts.T("plugins.integerError"), StringComparison.Ordinal) == true
                && Editor().GetVisualDescendants().OfType<TextBlock>().Any(label => label.IsEffectivelyVisible && label.Text == Texts.T("plugins.integerError")),
                "Local template validation lost raw input, field error/help/focus, or wrote/started a worker.");
            Require(AutomationProperties.GetAutomationId(count)?.StartsWith("PluginField:FilterRule:template%3A", StringComparison.Ordinal) == true,
                "Template parameter identity is not separated from the live rule parameter.");
            foreach (string nextLanguage in new[] { "de", "en" })
            {
                Texts.SetLanguage(nextLanguage); await FlushTemplateAsync();
                count = Count();
                Require(count.Text == "1.5" && !string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(count)),
                    "Template language rebuild lost raw text or field errors.");
                Save(); await Program.WaitUntilAsync(() => TemplateFieldFocused(count));
            }
            count.Text = "9";
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Save();
            Require(page.IsBusy && writes == 0, "Worker validation was not awaited before publication.");
            gate.SetResult();
            await Program.WaitUntilAsync(() => !page.IsBusy && TemplateFieldFocused(count));
            Require(count.Text == "9" && writes == 0 && ReferenceEquals(count, Count())
                && AutomationProperties.GetHelpText(count)?.Contains("Fixture rejects count nine", StringComparison.Ordinal) == true
                && Editor().GetVisualDescendants().OfType<TextBlock>().Any(label => label.IsEffectivelyVisible && label.Text == "Fixture rejects count nine"),
                "Real template worker rejection lost its actual field, raw text, accessible error, or focused it before Busy ended.");
            Program.CaptureWindow(window, "smoke-template-filter-worker-field-error.png");
            await PluginSettingsErrorMatrixChecks.CaptureAsync(window, page, "template-worker", "rule-count", "9", "Fixture rejects count nine",
                Editor, async () => { Save(); await Program.WaitUntilAsync(() => !page.IsBusy); }, () => writes == 0 && ReferenceEquals(catalog, RuleTemplateSet.Empty));
            count = Count(); // Matrix language changes rebuild the view, while the draft remains the same.


            // A changed-then-restored value has the same fingerprint but a newer edit generation.
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Save(); Require(page.IsBusy, "Stale worker fixture did not enter Busy.");
            count.Text = "2"; count.Text = "9";
            Button outside = window.FindControl<Button>("DownloadsNavigationButton")!; outside.Focus(NavigationMethod.Tab);
            gate.SetResult(); await Program.WaitUntilAsync(() => !page.IsBusy); await FlushTemplateAsync();
            Require(writes == 0 && outside.IsFocused && count.Text == "9" && string.IsNullOrEmpty(AutomationProperties.GetHelpText(count)),
                "A late worker result published field errors or stole focus after the parameter generation changed.");

            gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Save(); Require(page.IsBusy, "Decision worker fixture did not enter Busy.");
            page.DeactivateRuleDecisions(); outside.Focus(NavigationMethod.Tab);
            gate.SetResult(); await Program.WaitUntilAsync(() => !page.IsBusy); await FlushTemplateAsync();
            Require(writes == 0 && outside.IsFocused && string.IsNullOrEmpty(AutomationProperties.GetHelpText(count)),
                "A late worker result survived the rule decision generation.");

            gate = null; count.Text = "2"; Save();
            await Program.WaitUntilAsync(() => !page.IsBusy && writes == 1 && page.CanExecuteTemplateCommand("rules.template-edit"));
            Require(catalog.Templates.Single().ImageFilter!.Parameters["rule-count"] == "2",
                "Corrected template parameters did not publish exactly once.");
            await page.ExecuteTemplateCommandAsync("rules.template-edit"); await FlushTemplateAsync();
            count = Count(); count.Text = "9";
            gate = new(TaskCreationOptions.RunContinuationsAsynchronously); Save();
            Require(page.IsBusy, "Lifetime worker fixture did not enter Busy.");
            outside.Focus(NavigationMethod.Tab); page.EndLifetime(); gate.TrySetResult(); await FlushTemplateAsync();
            Require(writes == 1 && outside.IsFocused && count.Text == "9", "A cancelled template lifetime wrote or stole focus.");
            Console.WriteLine("TEMPLATE PARAMETER UI PASS: raw integer/local and real worker errors, accessible field mapping, post-Busy focus, language retention, edit/decision/lifetime guards, corrected publication.");
        }
        finally { gate?.TrySetResult(); page.EndLifetime(); host.Content = retained; Texts.SetLanguage(language); }
        T Named<T>(string name) where T : Control => page.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);
        PluginSettingsEditor Editor() => page.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(editor =>
            AutomationProperties.GetAutomationId(editor.FindField("rule-count")!)?.StartsWith("PluginField:FilterRule:template%3A", StringComparison.Ordinal) == true);
        TextBox Count() => (TextBox)Editor().FindField("rule-count")!;
        void Save() { Button save = Named<Button>("templates-save"); save.Focus(NavigationMethod.Tab); save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); }
    }
    private static async Task FlushTemplateAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }
    private static bool TemplateFieldFocused(Control input)
    {
        if (!input.IsAttachedToVisualTree() || !input.IsEffectivelyVisible || !input.IsEffectivelyEnabled
            || input.Bounds.Width <= 0 || input.Bounds.Height <= 0
            || !ReferenceEquals(TopLevel.GetTopLevel(input)?.FocusManager?.GetFocusedElement(), input)) return false;
        foreach (ScrollViewer scroll in input.GetVisualAncestors().OfType<ScrollViewer>())
        {
            var point = input.TranslatePoint(default, scroll);
            if (point == null || point.Value.Y < -1 || point.Value.Y + input.Bounds.Height > scroll.Bounds.Height + 1) return false;
        }
        return true;
    }
}
