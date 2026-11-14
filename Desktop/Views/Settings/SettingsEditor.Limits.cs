using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>SP5 and scrub-cache settings use the existing raw-text validation and draft commit path.</summary>
public partial class SettingsEditor
{
    private decimal? _archiveTagFilterTagsPerList = Config.ArchiveTagFilterTagsPerListDefault;
    public decimal? ArchiveTagFilterTagsPerList { get => _archiveTagFilterTagsPerList; set => SetField(ref _archiveTagFilterTagsPerList, value, nameof(ArchiveTagFilterTagsPerList)); }
    public decimal ArchiveTagFilterTagsPerListMinimum => Config.ArchiveTagFilterTagsPerListMinimum;
    public decimal ArchiveTagFilterTagsPerListMaximum => Config.ArchiveTagFilterTagsPerListMaximum;
    public string ArchiveTagFilterTagsPerListLabel => Texts.T("budgets.archiveTagFilterTagsPerList");
    public string ArchiveTagFilterTagsPerListHint => Texts.T("budgets.archiveTagFilterTagsPerListHint", Config.ArchiveTagFilterTagsPerListDefault, Config.ArchiveTagFilterTagsPerListMinimum, Config.ArchiveTagFilterTagsPerListMaximum);
    public string ArchiveTagFilterTagsPerListHelp => FieldHelp(nameof(ArchiveTagFilterTagsPerListInput), Texts.T("settings.unitCount") + ". " + ArchiveTagFilterTagsPerListHint);

    private decimal? _urlTextExportLimitMiB = Config.UrlTextExportLimitMiBDefault;
    public decimal? UrlTextExportLimitMiB { get => _urlTextExportLimitMiB; set => SetField(ref _urlTextExportLimitMiB, value, nameof(UrlTextExportLimitMiB)); }
    public decimal UrlTextExportLimitMiBMinimum => Config.UrlTextExportLimitMiBMinimum;
    public decimal UrlTextExportLimitMiBMaximum => Config.UrlTextExportLimitMiBMaximum;
    public string UrlTextExportLimitMiBLabel => Texts.T("budgets.urlTextExportLimitMiB");
    public string UrlTextExportLimitMiBHint => Texts.T("budgets.urlTextExportLimitMiBHint", Config.UrlTextExportLimitMiBDefault, Config.UrlTextExportLimitMiBMinimum, Config.UrlTextExportLimitMiBMaximum);
    public string UrlTextExportLimitMiBHelp => FieldHelp(nameof(UrlTextExportLimitMiBInput), Texts.T("settings.unitMiB") + ". " + UrlTextExportLimitMiBHint);

    private decimal? _slashTagSuggestionCount = Config.SlashTagSuggestionCountDefault;
    public decimal? SlashTagSuggestionCount { get => _slashTagSuggestionCount; set => SetField(ref _slashTagSuggestionCount, value, nameof(SlashTagSuggestionCount)); }
    public decimal SlashTagSuggestionCountMinimum => Config.SlashTagSuggestionCountMinimum;
    public decimal SlashTagSuggestionCountMaximum => Config.SlashTagSuggestionCountMaximum;
    public string SlashTagSuggestionCountLabel => Texts.T("budgets.slashTagSuggestionCount");
    public string SlashTagSuggestionCountHint => Texts.T("budgets.slashTagSuggestionCountHint", Config.SlashTagSuggestionCountDefault, Config.SlashTagSuggestionCountMinimum, Config.SlashTagSuggestionCountMaximum);
    public string SlashTagSuggestionCountHelp => FieldHelp(nameof(SlashTagSuggestionCountInput), Texts.T("settings.unitCount") + ". " + SlashTagSuggestionCountHint);

    private decimal? _videoScrubCacheLimitMiB = Config.VideoScrubCacheLimitMiBDefault;
    public decimal? VideoScrubCacheLimitMiB { get => _videoScrubCacheLimitMiB; set => SetField(ref _videoScrubCacheLimitMiB, value, nameof(VideoScrubCacheLimitMiB)); }
    public decimal VideoScrubCacheLimitMiBMinimum => Config.VideoScrubCacheLimitMiBMinimum;
    public decimal VideoScrubCacheLimitMiBMaximum => Config.VideoScrubCacheLimitMiBMaximum;
    public string VideoScrubCacheLimitMiBLabel => Texts.T("budgets.videoScrubCacheMiB");
    public string VideoScrubCacheLimitMiBHint => Texts.T("budgets.videoScrubCacheMiBHint", Config.VideoScrubCacheLimitMiBDefault, Config.VideoScrubCacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMaximum);
    public string VideoScrubCacheLimitMiBHelp => FieldHelp(nameof(VideoScrubCacheLimitMiBInput), Texts.T("settings.unitMiB") + ". " + VideoScrubCacheLimitMiBHint);

