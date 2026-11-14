using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// The "Statistics" block of the Behavior page: nine settings (six numbers, three switches) with the same draft, validation,
/// discard and save behavior as every other row. Two rows depend on a switch: they stay visible but are unavailable, and say why.
/// The saved values reach the statistics hub through the host's normal settings path (IoStatsOptions.FromConfig).
/// </summary>
public partial class SettingsEditor
{
    private decimal? _statsSampleIntervalMs, _statsHistorySamples, _statsBackgroundIntervalMs, _statsRetentionDays, _statsPersistIntervalMinutes, _statsMaxSources;
    private bool _statsTrayTooltip = true, _statsTrayMenu = true, _statsPersist = true;

    public string StatisticsTitle => Texts.T("stats.settings.title");
    public string StatisticsIntro => Texts.T("stats.settings.intro");

    public decimal? StatsSampleIntervalMs { get => _statsSampleIntervalMs; set => SetField(ref _statsSampleIntervalMs, value, nameof(StatsSampleIntervalMs)); }
    public decimal? StatsHistorySamples { get => _statsHistorySamples; set => SetField(ref _statsHistorySamples, value, nameof(StatsHistorySamples)); }
    public decimal? StatsBackgroundIntervalMs { get => _statsBackgroundIntervalMs; set => SetField(ref _statsBackgroundIntervalMs, value, nameof(StatsBackgroundIntervalMs)); }
    public decimal? StatsRetentionDays { get => _statsRetentionDays; set => SetField(ref _statsRetentionDays, value, nameof(StatsRetentionDays)); }
    public decimal? StatsPersistIntervalMinutes { get => _statsPersistIntervalMinutes; set => SetField(ref _statsPersistIntervalMinutes, value, nameof(StatsPersistIntervalMinutes)); }
    public decimal? StatsMaxSources { get => _statsMaxSources; set => SetField(ref _statsMaxSources, value, nameof(StatsMaxSources)); }

    public bool StatsTrayTooltip
    {
        get => _statsTrayTooltip;
        set { SetField(ref _statsTrayTooltip, value, nameof(StatsTrayTooltip)); RaiseStatisticsDependencyChanged(); }
    }

    public bool StatsTrayMenu { get => _statsTrayMenu; set => SetField(ref _statsTrayMenu, value, nameof(StatsTrayMenu)); }

    public bool StatsPersist
    {
        get => _statsPersist;
        set { SetField(ref _statsPersist, value, nameof(StatsPersist)); RaiseStatisticsDependencyChanged(); }
    }

    // Dependencies. A row that depends on a switch is unavailable while the switch is off; its tooltip (also on a disabled
    // control) and its help text name the switch. The tip is null while the row is available, so no empty tooltip appears.
    public bool StatsRetentionDaysEnabled => StatsPersist;
    public bool StatsPersistIntervalMinutesEnabled => StatsPersist;
    public bool StatsBackgroundIntervalMsEnabled => StatsTrayTooltip;
    private string StatsPersistReason => Texts.T("stats.settings.needsOption", StatsPersistLabel);
    private string StatsTrayTooltipReason => Texts.T("stats.settings.needsOption", StatsTrayTooltipLabel);
    public string? StatsRetentionDaysReason => StatsPersist ? null : StatsPersistReason;
    public string? StatsPersistIntervalMinutesReason => StatsPersist ? null : StatsPersistReason;
    public string? StatsBackgroundIntervalMsReason => StatsTrayTooltip ? null : StatsTrayTooltipReason;

