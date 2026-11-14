using System.Collections.Generic;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>Draft binding for the WV12 switch notice. The setting is ordinary notification configuration.</summary>
public partial class SettingsEditor
{
    private decimal? _workViewSwitchNoticeSeconds = 8;

    public decimal? WorkViewSwitchNoticeSeconds
    {
        get => _workViewSwitchNoticeSeconds;
        set => SetField(ref _workViewSwitchNoticeSeconds, value, nameof(WorkViewSwitchNoticeSeconds));
    }

    public decimal WorkViewSwitchNoticeSecondsMinimum => Config.WorkViewSwitchNoticeSecondsMinimum;
    public decimal WorkViewSwitchNoticeSecondsMaximum => Config.WorkViewSwitchNoticeSecondsMaximum;
    public string WorkViewSwitchNoticeSecondsLabel => WorkViewSwitchCatalog.T("settings.workViewSwitchNoticeSeconds");
    public string WorkViewSwitchNoticeSecondsHint => WorkViewSwitchCatalog.T("settings.workViewSwitchNoticeSecondsHint");
    public string WorkViewSwitchNoticeSecondsHelp =>
        FieldHelp(nameof(WorkViewSwitchNoticeSecondsInput), WorkViewSwitchNoticeSecondsHint);

    private void LoadWorkViewSwitchNoticeDraft(Config config)
    {
        WorkViewSwitchNoticeSeconds = config.WorkViewSwitchNoticeSeconds;
        ResetNumber(WorkViewSwitchNoticeSecondsInput, config.WorkViewSwitchNoticeSeconds);
    }

    private void ApplyWorkViewSwitchNoticeDraft(Config candidate)
    {
        candidate.WorkViewSwitchNoticeSeconds = GetInteger(WorkViewSwitchNoticeSeconds,
            WorkViewSwitchNoticeSecondsLabel, Config.WorkViewSwitchNoticeSecondsMinimum,
            Config.WorkViewSwitchNoticeSecondsMaximum);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> WorkViewNoticeNumericFields()
    {
        yield return (WorkViewSwitchNoticeSecondsInput, WorkViewSwitchNoticeSeconds,
            WorkViewSwitchNoticeSecondsLabel, Texts.T("settings.unitSeconds"),
            Config.WorkViewSwitchNoticeSecondsMinimum, Config.WorkViewSwitchNoticeSecondsMaximum, true);
    }

    private static readonly string[] WorkViewNoticeHelpProperties =
    [
        nameof(WorkViewSwitchNoticeSecondsHelp)
    ];

    private void NotifyWorkViewNoticeHelpChanged() => Raise(WorkViewNoticeHelpProperties);

    private void RaiseWorkViewNoticeTextsChanged() => Raise(new[]
    {
        nameof(WorkViewSwitchNoticeSecondsLabel), nameof(WorkViewSwitchNoticeSecondsHint),
        nameof(WorkViewSwitchNoticeSecondsHelp)
    });
}
