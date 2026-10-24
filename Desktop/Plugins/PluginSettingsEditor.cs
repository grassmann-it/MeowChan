using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;
using Ursa.Controls;

namespace MeowChan.Desktop.Plugins;

/// <summary>Presentation adapter only: SDK/worker validation and persistence stay with the caller.</summary>
internal sealed class PluginSettingsEditor : StackPanel
{
    private sealed class ErrorState
    {
        internal IReadOnlyList<FieldValidationError> Errors = Array.Empty<FieldValidationError>();
    }
    // Rebuilding the view (for example after a language change) must not erase worker errors.
    // This is presentation state, deliberately excluded from the draft's loss/dirty comparison.
    private static readonly ConditionalWeakTable<PluginSettingsDraft, ErrorState> SavedErrors = new();
    private readonly Dictionary<string, Control> _inputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TextBlock> _errors = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SettingDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly ErrorState _errorState;
    internal PluginSettingsDraft Draft { get; }
    internal Form SettingsForm { get; }

    internal PluginSettingsEditor(PluginSettingsDraft draft, string scopeIdentity = "")
    {
        Draft = draft;
        _errorState = SavedErrors.GetValue(draft, _ => new ErrorState());
        Spacing = 10;
        if (draft.UnmappedValues.Count > 0)
        {
            StackPanel removed = new() { Spacing = 6 };
            removed.Children.Add(new TextBlock { Text = Texts.T("plugins.removedSettings") + "\n" + string.Join("\n", draft.UnmappedValues.Select(pair => pair.Key + " = " + pair.Value)), TextWrapping = TextWrapping.Wrap });
            Button acknowledge = new() { Content = Texts.T("plugins.acceptSchema") };
            acknowledge.Click += (_, _) => { draft.AcknowledgeRemovedFields(); removed.IsVisible = false; };
            removed.Children.Add(acknowledge); Children.Add(removed);
        }
        if (draft.Schema.Fields.Count == 0)
            Children.Add(new TextBlock { Text = Texts.T("plugins.noSettings"), TextWrapping = TextWrapping.Wrap });
        SettingsForm = new Form { HorizontalAlignment = HorizontalAlignment.Stretch, LabelWidth = GridLength.Auto };
        Children.Add(SettingsForm);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (SettingDefinition field in draft.Schema.Fields)
        {
            TextBlock label = new() { Text = field.Label, TextWrapping = TextWrapping.Wrap };
            StackPanel content = new() { Spacing = 6 };
            Control editor;
            if (field.Kind == SettingKind.Boolean)
            {
                bool valid = bool.TryParse(draft.Text(field.Key), out bool boolean);
                CheckBox checkbox = new() { IsThreeState = !field.IsRequired,
                    IsChecked = draft.IsSet(field.Key) && valid ? boolean : null };
                TextBlock? legacyValue = null;
                bool? previousChecked = checkbox.IsChecked;
                checkbox.IsCheckedChanged += (_, _) =>
                {
                    if (previousChecked == checkbox.IsChecked) return;
                    previousChecked = checkbox.IsChecked;
                    if (legacyValue != null) legacyValue.IsVisible = false;
                    draft.Set(field.Key, checkbox.IsChecked == true ? "true" : "false", checkbox.IsChecked.HasValue);
                };
                editor = checkbox;
                if (draft.IsSet(field.Key) && !valid)
                {
                    legacyValue = new TextBlock { Text = draft.Text(field.Key), TextWrapping = TextWrapping.Wrap };
                    content.Children.Add(legacyValue);
                }
            }
            else if (field.Kind == SettingKind.Enum)
            {
                ComboBox combo = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
                List<ComboBoxItem> choices = new() { new() { Content = Texts.T("plugins.unset"), Tag = null } };
                choices.AddRange((field.Options ?? Array.Empty<SettingOption>()).Select(option => new ComboBoxItem { Content = option.Label, Tag = option.Value }));
                string? value = draft.IsSet(field.Key) ? draft.Text(field.Key) : null;
                // An obsolete value remains visible and correctable, without silently choosing unset.
                if (value != null && !choices.Any(item => Equals(item.Tag, value)))
                    choices.Add(new ComboBoxItem { Content = value, Tag = value });
                combo.ItemsSource = choices;
                combo.SelectedItem = choices.FirstOrDefault(item => Equals(item.Tag, value));
                object? previousChoice = combo.SelectedItem;
                combo.SelectionChanged += (_, _) =>
                {
                    if (ReferenceEquals(previousChoice, combo.SelectedItem)) return;
                    previousChoice = combo.SelectedItem;
                    if (combo.SelectedItem is ComboBoxItem choice) draft.Set(field.Key, choice.Tag as string ?? string.Empty, choice.Tag != null);
                };
                editor = combo;
            }
            else
            {
                // TextBox intentionally handles Integer and Uri too. Conversion, clamping or MaxLength
                // here would destroy rejected or historical raw input before the existing validator sees it.
                TextBox input = new() { Text = draft.Text(field.Key), HorizontalAlignment = HorizontalAlignment.Stretch,
                    PlaceholderText = field.Kind == SettingKind.Uri ? "https://…" : null };
                CheckBox? set = null;
                if (!field.IsRequired)
                {
                    set = new CheckBox { Content = Texts.T("plugins.valueSet"), IsChecked = draft.IsSet(field.Key) };
                    AutomationProperties.SetName(set, field.Label + ": " + Texts.T("plugins.valueSet"));
                    input.IsEnabled = set.IsChecked == true;
                    set.IsCheckedChanged += (_, _) =>
                    {
                        input.IsEnabled = set.IsChecked == true;
                        if (draft.IsSet(field.Key) != (set.IsChecked == true))
                            draft.Set(field.Key, input.Text ?? string.Empty, set.IsChecked == true);
                    };
                    content.Children.Add(set);
                }
                input.TextChanging += (_, _) =>
                {
                    string text = input.Text ?? string.Empty;
                    // Avalonia raises TextChanging synchronously after TextProperty changes, while
                    // TextChanged is queued. Save/Discard must see even an immediately preceding edit.
                    // Identical text never acknowledges a type migration merely by rendering it.
                    if (draft.Text(field.Key) != text) draft.Set(field.Key, text, field.IsRequired || set?.IsChecked == true);
                };
                editor = input;
            }
            string baseName = "PluginField_" + field.Key.Replace('-', '_').Replace('.', '_');
            string name = baseName;
            for (int suffix = 2; !names.Add(name); suffix++) name = baseName + "_" + suffix;
            editor.Name = name;
            AutomationProperties.SetAutomationId(editor, FieldIdentity(draft.Scope, scopeIdentity, field.Key));
            AutomationProperties.SetName(editor, field.Label + (field.IsRequired ? " *" : string.Empty));
            AutomationProperties.SetLabeledBy(editor, label);
            AutomationProperties.SetIsRequiredForForm(editor, field.IsRequired);
            _inputs.Add(field.Key, editor);
            _definitions.Add(field.Key, field);
            content.Children.Add(editor);
            // Keep correction and its error adjacent even when the schema help exceeds the viewport.
            TextBlock error = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap, Classes = { "error" } };
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
            _errors.Add(field.Key, error); content.Children.Add(error);
            if (!string.IsNullOrEmpty(field.Help))
                content.Children.Add(new TextBlock { Text = field.Help, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
            FormItem item = new() { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            FormItem.SetLabel(item, label);
            FormItem.SetIsRequired(item, field.IsRequired);
            // Ursa's label panel is horizontal; bound the wrapping text to the actual available width.
            item.SizeChanged += (_, _) => label.MaxWidth = Math.Max(1, item.Bounds.Width - 24);
            item.Loaded += (_, _) =>
            {
                // FormItem's content is a help/error panel. Its label must still target the input.
                if (item.GetVisualDescendants().OfType<Label>().FirstOrDefault(value => value.Name == FormItem.PART_Label) is { } nativeLabel)
                    nativeLabel.Target = editor;
            };
            SettingsForm.Items.Add(item);
        }
        ShowErrors(_errorState.Errors);
    }

    internal static string FieldIdentity(SettingScope scope, string scopeIdentity, string key) =>
        "PluginField:" + scope + ":" + Uri.EscapeDataString(scopeIdentity) + ":" + Uri.EscapeDataString(key);

    internal Control? FindField(string key) => _inputs.GetValueOrDefault(key);

    internal void ShowErrors(IReadOnlyList<FieldValidationError> errors)
    {
        _errorState.Errors = errors.ToArray();
        foreach ((string key, TextBlock label) in _errors)
        {
            label.Text = string.Join(Environment.NewLine, errors.Where(error => error.FieldKey == key).Select(error => error.Message));
            label.IsVisible = !string.IsNullOrEmpty(label.Text);
            AutomationProperties.SetHelpText(_inputs[key], string.Join(Environment.NewLine,
                new[] { _definitions[key].Help, label.Text }.Where(text => !string.IsNullOrEmpty(text))));
        }
    }

    // Called only for an explicit failed save. Merely showing/rebinding errors never steals focus.
    internal bool TryFocusFirstError(Func<bool>? current = null)
    {
        string? key = Draft.Schema.Fields.FirstOrDefault(field =>
            _errorState.Errors.Any(error => error.FieldKey == field.Key))?.Key;
        if (key == null || current?.Invoke() == false) return false;
        Control input = _inputs[key];
        foreach (Expander expander in this.GetLogicalAncestors().OfType<Expander>()) expander.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            TopLevel? topLevel = TopLevel.GetTopLevel(this);
            bool isCurrent = current?.Invoke() != false && _errorState.Errors.Any(error => error.FieldKey == key);
            if (!isCurrent || topLevel == null || !IsEffectivelyVisible || !IsEffectivelyEnabled)
            {
                Serilog.Log.Debug("Plugin field focus skipped: scope {Scope}, key {Key}, current {Current}, attached {Attached}, visible {Visible}, enabled {Enabled}",
                    Draft.Scope, key, isCurrent, topLevel != null, IsEffectivelyVisible, IsEffectivelyEnabled);
                return;
            }
            // Opening an Expander invalidates layout. Materialize it before requesting scroll/focus.
            topLevel.UpdateLayout();
            // Optional unset text still needs an accessible correction path.
            Control target = input.IsEffectivelyEnabled ? input : input.GetLogicalParent() is Panel panel
                ? panel.Children.OfType<CheckBox>().FirstOrDefault() ?? input : input;
            if (current?.Invoke() == false || !_errorState.Errors.Any(error => error.FieldKey == key)) return;
            bool accepted = target.Focus(NavigationMethod.Tab);
            // Focus can request the input alone into view. Reveal the associated error afterward,
            // using real arranged geometry: help text/wrapping/multiple messages change this area.
            // Requesting their union keeps the focused input and its local explanation visible together.
            Rect revealArea = new(target.Bounds.Size);
            TextBlock error = _errors[key];
            if (error.IsVisible && error.TranslatePoint(default, target) is Point errorPosition)
                revealArea = revealArea.Union(new Rect(errorPosition, error.Bounds.Size));
            target.BringIntoView(revealArea);
            topLevel.UpdateLayout();
            bool focused = ReferenceEquals(topLevel.FocusManager?.GetFocusedElement(), target);
            Serilog.Log.Debug("Plugin field focus: scope {Scope}, key {Key}, visible {Visible}, enabled {Enabled}, bounds {Bounds}, reveal {Reveal}, accepted {Accepted}, focused {Focused}",
                Draft.Scope, key, target.IsEffectivelyVisible, target.IsEffectivelyEnabled, target.Bounds, revealArea, accepted, focused);
        }, DispatcherPriority.Background);
        return true;
    }
}
