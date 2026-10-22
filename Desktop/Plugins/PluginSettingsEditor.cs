using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop.Localization;
using MeowChan.PluginSdk;

namespace MeowChan.Desktop.Plugins;

internal sealed class PluginSettingsEditor : StackPanel
{
    private readonly Dictionary<string, TextBlock> _errors = new(StringComparer.Ordinal);
    internal PluginSettingsDraft Draft { get; }

    internal PluginSettingsEditor(PluginSettingsDraft draft)
    {
        Draft = draft;
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
        foreach (SettingDefinition field in draft.Schema.Fields)
        {
            TextBlock label = new() { Text = field.Label + (field.IsRequired ? " *" : string.Empty), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
            Control editor;
            if (field.Kind == SettingKind.Boolean)
            {
                CheckBox checkbox = new() { IsThreeState = !field.IsRequired,
                    IsChecked = draft.IsSet(field.Key) ? draft.Text(field.Key) == "true" : null };
                checkbox.IsCheckedChanged += (_, _) => draft.Set(field.Key, checkbox.IsChecked == true ? "true" : "false", checkbox.IsChecked.HasValue);
                editor = checkbox;
            }
            else if (field.Kind == SettingKind.Enum)
            {
                ComboBox combo = new() { HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
                List<ComboBoxItem> choices = new() { new() { Content = Texts.T("plugins.unset"), Tag = null } };
                choices.AddRange((field.Options ?? Array.Empty<SettingOption>()).Select(option => new ComboBoxItem { Content = option.Label, Tag = option.Value }));
                combo.ItemsSource = choices;
                combo.SelectedItem = choices.FirstOrDefault(item => Equals(item.Tag, draft.IsSet(field.Key) ? draft.Text(field.Key) : null));
                combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is ComboBoxItem choice) draft.Set(field.Key, choice.Tag as string ?? string.Empty, choice.Tag != null); };
                editor = combo;
            }
            else
            {
                TextBox input = new() { Text = draft.Text(field.Key), MaxLength = field.MaximumLength ?? 32768,
                    PlaceholderText = field.Kind == SettingKind.Uri ? "https://…" : null };
                input.TextChanged += (_, _) => draft.Set(field.Key, input.Text ?? string.Empty,
                    field.IsRequired || !string.IsNullOrEmpty(input.Text));
                editor = input;
            }
            editor.Name = "PluginField_" + field.Key.Replace('-', '_').Replace('.', '_');
            AutomationProperties.SetName(editor, label.Text);
            AutomationProperties.SetLabeledBy(editor, label);
            AutomationProperties.SetHelpText(editor, field.Help ?? string.Empty);
            Children.Add(label); Children.Add(editor);
            if (!string.IsNullOrEmpty(field.Help))
                Children.Add(new TextBlock { Text = field.Help, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
            TextBlock error = new() { IsVisible = false, TextWrapping = TextWrapping.Wrap, Classes = { "error" } };
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Polite);
            _errors.Add(field.Key, error); Children.Add(error);
        }
    }

    internal void ShowErrors(IReadOnlyList<FieldValidationError> errors)
    {
        foreach ((string key, TextBlock label) in _errors)
        {
            label.Text = string.Join(Environment.NewLine, errors.Where(error => error.FieldKey == key).Select(error => error.Message));
            label.IsVisible = !string.IsNullOrEmpty(label.Text);
        }
    }
}