    private bool _shrinkWindowForBarebones;
    public bool ShrinkWindowForBarebones { get => _shrinkWindowForBarebones; set => SetField(ref _shrinkWindowForBarebones, value, nameof(ShrinkWindowForBarebones)); }
    public string ShrinkWindowForBarebonesLabel => Texts.T("settings.shrinkWindowForBarebones");
    public string ShrinkWindowForBarebonesHint => Texts.T("settings.shrinkWindowForBarebonesHint");

    private void LoadLimitsDraft(Config config)
    {
        LoadLimitNumbersDraft(config);
        ShrinkWindowForBarebones = config.ShrinkWindowForBarebones;
    }

    // Separate category resets must not reset the Interface switch with the storage limits.
    private void LoadLimitNumbersDraft(Config config)
    {
        ArchiveTagFilterTagsPerList = config.ArchiveTagFilterTagsPerList;
        ResetNumber(ArchiveTagFilterTagsPerListInput, config.ArchiveTagFilterTagsPerList);
        UrlTextExportLimitMiB = config.UrlTextExportLimitMiB;
        ResetNumber(UrlTextExportLimitMiBInput, config.UrlTextExportLimitMiB);
        SlashTagSuggestionCount = config.SlashTagSuggestionCount;
        ResetNumber(SlashTagSuggestionCountInput, config.SlashTagSuggestionCount);
        VideoScrubCacheLimitMiB = config.VideoScrubCacheLimitMiB;
        ResetNumber(VideoScrubCacheLimitMiBInput, config.VideoScrubCacheLimitMiB);
    }

    private void ApplyLimitsDraft(Config candidate)
    {
        candidate.ArchiveTagFilterTagsPerList = GetInteger(ArchiveTagFilterTagsPerList, ArchiveTagFilterTagsPerListLabel, Config.ArchiveTagFilterTagsPerListMinimum, Config.ArchiveTagFilterTagsPerListMaximum);
        candidate.UrlTextExportLimitMiB = GetInteger(UrlTextExportLimitMiB, UrlTextExportLimitMiBLabel, Config.UrlTextExportLimitMiBMinimum, Config.UrlTextExportLimitMiBMaximum);
        candidate.SlashTagSuggestionCount = GetInteger(SlashTagSuggestionCount, SlashTagSuggestionCountLabel, Config.SlashTagSuggestionCountMinimum, Config.SlashTagSuggestionCountMaximum);
        candidate.VideoScrubCacheLimitMiB = GetInteger(VideoScrubCacheLimitMiB, VideoScrubCacheLimitMiBLabel, Config.VideoScrubCacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMaximum);
        candidate.ShrinkWindowForBarebones = ShrinkWindowForBarebones;
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> LimitsNumericFields()
    {
        yield return (ArchiveTagFilterTagsPerListInput, ArchiveTagFilterTagsPerList, ArchiveTagFilterTagsPerListLabel, Texts.T("settings.unitCount"), Config.ArchiveTagFilterTagsPerListMinimum, Config.ArchiveTagFilterTagsPerListMaximum, true);
        yield return (UrlTextExportLimitMiBInput, UrlTextExportLimitMiB, UrlTextExportLimitMiBLabel, Texts.T("settings.unitMiB"), Config.UrlTextExportLimitMiBMinimum, Config.UrlTextExportLimitMiBMaximum, true);
        yield return (SlashTagSuggestionCountInput, SlashTagSuggestionCount, SlashTagSuggestionCountLabel, Texts.T("settings.unitCount"), Config.SlashTagSuggestionCountMinimum, Config.SlashTagSuggestionCountMaximum, true);
        yield return (VideoScrubCacheLimitMiBInput, VideoScrubCacheLimitMiB, VideoScrubCacheLimitMiBLabel, Texts.T("settings.unitMiB"), Config.VideoScrubCacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMaximum, true);
    }

    private static readonly string[] LimitsHelpProperties =
    [
        nameof(ArchiveTagFilterTagsPerListHelp),
        nameof(UrlTextExportLimitMiBHelp),
        nameof(SlashTagSuggestionCountHelp),
        nameof(VideoScrubCacheLimitMiBHelp),
    ];

    private void NotifyLimitsHelpChanged() => Raise(LimitsHelpProperties);
    private void RaiseLimitsTextsChanged() => Raise(new[]
    {
        nameof(ArchiveTagFilterTagsPerListLabel), nameof(ArchiveTagFilterTagsPerListHint),
        nameof(UrlTextExportLimitMiBLabel), nameof(UrlTextExportLimitMiBHint),
        nameof(SlashTagSuggestionCountLabel), nameof(SlashTagSuggestionCountHint),
        nameof(VideoScrubCacheLimitMiBLabel), nameof(VideoScrubCacheLimitMiBHint),
        nameof(ShrinkWindowForBarebonesLabel), nameof(ShrinkWindowForBarebonesHint)
    }.Concat(LimitsHelpProperties));
}