    public string StatsSampleIntervalMsLabel => Texts.T("stats.settings.sampleInterval");
    public string StatsHistorySamplesLabel => Texts.T("stats.settings.historySamples");
    public string StatsBackgroundIntervalMsLabel => Texts.T("stats.settings.backgroundInterval");
    public string StatsTrayTooltipLabel => Texts.T("stats.settings.trayTooltip");
    public string StatsTrayMenuLabel => Texts.T("stats.settings.trayMenu");
    public string StatsPersistLabel => Texts.T("stats.settings.persist");
    public string StatsRetentionDaysLabel => Texts.T("stats.settings.retentionDays");
    public string StatsPersistIntervalMinutesLabel => Texts.T("stats.settings.persistInterval");
    public string StatsMaxSourcesLabel => Texts.T("stats.settings.maxSources");

    public string StatsSampleIntervalMsHint => Texts.T("stats.settings.sampleIntervalHint", Config.StatsSampleIntervalMsMinimum, Config.StatsSampleIntervalMsMaximum);
    public string StatsHistorySamplesHint => Texts.T("stats.settings.historySamplesHint", Config.StatsHistorySamplesMinimum, Config.StatsHistorySamplesMaximum);
    public string StatsBackgroundIntervalMsHint => Texts.T("stats.settings.backgroundIntervalHint", Config.StatsBackgroundIntervalMsMinimum, Config.StatsBackgroundIntervalMsMaximum);
    public string StatsTrayTooltipHint => Texts.T("stats.settings.trayTooltipHint");
    public string StatsTrayMenuHint => Texts.T("stats.settings.trayMenuHint");
    public string StatsPersistHint => Texts.T("stats.settings.persistHint");
    public string StatsRetentionDaysHint => Texts.T("stats.settings.retentionDaysHint", Config.StatsRetentionDaysMinimum, Config.StatsRetentionDaysMaximum);
    public string StatsPersistIntervalMinutesHint => Texts.T("stats.settings.persistIntervalHint", Config.StatsPersistIntervalMinutesMinimum, Config.StatsPersistIntervalMinutesMaximum);
    public string StatsMaxSourcesHint => Texts.T("stats.settings.maxSourcesHint", Config.StatsMaxSourcesMinimum, Config.StatsMaxSourcesMaximum);

    // Assistive-technology help: the unit, the hint with its range and, while the row is unavailable, the reason.
    private string StatisticsHelp(string field, string unitKey, string hint, string? reason)
        => FieldHelp(field, Texts.T(unitKey) + ". " + hint + (reason == null ? string.Empty : " " + reason));
    public string StatsSampleIntervalMsHelp => StatisticsHelp(nameof(StatsSampleIntervalMsInput), "stats.settings.unitMs", StatsSampleIntervalMsHint, null);
    public string StatsHistorySamplesHelp => StatisticsHelp(nameof(StatsHistorySamplesInput), "stats.settings.unitReadings", StatsHistorySamplesHint, null);
    public string StatsBackgroundIntervalMsHelp => StatisticsHelp(nameof(StatsBackgroundIntervalMsInput), "stats.settings.unitMs", StatsBackgroundIntervalMsHint, StatsBackgroundIntervalMsReason);
    public string StatsRetentionDaysHelp => StatisticsHelp(nameof(StatsRetentionDaysInput), "stats.settings.unitDays", StatsRetentionDaysHint, StatsRetentionDaysReason);
    public string StatsPersistIntervalMinutesHelp => StatisticsHelp(nameof(StatsPersistIntervalMinutesInput), "settings.unitMinutes", StatsPersistIntervalMinutesHint, StatsPersistIntervalMinutesReason);
    public string StatsMaxSourcesHelp => StatisticsHelp(nameof(StatsMaxSourcesInput), "stats.settings.unitSources", StatsMaxSourcesHint, null);

    private static readonly string[] StatisticsHelpProperties =
    {
        nameof(StatsSampleIntervalMsHelp), nameof(StatsHistorySamplesHelp), nameof(StatsBackgroundIntervalMsHelp),
        nameof(StatsRetentionDaysHelp), nameof(StatsPersistIntervalMinutesHelp), nameof(StatsMaxSourcesHelp)
    };

