using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Plugins;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan.DesktopSmoke;

internal static class PluginSettingsLanguageSwitchChecks
{
    // Call from the native smoke's UI-thread continuation, next to LanguageSwitchFooterChecks.
    // Exercise BuildSettings' dynamic editor reconstruction with the same draft. These checks
    // do not claim native presentation/focus coverage or reload/replace the loaded catalogs.
    internal static void Run()
    {
        Dispatcher.UIThread.VerifyAccess();
        string initialLanguage = Texts.Language;
        try
        {
            foreach ((string language, SettingScope scope) in new[]
            {
                ("es", SettingScope.Plugin), ("fr", SettingScope.Source), ("ru", SettingScope.FilterRule)
            })
            {
                Texts.SetLanguage("en");
                RebuiltEditorRelocalizesHostControlsWithoutMutatingRawDraftOrProviderText(language, scope);
            }
            foreach (string language in new[] { "es", "fr", "ru" })
            {
                Texts.SetLanguage("en");
                RebuiltSchemaReviewUsesCurrentHostLabelsWithoutAcknowledgingRemoval(language);
            }
            Texts.SetLanguage("en");
            ClearingErrorsOnTheCurrentEditorDoesNotResurrectThemOnAnotherLanguageRebuild();
            Console.WriteLine("SMOKE plugin settings language: 7 editor regressions passed on the UI thread; raw drafts, provider text, accessibility, schema review and error clearing retained.");
        }
        finally
        {
            Texts.SetLanguage(initialLanguage);
        }
    }

