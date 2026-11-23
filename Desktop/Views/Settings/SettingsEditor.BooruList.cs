using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using MeowChan.Desktop;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private const string BooruListKey = "settings.booruList.";
    private sealed record BooruListField(BooruListLayoutSettings.Field Spec, SettingsNumberInput Input, TextBlock Label,
        TextBlock Hint, TextBlock Error);
    private readonly List<BooruListField> _booruListFields = new();

    private void InitializeBooruListFields()
    {
        foreach (BooruListLayoutSettings.Field field in BooruListLayoutSettings.Fields)
        {
            string name = "BooruList" + field.Name;
            SettingsNumberInput input = new() { Name = name + "Input", Minimum = field.Min, Maximum = field.Max };
            TextBlock label = new() { Name = name + "FieldLabel", Classes = { "workspace-field-label" }, TextWrapping = TextWrapping.Wrap };
            TextBlock hint = new() { Classes = { "workspace-help" }, TextWrapping = TextWrapping.Wrap };
            TextBlock error = new() { Name = input.Name + "Error", Classes = { "workspace-error" }, TextWrapping = TextWrapping.Wrap, IsVisible = false };
            AutomationProperties.SetLabeledBy(input, label);
            AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
            StackPanel editor = new() { Spacing = 4, Children = { input, error } };
            Panel host = field.Name == nameof(BooruListLayoutSettings.PreviewWidth) ? BooruListBasicFields : BooruListAdvancedFields;
            host.Children.Add(SettingsRowFactory.Create([label, hint], editor));
            _booruListFields.Add(new(field, input, label, hint, error));
        }
        foreach (BooruListField field in _booruListFields.Where(field =>
            field.Spec.Name is nameof(BooruListLayoutSettings.MinimumWidth) or nameof(BooruListLayoutSettings.MaximumWidth)))
            field.Input.ValueChanged += (_, _) => UpdateBooruListTexts();
        UpdateBooruListTexts();
    }

    private void LoadBooruListDraft(Config config)
    {
        foreach (BooruListField field in _booruListFields)
            ResetNumber(field.Input, field.Spec.Read(config.BooruListLayout));
        UpdateBooruListTexts();
    }

    private BooruListLayoutSettings CaptureBooruListDraft()
    {
        BooruListLayoutSettings draft = BooruListLayoutSettings.Default;
        foreach (BooruListField field in _booruListFields)
            draft = field.Spec.Write(draft, GetInteger(field.Input.Value, field.Label.Text!, field.Spec.Min, field.Spec.Max));
        return draft;
    }

    private bool ValidateBooruListDraft(bool focusError)
    {
        if (CaptureBooruListDraft().InvalidField() is not { } name) return true;
        BooruListField field = ListField(name);
        string kind = name switch
        {
            nameof(BooruListLayoutSettings.PreviewWidth) or nameof(BooruListLayoutSettings.MinimumWidth) => "Width",
            nameof(BooruListLayoutSettings.MediumFrom) => "Breakpoint",
            nameof(BooruListLayoutSettings.MediumPreviewHeight) or nameof(BooruListLayoutSettings.WidePreviewHeight) => "Height",
            _ => "Row"
        };
        string Message() => Texts.T(BooruListKey + "invalid." + kind,
            name == nameof(BooruListLayoutSettings.NarrowRowHeight)
                ? BooruListLayoutSettings.MinimumNarrowFrameAndTags : BooruListLayoutSettings.MinimumFrameAndActions);
        field.Error.Text = Message();
        field.Error.IsVisible = true;
        _firstInvalidInput ??= field.Input;
        SetFieldErrorHelp(field.Input, Message);
        return FinishFieldValidation(focusError);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> BooruListNumericFields()
    {
        foreach (BooruListField field in _booruListFields)
            yield return (field.Input, field.Input.Value, field.Label.Text!, "DIP", field.Spec.Min, field.Spec.Max, true);
    }

    private IEnumerable<DefaultSetting> BooruListDefaults() => _booruListFields.Select(field =>
        Number(field.Input, () => field.Input.Value, value => field.Input.Value = value, config => field.Spec.Read(config.BooruListLayout)));

    private BooruListField ListField(string name) => _booruListFields.First(field => field.Spec.Name == name);

    private TextBlock? BooruListError(string name) => _booruListFields.FirstOrDefault(field => field.Error.Name == name)?.Error;

    private void UpdateBooruListTexts()
    {
        BooruListAdvanced.Header = Texts.T(BooruListKey + "advanced");
        decimal? configuredMin = ListField(nameof(BooruListLayoutSettings.MinimumWidth)).Input.Value;
        decimal? configuredMax = ListField(nameof(BooruListLayoutSettings.MaximumWidth)).Input.Value;
        foreach (BooruListField field in _booruListFields)
        {
            field.Label.Text = Texts.T(BooruListKey + field.Spec.Name);
            (decimal minimum, decimal maximum) = (field.Spec.Min, field.Spec.Max);
            if (field.Spec.Name == nameof(BooruListLayoutSettings.PreviewWidth)
                && configuredMin is { } min && configuredMax is { } max && min <= max)
                (minimum, maximum) = (min, max);
            string range = Texts.T("settings.reader.range", minimum, maximum);
            field.Hint.Text = Texts.T(BooruListKey + "default", field.Spec.Read(BooruListLayoutSettings.Default)) + " " + range;
            AutomationProperties.SetName(field.Input, field.Label.Text);
        }
        UpdateBooruListHelp();
    }

    private void UpdateBooruListHelp()
    {
        foreach (BooruListField field in _booruListFields)
            AutomationProperties.SetHelpText(field.Input, FieldHelp(field.Input.Name!, field.Hint.Text!));
    }
}
