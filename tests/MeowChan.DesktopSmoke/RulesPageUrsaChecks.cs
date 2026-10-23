using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using MeowChan.Rules;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;
internal static partial class RulesPageChecks
{
    private static async Task CheckUrsaDraftWorkflowAsync(MainWindow window)
    {
        ContentControl host = window.FindControl<ContentControl>("RulesPageHost")!; object? retained = host.Content;
        MediaRuleSet rules = new("ursa-rules", Array.Empty<MediaRule>()); RuleTemplateSet templates = RuleTemplateSet.Empty;
        int ruleWrites = 0, templateWrites = 0;
        TaskCompletionSource? templatePublicationGate = null;
        RulesPage fixture = new(() => rules, (values, revision) => { ruleWrites++; rules = new(Guid.NewGuid().ToString("N"), values); return Task.FromResult(rules); });
        fixture.AttachTemplateServices(() => Task.FromResult(new RuleTemplateLoadResult(templates, null, false)), async (values, revision) =>
        {
            Require(revision == templates.Revision, "Template save did not use its independent catalog revision.");
            templateWrites++; var next = new RuleTemplateSet(Guid.NewGuid().ToString("N"), values);
            if (templatePublicationGate != null) await templatePublicationGate.Task;
            templates = next; return templates;
        });
        host.Content = fixture;
        try
        {
            fixture.IsAvailable = true;
            await Program.WaitUntilAsync(() => fixture.CanExecuteTemplateCommand("rules.template-create"));
            fixture.EditScope(MediaRuleScope.ForThread("unknown-source", "unknown-board", "unknown-thread"));
            fixture.RuleEnabled = false; fixture.TypesMode = (int)MediaTypeSelectionMode.Selected;
            await FlushAsync(); // Materialize the editor, then deliberately do not yield between input and Save/Discard.
            var raw = fixture.FindControl<MeowChan.Desktop.Controls.RuleExtensionsInput>("RuleExtensionsInput")!.GetVisualDescendants().OfType<TextBox>().First();
            raw.Text = "image/jpeg";
            await fixture.SaveCurrentAsync();
            Require(ruleWrites == 0 && fixture.ExtensionsText == "image/jpeg" && fixture.HasValidationError,
                "Same-turn Save observed stale valid tag text instead of current raw input.");
            fixture.RequestDiscardCurrentDraft();
            Require(fixture.IsDiscardConfirmationOpen, "Same-turn Discard failed to include current raw text.");
            fixture.DeactivateRuleDecisions();
            fixture.ExtensionsText = "png, image/jpeg";
            fixture.SizeMode = (int)MediaSizeLimitMode.Limited; fixture.MegabytesText = "2";
            await fixture.ExecuteTemplateCommandAsync("rules.template-create"); await FlushAsync();
            TextBox editorName = Named<TextBox>("RuleTemplateName"); editorName.Text = "Only size";
            Named<CheckBox>("templates-includeSize").IsChecked = true;
            TextBox editorSize = Named<TextBox>("RuleTemplateMegabytes"); editorSize.Text = "invalid MB raw";
            Button editorSave = Named<Button>("templates-save"); editorSave.Focus(NavigationMethod.Tab); Click(editorSave);
            await Program.WaitUntilAsync(() => editorSize.IsFocused && editorSize.IsEffectivelyVisible && Fits(window, editorSize));
            Require(ReferenceEquals(editorSize, Named<TextBox>("RuleTemplateMegabytes")) && ReferenceEquals(editorName, Named<TextBox>("RuleTemplateName"))
                && ReferenceEquals(editorSave, Named<Button>("templates-save")) && editorSize.Text == "invalid MB raw" && editorName.Text == "Only size" && templateWrites == 0,
                "Template validation replaced focused fields, lost raw input, or wrote invalid values.");
            editorSize.Text = "2"; Click(editorSave);
            await Program.WaitUntilAsync(() => fixture.CanExecuteTemplateCommand("rules.template-preview"));
            Require(templateWrites == 1 && ruleWrites == 0 && templates.Templates.Single().Types == null,
                "Creating a valid size-only template saved a rule or included the invalid media group.");
            fixture.MegabytesText = "3";
            ComboBox list = Named<ComboBox>("RuleTemplateList"); list.Focus(NavigationMethod.Tab);
            list.SelectedIndex = -1; list.SelectedIndex = 0;
            await Program.WaitUntilAsync(() => list.IsFocused && list.IsEffectivelyVisible);
            Require(ReferenceEquals(list, Named<ComboBox>("RuleTemplateList")), "Selecting a template detached its focused list.");
            Button previewTrigger = Named<Button>("templates-preview"); previewTrigger.Focus(NavigationMethod.Tab);
            await fixture.ExecuteTemplateCommandAsync("rules.template-preview");
            await Program.WaitUntilAsync(() => Named<TextBlock>("RuleTemplatePreviewHeading").IsFocused && Fits(window, Named<TextBlock>("RuleTemplatePreviewHeading")));
            Click(Named<Button>("templates-cancel"));
            await Program.WaitUntilAsync(() => previewTrigger.IsFocused && previewTrigger.IsEffectivelyVisible && Fits(window, previewTrigger));
            await fixture.ExecuteTemplateCommandAsync("rules.template-preview");
            await Program.WaitUntilAsync(() => Named<TextBlock>("RuleTemplatePreviewHeading").IsFocused);
            var escapePreview = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            Named<TextBlock>("RuleTemplatePreviewHeading").RaiseEvent(escapePreview);
            Require(escapePreview.Handled && !fixture.IsTemplateDecisionOpen, "Escape did not dismiss the real preview input surface.");
            await Program.WaitUntilAsync(() => previewTrigger.IsFocused && previewTrigger.IsEffectivelyVisible);
            Require(fixture.ExtensionsText == "png, image/jpeg" && ruleWrites == 0, "Preview Cancel/Escape changed the target draft.");
            await fixture.ExecuteTemplateCommandAsync("rules.template-preview");
            await fixture.ExecuteTemplateCommandAsync("rules.template-apply");
            Require(fixture.MegabytesText == "2" && fixture.ExtensionsText == "png, image/jpeg" && !fixture.RuleEnabled && ruleWrites == 0,
                "Copy changed an unrelated raw field, enabled the target or wrote its rule.");
            await fixture.ExecuteTemplateCommandAsync("rules.template-undo-apply");
            Require(fixture.MegabytesText == "3" && fixture.ExtensionsText == "png, image/jpeg", "Undo failed to restore raw draft input.");
            fixture.RequestDiscardCurrentDraft(); await FlushAsync();
            Require(fixture.IsDiscardConfirmationOpen && ruleWrites == 0, "Explicit changed-draft discard did not ask before any write.");
            fixture.DeactivateRuleDecisions();
            Require(fixture.ExtensionsText == "png, image/jpeg", "Closing the decision lost raw text.");
            fixture.RequestDiscardCurrentDraft(); await FlushAsync();
            PopConfirm confirmation = fixture.GetVisualDescendants().OfType<PopConfirm>().First(p => p.IsDropdownOpen);
            confirmation.ConfirmCommand!.Execute(null); await FlushAsync();
            Require(!fixture.HasDiscardableChanges && fixture.ExtensionsText.Length == 0 && ruleWrites == 0,
                "Confirmed discard failed to reload the saved rule or wrote it.");
            fixture.RequestDiscardCurrentDraft(); Require(!fixture.IsDiscardConfirmationOpen, "An unchanged draft opened a loss confirmation.");
            Require(fixture.FindControl<Breadcrumb>("RuleScopeBreadcrumb")!.Items.Count == 4, "Unknown IDs lost structural breadcrumb ancestors.");
            Button editTrigger = Named<Button>("templates-edit"); editTrigger.Focus(NavigationMethod.Tab);
            await fixture.ExecuteTemplateCommandAsync("rules.template-edit"); await FlushAsync();
            Named<TextBox>("RuleTemplateName").Text = "unsaved name";
            var escapeEditor = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape };
            Named<TextBox>("RuleTemplateName").RaiseEvent(escapeEditor); await FlushAsync();
            Require(escapeEditor.Handled && fixture.IsRuleConfirmationOpen && Named<TextBox>("RuleTemplateName").Text == "unsaved name",
                "Escape discarded an editor draft or bypassed its explicit discard decision.");
            Require(!fixture.CanExecuteTemplateCommand("rules.template-create") && !fixture.CanExecuteTemplateCommand("rules.template-apply"),
                "A template action remained executable behind editor discard confirmation.");
            fixture.GetVisualDescendants().OfType<PopConfirm>().Single(p => p.IsDropdownOpen).ConfirmCommand!.Execute(null);
            await Program.WaitUntilAsync(() => editTrigger.IsFocused && editTrigger.IsEffectivelyVisible && Fits(window, editTrigger));
            Require(templates.Templates.Single().Name == "Only size", "Cancelling the template editor published its raw name.");
            Button deleteTrigger = Named<Button>("templates-delete"); deleteTrigger.Focus(NavigationMethod.Tab);
            await fixture.ExecuteTemplateCommandAsync("rules.template-delete"); await FlushAsync();
            Require(!fixture.CanExecuteTemplateCommand("rules.template-create") && !fixture.CanExecuteTemplateCommand("rules.template-preview"),
                "A template action remained executable behind delete confirmation.");
            templatePublicationGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.GetVisualDescendants().OfType<PopConfirm>().Single(p => p.IsDropdownOpen).ConfirmCommand!.Execute(null);
            Require(fixture.IsBusy && !deleteTrigger.IsEnabled, "Delete did not wait for delayed publication.");
            templatePublicationGate.SetResult();
            await Program.WaitUntilAsync(() => !fixture.IsBusy && templates.Templates.Count == 0);
            await Program.WaitUntilAsync(() => Named<Button>("templates-create").IsFocused && Named<Button>("templates-create").IsEffectivelyVisible);
            Require(ruleWrites == 0, "Deleting a template wrote an active rule.");
        }
        finally { templatePublicationGate?.TrySetResult(); fixture.EndLifetime(); host.Content = retained; }
        T Named<T>(string name) where T : Control => fixture.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
    }
}
