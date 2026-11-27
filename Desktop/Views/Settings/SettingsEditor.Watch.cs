using System;
using System.Collections.Generic;
using Avalonia.Controls;
using MeowChan.Boards.Base;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private bool _adaptiveRecheckEnabled;
    private decimal? _adaptiveRecheckStartSeconds;
    private decimal? _adaptiveRecheckMinimumSeconds;
    private decimal? _adaptiveRecheckMaximumSeconds;

    public bool AdaptiveRecheckEnabled
    {
        get => _adaptiveRecheckEnabled;
        set => SetField(ref _adaptiveRecheckEnabled, value, nameof(AdaptiveRecheckEnabled));
    }

    public decimal? AdaptiveRecheckStartSeconds
    {
        get => _adaptiveRecheckStartSeconds;
        set => SetField(ref _adaptiveRecheckStartSeconds, value, nameof(AdaptiveRecheckStartSeconds));
    }

    public decimal? AdaptiveRecheckMinimumSeconds
    {
        get => _adaptiveRecheckMinimumSeconds;
        set => SetField(ref _adaptiveRecheckMinimumSeconds, value, nameof(AdaptiveRecheckMinimumSeconds));
    }

    public decimal? AdaptiveRecheckMaximumSeconds
    {
        get => _adaptiveRecheckMaximumSeconds;
        set => SetField(ref _adaptiveRecheckMaximumSeconds, value, nameof(AdaptiveRecheckMaximumSeconds));
    }

    public decimal AdaptiveRecheckLowerBound => AdaptiveRecheckSettings.MinimumSeconds;
    public decimal AdaptiveRecheckUpperBound => AdaptiveRecheckSettings.MaximumSeconds;
    public string AdaptiveRecheckEnabledLabel => Texts.T("settings.adaptiveRecheckEnabled");
    public string AdaptiveRecheckHint => Texts.T("settings.adaptiveRecheckHint");
    public string AdaptiveRecheckStartLabel => Texts.T("settings.adaptiveRecheckStart");
    public string AdaptiveRecheckMinimumLabel => Texts.T("settings.adaptiveRecheckMinimum");
    public string AdaptiveRecheckMaximumLabel => Texts.T("settings.adaptiveRecheckMaximum");
    public string AdaptiveRecheckStartHelp => FieldHelp(nameof(AdaptiveRecheckStartSecondsInput), SecondsHelp);
    public string AdaptiveRecheckMinimumHelp => FieldHelp(nameof(AdaptiveRecheckMinimumSecondsInput), SecondsHelp);
    public string AdaptiveRecheckMaximumHelp => FieldHelp(nameof(AdaptiveRecheckMaximumSecondsInput), SecondsHelp);

    private void LoadAdaptiveRecheckDraft(Config config)
    {
        AdaptiveRecheckEnabled = config.AdaptiveRecheckEnabled;
        AdaptiveRecheckStartSeconds = config.AdaptiveRecheckStartSeconds;
        AdaptiveRecheckMinimumSeconds = config.AdaptiveRecheckMinimumSeconds;
        AdaptiveRecheckMaximumSeconds = config.AdaptiveRecheckMaximumSeconds;
        ResetNumber(AdaptiveRecheckStartSecondsInput, config.AdaptiveRecheckStartSeconds);
        ResetNumber(AdaptiveRecheckMinimumSecondsInput, config.AdaptiveRecheckMinimumSeconds);
        ResetNumber(AdaptiveRecheckMaximumSecondsInput, config.AdaptiveRecheckMaximumSeconds);
    }

    private void ApplyAdaptiveRecheckDraft(Config candidate)
    {
        candidate.AdaptiveRecheckEnabled = AdaptiveRecheckEnabled;
        candidate.AdaptiveRecheckStartSeconds = ReadSeconds(AdaptiveRecheckStartSeconds, AdaptiveRecheckStartLabel);
        candidate.AdaptiveRecheckMinimumSeconds = ReadSeconds(AdaptiveRecheckMinimumSeconds, AdaptiveRecheckMinimumLabel);
        candidate.AdaptiveRecheckMaximumSeconds = ReadSeconds(AdaptiveRecheckMaximumSeconds, AdaptiveRecheckMaximumLabel);

        static int ReadSeconds(decimal? value, string label) => GetInteger(value, label,
            AdaptiveRecheckSettings.MinimumSeconds, AdaptiveRecheckSettings.MaximumSeconds);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> AdaptiveRecheckNumericFields()
    {
        yield return (AdaptiveRecheckStartSecondsInput, AdaptiveRecheckStartSeconds, AdaptiveRecheckStartLabel,
            SecondsHelp, AdaptiveRecheckLowerBound, AdaptiveRecheckUpperBound, true);
        yield return (AdaptiveRecheckMinimumSecondsInput, AdaptiveRecheckMinimumSeconds, AdaptiveRecheckMinimumLabel,
            SecondsHelp, AdaptiveRecheckLowerBound, AdaptiveRecheckUpperBound, true);
        yield return (AdaptiveRecheckMaximumSecondsInput, AdaptiveRecheckMaximumSeconds, AdaptiveRecheckMaximumLabel,
            SecondsHelp, AdaptiveRecheckLowerBound, AdaptiveRecheckUpperBound, true);
    }

    private void ValidateAdaptiveRecheckOrder()
    {
        if (AdaptiveRecheckMinimumSeconds > AdaptiveRecheckStartSeconds)
            SetOrderError(AdaptiveRecheckMinimumSecondsInput, AdaptiveRecheckMinimumSecondsInputError);
        if (AdaptiveRecheckStartSeconds > AdaptiveRecheckMaximumSeconds)
            SetOrderError(AdaptiveRecheckMaximumSecondsInput, AdaptiveRecheckMaximumSecondsInputError);

        void SetOrderError(NumericUpDown input, TextBlock error)
        {
            static string Message() => Texts.T("settings.adaptiveRecheckOrder");
            error.Text = Message();
            error.IsVisible = true;
            SetFieldErrorHelp(input, Message);
            _firstInvalidInput ??= input;
        }
    }

    private void NotifyAdaptiveRecheckHelpChanged() => Raise(
        [nameof(AdaptiveRecheckStartHelp), nameof(AdaptiveRecheckMinimumHelp), nameof(AdaptiveRecheckMaximumHelp)]);

    private void RaiseAdaptiveRecheckTextsChanged() => Raise(
        [nameof(AdaptiveRecheckEnabledLabel), nameof(AdaptiveRecheckHint), nameof(AdaptiveRecheckStartLabel),
            nameof(AdaptiveRecheckMinimumLabel), nameof(AdaptiveRecheckMaximumLabel)]);
}
