using System.Collections.Generic;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>Draft binding for the shutdown time limit (Behavior): how long the app waits for its shutdown steps before it ends itself.</summary>
public partial class SettingsEditor
{
    private decimal? _shutdownTimeLimitSeconds = Config.ShutdownTimeLimitSecondsDefault;

    public decimal? ShutdownTimeLimitSeconds
    {
        get => _shutdownTimeLimitSeconds;
        set => SetField(ref _shutdownTimeLimitSeconds, value, nameof(ShutdownTimeLimitSeconds));
    }

    public decimal ShutdownTimeLimitSecondsMinimum => Config.ShutdownTimeLimitSecondsMinimum;
    public decimal ShutdownTimeLimitSecondsMaximum => Config.ShutdownTimeLimitSecondsMaximum;
    public string ShutdownTimeLimitSecondsLabel => Texts.T("settings.shutdownTimeLimit");
    public string ShutdownTimeLimitSecondsHint => Texts.T("settings.shutdownTimeLimitHint");
    public string ShutdownTimeLimitSecondsHelp => FieldHelp(nameof(ShutdownTimeLimitSecondsInput), ShutdownTimeLimitSecondsHint);

    private void LoadShutdownLimitDraft(Config config)
    {
        ShutdownTimeLimitSeconds = config.ShutdownTimeLimitSeconds;
        ResetNumber(ShutdownTimeLimitSecondsInput, config.ShutdownTimeLimitSeconds);
    }

    private void ApplyShutdownLimitDraft(Config candidate)
    {
        candidate.ShutdownTimeLimitSeconds = GetInteger(ShutdownTimeLimitSeconds, ShutdownTimeLimitSecondsLabel,
            Config.ShutdownTimeLimitSecondsMinimum, Config.ShutdownTimeLimitSecondsMaximum);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> ShutdownLimitNumericFields()
    {
        yield return (ShutdownTimeLimitSecondsInput, ShutdownTimeLimitSeconds, ShutdownTimeLimitSecondsLabel, Texts.T("settings.unitSeconds"),
            Config.ShutdownTimeLimitSecondsMinimum, Config.ShutdownTimeLimitSecondsMaximum, true);
    }

    private void NotifyShutdownLimitHelpChanged() => Raise(new[] { nameof(ShutdownTimeLimitSecondsHelp) });

    private void RaiseShutdownLimitTextsChanged() => Raise(new[]
    {
        nameof(ShutdownTimeLimitSecondsLabel), nameof(ShutdownTimeLimitSecondsHint), nameof(ShutdownTimeLimitSecondsHelp)
    });
}
