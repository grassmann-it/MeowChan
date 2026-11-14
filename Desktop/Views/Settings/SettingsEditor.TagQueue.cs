using System.Collections.Generic;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

// The four limits of the tagging queue (entries, file size, kept finished entries, confirmation threshold) in the Storage and limits
// group. Same pattern as the reader limits: the ranges come from Config, a lowered limit never drops existing entries.
public partial class SettingsEditor
{
    public int TagQueueLimitMinimum => Config.TagQueueLimitMinimum;
    public int TagQueueLimitMaximum => Config.TagQueueLimitMaximum;
    public int TagQueueFileLimitMiBMinimum => Config.TagQueueFileLimitMiBMinimum;
    public int TagQueueFileLimitMiBMaximum => Config.TagQueueFileLimitMiBMaximum;
    public int TagQueueHistoryMinimum => Config.TagQueueHistoryMinimum;
    public int TagQueueHistoryMaximum => Config.TagQueueHistoryMaximum;
    public int TagQueueConfirmAboveMinimum => Config.TagQueueConfirmAboveMinimum;
    public int TagQueueConfirmAboveMaximum => Config.TagQueueConfirmAboveMaximum;

    private decimal? _tagQueueLimit;
    public decimal? TagQueueLimit { get => _tagQueueLimit; set => SetField(ref _tagQueueLimit, value, nameof(TagQueueLimit)); }
    public string TagQueueLimitLabel => Texts.T("settings.limits.tagQueue");
    public string TagQueueLimitHint => Texts.T("settings.limits.tagQueue.hint") + " " + Texts.T("settings.reader.range", Config.TagQueueLimitMinimum, Config.TagQueueLimitMaximum);
    public string TagQueueLimitHelp => FieldHelp(nameof(TagQueueLimitInput), TagQueueLimitHint);

    private decimal? _tagQueueFileLimitMiB;
    public decimal? TagQueueFileLimitMiB { get => _tagQueueFileLimitMiB; set => SetField(ref _tagQueueFileLimitMiB, value, nameof(TagQueueFileLimitMiB)); }
    public string TagQueueFileLimitMiBLabel => Texts.T("settings.limits.tagQueueFile");
    public string TagQueueFileLimitMiBHint => Texts.T("settings.limits.tagQueueFile.hint") + " " + Texts.T("settings.reader.range", Config.TagQueueFileLimitMiBMinimum, Config.TagQueueFileLimitMiBMaximum);
    public string TagQueueFileLimitMiBHelp => FieldHelp(nameof(TagQueueFileLimitMiBInput), TagQueueFileLimitMiBHint);

    private decimal? _tagQueueHistory;
    public decimal? TagQueueHistory { get => _tagQueueHistory; set => SetField(ref _tagQueueHistory, value, nameof(TagQueueHistory)); }
    public string TagQueueHistoryLabel => Texts.T("settings.limits.tagQueueHistory");
    public string TagQueueHistoryHint => Texts.T("settings.limits.tagQueueHistory.hint") + " " + Texts.T("settings.reader.range", Config.TagQueueHistoryMinimum, Config.TagQueueHistoryMaximum);
    public string TagQueueHistoryHelp => FieldHelp(nameof(TagQueueHistoryInput), TagQueueHistoryHint);

    private decimal? _tagQueueConfirmAbove;
    public decimal? TagQueueConfirmAbove { get => _tagQueueConfirmAbove; set => SetField(ref _tagQueueConfirmAbove, value, nameof(TagQueueConfirmAbove)); }
    public string TagQueueConfirmAboveLabel => Texts.T("settings.limits.tagQueueConfirm");
    public string TagQueueConfirmAboveHint => Texts.T("settings.limits.tagQueueConfirm.hint") + " " + Texts.T("settings.reader.range", Config.TagQueueConfirmAboveMinimum, Config.TagQueueConfirmAboveMaximum);
    public string TagQueueConfirmAboveHelp => FieldHelp(nameof(TagQueueConfirmAboveInput), TagQueueConfirmAboveHint);

    private void LoadTagQueueDraft(Config config)
    {
        TagQueueLimit = config.TagQueueLimit;
        ResetNumber(TagQueueLimitInput, TagQueueLimit.Value);
        TagQueueFileLimitMiB = config.TagQueueFileLimitMiB;
        ResetNumber(TagQueueFileLimitMiBInput, TagQueueFileLimitMiB.Value);
        TagQueueHistory = config.TagQueueHistory;
        ResetNumber(TagQueueHistoryInput, TagQueueHistory.Value);
        TagQueueConfirmAbove = config.TagQueueConfirmAbove;
        ResetNumber(TagQueueConfirmAboveInput, TagQueueConfirmAbove.Value);
    }

    private void ApplyTagQueueDraft(Config candidate)
    {
        candidate.TagQueueLimit = GetInteger(TagQueueLimit, TagQueueLimitLabel, Config.TagQueueLimitMinimum, Config.TagQueueLimitMaximum);
        candidate.TagQueueFileLimitMiB = GetInteger(TagQueueFileLimitMiB, TagQueueFileLimitMiBLabel, Config.TagQueueFileLimitMiBMinimum, Config.TagQueueFileLimitMiBMaximum);
        candidate.TagQueueHistory = GetInteger(TagQueueHistory, TagQueueHistoryLabel, Config.TagQueueHistoryMinimum, Config.TagQueueHistoryMaximum);
        candidate.TagQueueConfirmAbove = GetInteger(TagQueueConfirmAbove, TagQueueConfirmAboveLabel, Config.TagQueueConfirmAboveMinimum, Config.TagQueueConfirmAboveMaximum);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> TagQueueNumericFields()
    {
        yield return (TagQueueLimitInput, TagQueueLimit, TagQueueLimitLabel, string.Empty, Config.TagQueueLimitMinimum, Config.TagQueueLimitMaximum, true);
        yield return (TagQueueFileLimitMiBInput, TagQueueFileLimitMiB, TagQueueFileLimitMiBLabel, string.Empty, Config.TagQueueFileLimitMiBMinimum, Config.TagQueueFileLimitMiBMaximum, true);
        yield return (TagQueueHistoryInput, TagQueueHistory, TagQueueHistoryLabel, string.Empty, Config.TagQueueHistoryMinimum, Config.TagQueueHistoryMaximum, true);
        yield return (TagQueueConfirmAboveInput, TagQueueConfirmAbove, TagQueueConfirmAboveLabel, string.Empty, Config.TagQueueConfirmAboveMinimum, Config.TagQueueConfirmAboveMaximum, true);
    }

    private void RaiseTagQueueTextsChanged() => Raise(TagQueueTextProperties);
    private void NotifyTagQueueHelpChanged() => Raise(TagQueueHelpProperties);

    private static readonly string[] TagQueueTextProperties =
    {
        nameof(TagQueueLimitLabel), nameof(TagQueueLimitHint), nameof(TagQueueLimitHelp),
        nameof(TagQueueFileLimitMiBLabel), nameof(TagQueueFileLimitMiBHint), nameof(TagQueueFileLimitMiBHelp),
        nameof(TagQueueHistoryLabel), nameof(TagQueueHistoryHint), nameof(TagQueueHistoryHelp),
        nameof(TagQueueConfirmAboveLabel), nameof(TagQueueConfirmAboveHint), nameof(TagQueueConfirmAboveHelp),
    };
    private static readonly string[] TagQueueHelpProperties =
    {
        nameof(TagQueueLimitHelp), nameof(TagQueueFileLimitMiBHelp), nameof(TagQueueHistoryHelp), nameof(TagQueueConfirmAboveHelp),
    };
}