    private static void RebuiltEditorRelocalizesHostControlsWithoutMutatingRawDraftOrProviderText(string language, SettingScope scope)
    {
        const string scopeIdentity = "source/with:identity";
        SettingsSchema schema = Schema(scope);
        PluginSettingsDraft draft = new(schema, scope, null);
        draft.Set("number", "9223372036854775808");
        draft.Set("optional", " unfinished <literal> ü\nraw ", false);
        draft.Set("mode", "obsolete/value");
        draft.Set("flag", "legacy-not-a-boolean");
        string fingerprint = draft.EditorFingerprint;
        long generation = draft.EditGeneration;
        bool dirty = draft.Dirty, discardable = draft.HasDiscardableChanges;
        PluginSettingsEditor previous = new(draft, scopeIdentity);
        object? englishValueSet = ValueSet(previous, "optional").Content;
        object? englishUnset = Unset(previous).Content;
        // A worker may reuse a local validator's code, label or even catalog key.
        // That does not give its message translation provenance.
        FieldValidationError[] workerErrors =
        [
            new("number", "integer", "Provider says: plugins.integerError <raw> ü"),
            new("mode", "required", "Provider enum rejection"),
            new("number", "integer", "Second provider detail")
        ];
        previous.ShowErrors(workerErrors);

        foreach (string next in new[] { language, "de", language })
        {
            Texts.SetLanguage(next);
            string context = $"{scope}/{next}";
            Require(Texts.Language == next, context + ": language switch was not applied.");
            PluginSettingsEditor current = new(draft, scopeIdentity);
            Require(ReferenceEquals(draft, current.Draft), context + ": editor replaced the draft.");
            Require(draft.EditorFingerprint == fingerprint && draft.EditGeneration == generation
                && draft.Dirty == dirty && draft.HasDiscardableChanges == discardable,
                context + ": reconstruction mutated draft state.");
            Require(Field<TextBox>(current, "number").Text == "9223372036854775808",
                context + ": rejected integer raw text changed.");
            TextBox optional = Field<TextBox>(current, "optional");
            Require(optional.Text == " unfinished <literal> ü\nraw " && !optional.IsEnabled && !draft.IsSet("optional"),
                context + ": optional unset raw text/state changed.");
            CheckBox valueSet = ValueSet(current, "optional");
            Require(Equals(valueSet.Content, Texts.T("plugins.valueSet")) && !Equals(valueSet.Content, englishValueSet),
                context + ": ValueSet retained an English label.");
            Require(AutomationProperties.GetName(valueSet) == "Provider optional: " + Texts.T("plugins.valueSet"),
                context + ": ValueSet accessible label was stale or translated provider text.");
            Require(AutomationProperties.GetAutomationId(valueSet)
                == PluginSettingsEditor.FieldIdentity(scope, scopeIdentity, "optional") + ":set",
                context + ": ValueSet identity changed.");
            Require(Equals(Unset(current).Content, Texts.T("plugins.unset")) && !Equals(Unset(current).Content, englishUnset),
                context + ": unset choice retained an English label.");
            ComboBox combo = Field<ComboBox>(current, "mode");
            Require(Need<ComboBoxItem>(combo.SelectedItem, context + ": enum selection").Tag as string == "obsolete/value",
                context + ": obsolete enum raw selection changed.");
            Require(combo.ItemsSource!.Cast<ComboBoxItem>().Any(item => Equals(item.Content, "Provider option <literal> ü")),
                context + ": provider option text changed.");
            Require(Field<CheckBox>(current, "flag").IsChecked == null,
                context + ": invalid legacy Boolean was silently coerced.");

            foreach (SettingDefinition definition in schema.Fields)
            {
                Control field = Field<Control>(current, definition.Key);
                string fieldContext = context + "/" + definition.Key;
                Require(AutomationProperties.GetAutomationId(field)
                    == PluginSettingsEditor.FieldIdentity(scope, scopeIdentity, definition.Key),
                    fieldContext + ": field identity changed.");
                Require(AutomationProperties.GetName(field) == definition.Label + (definition.IsRequired ? " *" : ""),
                    fieldContext + ": accessible provider label changed.");
                Require(Need<TextBlock>(AutomationProperties.GetLabeledBy(field), fieldContext + ": label").Text == definition.Label,
                    fieldContext + ": visible provider label changed.");
                string expectedError = string.Join(Environment.NewLine,
                    workerErrors.Where(error => error.FieldKey == definition.Key).Select(error => error.Message));
                TextBlock error = Error(current, definition.Key);
                Require(error.Text == expectedError && error.IsVisible == (expectedError.Length > 0),
                    fieldContext + ": worker error text/order/visibility changed.");
                Require(AutomationProperties.GetHelpText(field) == string.Join(Environment.NewLine,
                    new[] { definition.Help, expectedError }.Where(text => !string.IsNullOrEmpty(text))),
                    fieldContext + ": accessible help/error text changed.");
                if (definition.Help != null)
                    Require(Content(current, definition.Key).Children.OfType<TextBlock>().Any(text => text.Text == definition.Help),
                        fieldContext + ": provider help changed.");
            }
        }
    }