    private static readonly string[] StatisticsTextProperties =
    {
        nameof(StatisticsTitle), nameof(StatisticsIntro),
        nameof(StatsSampleIntervalMsLabel), nameof(StatsHistorySamplesLabel), nameof(StatsBackgroundIntervalMsLabel), nameof(StatsTrayTooltipLabel),
        nameof(StatsTrayMenuLabel), nameof(StatsPersistLabel), nameof(StatsRetentionDaysLabel), nameof(StatsPersistIntervalMinutesLabel), nameof(StatsMaxSourcesLabel),
        nameof(StatsSampleIntervalMsHint), nameof(StatsHistorySamplesHint), nameof(StatsBackgroundIntervalMsHint), nameof(StatsTrayTooltipHint),
        nameof(StatsTrayMenuHint), nameof(StatsPersistHint), nameof(StatsRetentionDaysHint), nameof(StatsPersistIntervalMinutesHint), nameof(StatsMaxSourcesHint),
        nameof(StatsRetentionDaysReason), nameof(StatsPersistIntervalMinutesReason), nameof(StatsBackgroundIntervalMsReason)
    };

    private void Raise(IEnumerable<string> properties)
    {
        if (_isClosed) return;
        foreach (string property in properties) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    private void RaiseStatisticsDependencyChanged() => Raise(new[]
    {
        nameof(StatsRetentionDaysEnabled), nameof(StatsPersistIntervalMinutesEnabled), nameof(StatsBackgroundIntervalMsEnabled),
        nameof(StatsRetentionDaysReason), nameof(StatsPersistIntervalMinutesReason), nameof(StatsBackgroundIntervalMsReason)
    }.Concat(StatisticsHelpProperties));

    /// <summary>A language change refreshes text only; it is not a change of any value.</summary>
    private void RaiseStatisticsTextsChanged() => Raise(StatisticsTextProperties.Concat(StatisticsHelpProperties));

    private void NotifyStatisticsHelpChanged() => Raise(StatisticsHelpProperties);

    /// <summary>Loads the nine values from <paramref name="config"/> into the draft and the inputs (start, discard, after a save).</summary>
    private void LoadStatisticsDraft(Config config)
    {
        StatsSampleIntervalMs = config.StatsSampleIntervalMs;
        StatsHistorySamples = config.StatsHistorySamples;
        StatsBackgroundIntervalMs = config.StatsBackgroundIntervalMs;
        StatsTrayTooltip = config.StatsTrayTooltip;
        StatsTrayMenu = config.StatsTrayMenu;
        StatsHeaderIcons = config.StatsHeaderIcons;
        StatsPersist = config.StatsPersist;
        StatsRetentionDays = config.StatsRetentionDays;
        StatsPersistIntervalMinutes = config.StatsPersistIntervalMinutes;
        StatsMaxSources = config.StatsMaxSources;
        ResetNumber(StatsSampleIntervalMsInput, StatsSampleIntervalMs.Value);
        ResetNumber(StatsHistorySamplesInput, StatsHistorySamples.Value);
        ResetNumber(StatsBackgroundIntervalMsInput, StatsBackgroundIntervalMs.Value);
        ResetNumber(StatsRetentionDaysInput, StatsRetentionDays.Value);
        ResetNumber(StatsPersistIntervalMinutesInput, StatsPersistIntervalMinutes.Value);
        ResetNumber(StatsMaxSourcesInput, StatsMaxSources.Value);
    }

    /// <summary>Copies the draft into the candidate. A row that is unavailable keeps its draft when that is valid and the saved value otherwise.</summary>
    private void ApplyStatisticsDraft(Config candidate, Config saved)
    {
        candidate.StatsSampleIntervalMs = GetInteger(StatsSampleIntervalMs, StatsSampleIntervalMsLabel, Config.StatsSampleIntervalMsMinimum, Config.StatsSampleIntervalMsMaximum);
        candidate.StatsHistorySamples = GetInteger(StatsHistorySamples, StatsHistorySamplesLabel, Config.StatsHistorySamplesMinimum, Config.StatsHistorySamplesMaximum);
        candidate.StatsBackgroundIntervalMs = StatisticsNumber(StatsBackgroundIntervalMs, StatsBackgroundIntervalMsLabel, Config.StatsBackgroundIntervalMsMinimum, Config.StatsBackgroundIntervalMsMaximum, StatsBackgroundIntervalMsEnabled, saved.StatsBackgroundIntervalMs);
        candidate.StatsTrayTooltip = StatsTrayTooltip;
        candidate.StatsTrayMenu = StatsTrayMenu;
        candidate.StatsHeaderIcons = StatsHeaderIcons;
        candidate.StatsPersist = StatsPersist;
        candidate.StatsRetentionDays = StatisticsNumber(StatsRetentionDays, StatsRetentionDaysLabel, Config.StatsRetentionDaysMinimum, Config.StatsRetentionDaysMaximum, StatsRetentionDaysEnabled, saved.StatsRetentionDays);
        candidate.StatsPersistIntervalMinutes = StatisticsNumber(StatsPersistIntervalMinutes, StatsPersistIntervalMinutesLabel, Config.StatsPersistIntervalMinutesMinimum, Config.StatsPersistIntervalMinutesMaximum, StatsPersistIntervalMinutesEnabled, saved.StatsPersistIntervalMinutes);
        candidate.StatsMaxSources = GetInteger(StatsMaxSources, StatsMaxSourcesLabel, Config.StatsMaxSourcesMinimum, Config.StatsMaxSourcesMaximum);
    }

    private static int StatisticsNumber(decimal? draft, string label, int minimum, int maximum, bool enabled, int saved)
    {
        if (enabled) return GetInteger(draft, label, minimum, maximum);
        bool valid = draft.HasValue && draft >= minimum && draft <= maximum && decimal.Truncate(draft.Value) == draft.Value;
        return valid ? decimal.ToInt32(draft!.Value) : saved;
    }

    /// <summary>False for a numeric row that is unavailable: its text is not validated, because the user cannot correct it there.</summary>
    private bool IsStatisticsFieldActive(Control input) => input.IsEnabled;

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> StatisticsNumericFields()
    {
        yield return (StatsSampleIntervalMsInput, StatsSampleIntervalMs, StatsSampleIntervalMsLabel, Texts.T("stats.settings.unitMs"),
            Config.StatsSampleIntervalMsMinimum, Config.StatsSampleIntervalMsMaximum, true);
        yield return (StatsHistorySamplesInput, StatsHistorySamples, StatsHistorySamplesLabel, Texts.T("stats.settings.unitReadings"),
            Config.StatsHistorySamplesMinimum, Config.StatsHistorySamplesMaximum, true);
        yield return (StatsBackgroundIntervalMsInput, StatsBackgroundIntervalMs, StatsBackgroundIntervalMsLabel, Texts.T("stats.settings.unitMs"),
            Config.StatsBackgroundIntervalMsMinimum, Config.StatsBackgroundIntervalMsMaximum, true);
        yield return (StatsRetentionDaysInput, StatsRetentionDays, StatsRetentionDaysLabel, Texts.T("stats.settings.unitDays"),
            Config.StatsRetentionDaysMinimum, Config.StatsRetentionDaysMaximum, true);
        yield return (StatsPersistIntervalMinutesInput, StatsPersistIntervalMinutes, StatsPersistIntervalMinutesLabel, Texts.T("settings.unitMinutes"),
            Config.StatsPersistIntervalMinutesMinimum, Config.StatsPersistIntervalMinutesMaximum, true);
        yield return (StatsMaxSourcesInput, StatsMaxSources, StatsMaxSourcesLabel, Texts.T("stats.settings.unitSources"),
            Config.StatsMaxSourcesMinimum, Config.StatsMaxSourcesMaximum, true);
    }
}
