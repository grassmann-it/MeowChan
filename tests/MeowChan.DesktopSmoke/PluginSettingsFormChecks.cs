using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginHost.Configuration;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

internal static class PluginSettingsFormChecks
{
    internal static async Task RunManagedScopesAsync(PluginsPage page, PluginProfileStore profile)
    {
        PluginSettingsEditor Editor(SettingScope scope) => page.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(editor => editor.Draft.Scope == scope);
        TextBox Field(SettingScope scope, string key) => (TextBox)Editor(scope).FindField(key)!;
        await OpenManagedScopeAsync(page, SettingScope.Plugin);
        long revision = (await profile.ReadAsync()).Revision;
        Field(SettingScope.Plugin, "number").Text = "1.5";
        Require(page.CurrentDraft!.Plugin.Text("number") == "1.5", "Save would observe stale raw text after an immediate edit.");
        page.FindControl<Button>("PluginSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => Field(SettingScope.Plugin, "number").IsFocused);
        Require(HasVisibleFieldFocus(Field(SettingScope.Plugin, "number")), "Plugin integer error did not reach the visible input through the window FocusManager.");
        Require((await profile.ReadAsync()).Revision == revision && Field(SettingScope.Plugin, "number").Text == "1.5", "Local form rejection wrote or replaced integer raw text.");
        await NativeFeatureCheckpoint.HoldAsync("plugin-local-error", page, Field(SettingScope.Plugin, "number"), Field(SettingScope.Plugin, "number"), page.FindControl<Button>("PluginSave")!);
        string language = Texts.Language;
        Texts.SetLanguage(language == "de" ? "en" : "de");
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Require(Field(SettingScope.Plugin, "number").Text == "1.5" && Editor(SettingScope.Plugin).GetLogicalDescendants().OfType<TextBlock>().Any(label => label.IsVisible && label.Classes.Contains("error") && !string.IsNullOrEmpty(label.Text)), "Language rebuild lost raw text or field errors.");
        Texts.SetLanguage(language);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        Field(SettingScope.Plugin, "number").Text = "2";
        await OpenManagedScopeAsync(page, SettingScope.Source);
        Field(SettingScope.Source, "label").Text = "rejected";
        Expander source = Editor(SettingScope.Source).GetLogicalAncestors().OfType<Expander>().First();
        source.IsExpanded = false;
        page.FindControl<Button>("PluginSave")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Program.WaitUntilAsync(() => !page.IsBusy && source.IsExpanded && Field(SettingScope.Source, "label").IsFocused);
        Require(HasVisibleFieldFocus(Field(SettingScope.Source, "label")), "Source error did not focus the visible input after opening its expander.");
        Require((await profile.ReadAsync()).Revision == revision, "Source worker rejection changed the profile revision.");
        Require(!Editor(SettingScope.Plugin).GetLogicalDescendants().OfType<TextBlock>().Any(label => label.IsVisible && label.Text == "Fixture rejects this label"), "Source worker error was assigned to the same plugin field key.");
        await NativeFeatureCheckpoint.HoldAsync("source-worker-error", page, Field(SettingScope.Source, "label"), Field(SettingScope.Source, "label"), page.FindControl<Button>("PluginSave")!);
        Field(SettingScope.Source, "label").Text = "source fixture";
    }

    internal static async Task<PluginSettingsEditor> OpenManagedScopeAsync(PluginsPage page, SettingScope scope)
    {
        // A closed presenter owns its editor logically before adding it to the visual tree.
        // Find the existing scope, open its actual header, then require the visual editor.
        Expander group = page.FindControl<StackPanel>("PluginSettings")!.Children.OfType<Expander>()
            .Single(candidate => candidate.Content is PluginSettingsEditor editor && editor.Draft.Scope == scope);
        PluginSettingsEditor expected = (PluginSettingsEditor)group.Content!;
        if (!group.IsExpanded)
        {
            group.ApplyTemplate();
            ToggleButton header = group.GetVisualDescendants().OfType<ToggleButton>()
                .First(toggle => toggle.GetVisualAncestors().OfType<Expander>().FirstOrDefault() == group);
            header.BringIntoView();
            await Dispatcher.UIThread.InvokeAsync(() => { TopLevel.GetTopLevel(page)!.UpdateLayout(); }, DispatcherPriority.Loaded);
            Require(header.IsAttachedToVisualTree() && header.IsEffectivelyVisible && header.Focus(),
                "The managed settings scope has no reachable real expander header.");
            header.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = header, Key = Key.Space });
            header.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyUpEvent, Source = header, Key = Key.Space });
        }
        await Program.WaitUntilAsync(() => group.IsExpanded && expected.IsAttachedToVisualTree() && expected.IsEffectivelyVisible);
        return page.GetVisualDescendants().OfType<PluginSettingsEditor>().Single(editor => ReferenceEquals(editor, expected));
    }

    internal static async Task RunAdapterAsync(ContentControl host)
    {
        object? previous = host.Content;
        try
        {
            foreach (SettingScope scope in new[] { SettingScope.Plugin, SettingScope.Source, SettingScope.FilterRule })
            {
                SettingsSchema schema = new("1", new SettingDefinition[]
                {
                    new("a-b", scope, SettingKind.Integer, "A very long number label " + new string('x', 120), "Number help", true),
                    new("a.b", scope, SettingKind.Uri, "URI", IsRequired: true),
                    new("a_b", scope, SettingKind.Text, "Optional text"),
                    new("flag", scope, SettingKind.Boolean, "Optional flag"),
                    new("mode", scope, SettingKind.Enum, "Mode", Options: new[] { new SettingOption("stable", "Translated label") })
                });
                PluginSettingsDraft draft = new(schema, scope, null);
                draft.SetInitialText("a-b", "9223372036854775808");
                draft.SetInitialText("a.b", "https://[broken");
                draft.SetInitialText("mode", "old choice");
                PluginSettingsEditor editor = new(draft, "fixture/source");
                Expander expander = new() { Header = scope.ToString(), Content = editor, IsExpanded = true };
                host.Content = new ScrollViewer { Content = expander };
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                Require(!draft.Dirty && editor.SettingsForm.Items.Count == schema.Fields.Count && editor.SettingsForm.Items.OfType<FormItem>().Count() == schema.Fields.Count, "Form rendering changed the draft or omitted schema items.");
                Control[] inputs = schema.Fields.Select(field => editor.FindField(field.Key)!).ToArray();
                Require(inputs.Select(input => input.Name).Distinct().Count() == inputs.Length && inputs.Select(AutomationProperties.GetAutomationId).Distinct().Count() == inputs.Length, "Colliding sanitized field names lost a field identity.");
                Require(AutomationProperties.GetIsRequiredForForm(inputs[0]) && AutomationProperties.GetHelpText(inputs[0]) == "Number help", "Form accessibility omitted required status or help.");
                Require(((TextBox)inputs[0]).Text == draft.Text("a-b") && ((TextBox)inputs[1]).Text == draft.Text("a.b"), "Rendering coerced rejected raw input.");
                ComboBox choices = (ComboBox)editor.FindField("mode")!;
                Require(((ComboBoxItem)choices.SelectedItem!).Tag as string == "old choice", "Unknown enum input was silently unset.");
                choices.SelectedItem = choices.ItemsSource!.Cast<ComboBoxItem>().Single(item => item.Tag as string == "stable");
                Require(draft.Text("mode") == "stable", "Enum saved its label instead of the schema value.");
                CheckBox flag = (CheckBox)editor.FindField("flag")!;
                flag.IsChecked = false; Require(draft.IsSet("flag") && draft.Text("flag") == "false", "Optional false became unset.");
                flag.IsChecked = null; Require(!draft.IsSet("flag"), "Optional boolean lost unset.");
                TextBox text = (TextBox)editor.FindField("a_b")!;
                CheckBox set = text.GetLogicalAncestors().OfType<StackPanel>().First().Children.OfType<CheckBox>().Single();
                set.IsChecked = true; Require(draft.IsSet("a_b") && draft.Text("a_b") == "", "Explicit optional empty text became unset.");
                set.IsChecked = false; Require(!draft.IsSet("a_b"), "Optional text cannot return to unset.");
                editor.ShowErrors(new[] { new FieldValidationError("a-b", "one", "first failure"), new FieldValidationError("a-b", "two", "second failure"), new FieldValidationError("unknown", "general", "summary only") });
                string errorHelp = AutomationProperties.GetHelpText(inputs[0]) ?? string.Empty;
                Require(errorHelp.Contains("first failure") && errorHelp.Contains("second failure"), "Multiple field errors are not accessible.");
                expander.IsExpanded = false;
                Require(editor.TryFocusFirstError(), "Known field error could not be targeted.");
                await Program.WaitUntilAsync(() => expander.IsExpanded && inputs[0].IsFocused);
                host.Content = null;
                PluginSettingsEditor rebuilt = new(draft, "fixture/source");
                Require(AutomationProperties.GetHelpText(rebuilt.FindField("a-b")!)?.Contains("second failure") == true, "Rebuilding erased worker field errors.");
                rebuilt.ShowErrors(Array.Empty<FieldValidationError>());
                Require(AutomationProperties.GetHelpText(rebuilt.FindField("a-b")!) == "Number help", "Replacing errors left stale messages.");
                Require(draft.Text("a-b") == "9223372036854775808" && draft.Text("a.b") == "https://[broken", "Focus or rebuilding changed raw text.");
            }
        }
        finally { host.Content = previous; }
    }

    private static bool HasVisibleFieldFocus(Control input)
    {
        if (!input.IsFocused || !input.IsEffectivelyVisible || input.Bounds.Width <= 0 || input.Bounds.Height <= 0
            || !ReferenceEquals(TopLevel.GetTopLevel(input)?.FocusManager?.GetFocusedElement(), input)) return false;
        foreach (ScrollViewer scroll in input.GetVisualAncestors().OfType<ScrollViewer>())
        {
            var location = input.TranslatePoint(default, scroll);
            if (location == null || location.Value.Y < -1 || location.Value.Y + input.Bounds.Height > scroll.Bounds.Height + 1) return false;
        }
        return true;
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