    private static void RebuiltSchemaReviewUsesCurrentHostLabelsWithoutAcknowledgingRemoval(string language)
    {
        PluginSettingsDraft draft = new(new("2", Array.Empty<SettingDefinition>()), SettingScope.Plugin,
            new("1", [new("removed/key", SettingKind.Text, Text: " preserved <provider value> ü ")]));
        PluginSettingsEditor english = new(draft, "schema/review");
        string fingerprint = draft.EditorFingerprint;
        Button englishAcknowledge = Need<Button>(english.SchemaAcknowledgement, "English schema acknowledgement");
        string identity = AutomationProperties.GetAutomationId(englishAcknowledge)
            ?? throw new InvalidOperationException("Schema acknowledgement has no automation identity.");
        Require(identity.Length > 0, "Schema acknowledgement has an empty automation identity.");
        Texts.SetLanguage(language);

        PluginSettingsEditor current = new(draft, "schema/review");
        Button acknowledge = Need<Button>(current.SchemaAcknowledgement, language + ": schema acknowledgement");
        Require(Equals(acknowledge.Content, Texts.T("plugins.acceptSchema"))
            && !Equals(acknowledge.Content, englishAcknowledge.Content),
            language + ": schema action retained an English label.");
        Require(AutomationProperties.GetAutomationId(acknowledge) == identity,
            language + ": schema action identity changed.");
        StackPanel removed = Need<StackPanel>(current.Children[0], language + ": removed settings panel");
        Require(Need<TextBlock>(removed.Children[0], language + ": removed settings label").Text
            == Texts.T("plugins.removedSettings") + "\nremoved/key =  preserved <provider value> ü ",
            language + ": removed settings host label was stale or provider raw value changed.");
        Require(current.Children.OfType<TextBlock>().Any(text => text.Text == Texts.T("plugins.noSettings")),
            language + ": empty schema host label was stale.");
        Require(draft.EditorFingerprint == fingerprint && draft.UnmappedValues["removed/key"] == " preserved <provider value> ü "
            && !draft.Dirty && !draft.HasDiscardableChanges && draft.EditGeneration == 0,
            language + ": schema reconstruction mutated/acknowledged removed settings.");
    }

    private static void ClearingErrorsOnTheCurrentEditorDoesNotResurrectThemOnAnotherLanguageRebuild()
    {
        PluginSettingsDraft draft = new(Schema(SettingScope.Plugin), SettingScope.Plugin, null);
        PluginSettingsEditor first = new(draft);
        first.ShowErrors([new("number", "integer", "Provider integer rejection")]);
        Texts.SetLanguage("fr");
        PluginSettingsEditor second = new(draft);
        Require(Error(second, "number").IsVisible, "French rebuild lost an existing error.");
        second.ShowErrors(Array.Empty<FieldValidationError>());
        Texts.SetLanguage("ru");
        PluginSettingsEditor third = new(draft);
        Require(Error(third, "number").Text == string.Empty && !Error(third, "number").IsVisible,
            "Russian rebuild resurrected a cleared worker error.");
        Require(AutomationProperties.GetHelpText(Field<Control>(third, "number")) == "Provider number help",
            "Cleared worker error remained in accessible help.");
    }

    private static SettingsSchema Schema(SettingScope scope) => new("1",
    [
        new("number", scope, SettingKind.Integer, "plugins.integerError", "Provider number help", IsRequired: true),
        new("optional", scope, SettingKind.Text, "Provider optional", "Provider optional help"),
        new("mode", scope, SettingKind.Enum, "Provider mode", Options: [new("a", "Provider option <literal> ü")]),
        new("flag", scope, SettingKind.Boolean, "Provider flag")
    ]);

    private static StackPanel Content(PluginSettingsEditor editor, string key)
    {
        Control field = Field<Control>(editor, key);
        StackPanel[] matches = editor.SettingsForm.Items.OfType<FormItem>()
            .Select(item => Need<StackPanel>(item.Content, key + ": form content"))
            .Where(panel => panel.Children.Contains(field)).ToArray();
        Require(matches.Length == 1, key + ": expected exactly one owning form panel.");
        return matches[0];
    }

    private static CheckBox ValueSet(PluginSettingsEditor editor, string key) =>
        Content(editor, key).Children.OfType<CheckBox>().Single();

    private static ComboBoxItem Unset(PluginSettingsEditor editor) =>
        Field<ComboBox>(editor, "mode").ItemsSource!.Cast<ComboBoxItem>().First();

    private static TextBlock Error(PluginSettingsEditor editor, string key) =>
        Content(editor, key).Children.OfType<TextBlock>().Single(text => text.Classes.Contains("error"));

    private static T Field<T>(PluginSettingsEditor editor, string key) where T : Control =>
        Need<T>(editor.FindField(key), key + ": field");

    private static T Need<T>(object? value, string context) where T : class => value as T
        ?? throw new InvalidOperationException(context + ": expected " + typeof(T).Name + ".");

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
