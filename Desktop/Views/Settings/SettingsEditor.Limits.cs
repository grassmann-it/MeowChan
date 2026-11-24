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

    private decimal? _exportHistorySizeMiB = Config.ExportHistorySizeMiBDefault;
    public decimal? ExportHistorySizeMiB { get => _exportHistorySizeMiB; set => SetField(ref _exportHistorySizeMiB, value, nameof(ExportHistorySizeMiB)); }
    public decimal ExportHistorySizeMiBMinimum => Config.ExportHistorySizeMiBMinimum;
    public decimal ExportHistorySizeMiBMaximum => Config.ExportHistorySizeMiBMaximum;
    public string ExportHistorySizeMiBLabel => Texts.T("budgets.exportHistorySizeMiB");
    public string ExportHistorySizeMiBHint => Texts.T("budgets.exportHistorySizeMiBHint", Config.ExportHistorySizeMiBDefault, Config.ExportHistorySizeMiBMinimum, Config.ExportHistorySizeMiBMaximum);
    public string ExportHistorySizeMiBHelp => FieldHelp(nameof(ExportHistorySizeMiBInput), Texts.T("settings.unitMiB") + ". " + ExportHistorySizeMiBHint);

    private decimal? _exportHistoryEntryLimit = Config.ExportHistoryEntryLimitDefault;
    public decimal? ExportHistoryEntryLimit { get => _exportHistoryEntryLimit; set => SetField(ref _exportHistoryEntryLimit, value, nameof(ExportHistoryEntryLimit)); }
    public decimal ExportHistoryEntryLimitMinimum => Config.ExportHistoryEntryLimitMinimum;
    public decimal ExportHistoryEntryLimitMaximum => Config.ExportHistoryEntryLimitMaximum;
    public string ExportHistoryEntryLimitLabel => Texts.T("budgets.exportHistoryEntryLimit");
    public string ExportHistoryEntryLimitHint => Texts.T("budgets.exportHistoryEntryLimitHint", Config.ExportHistoryEntryLimitDefault, Config.ExportHistoryEntryLimitMinimum, Config.ExportHistoryEntryLimitMaximum);
    public string ExportHistoryEntryLimitHelp => FieldHelp(nameof(ExportHistoryEntryLimitInput), Texts.T("settings.unitCount") + ". " + ExportHistoryEntryLimitHint);

    private decimal? _boardAdmissionSizeMiB = Config.BoardAdmissionSizeMiBDefault;
    public decimal? BoardAdmissionSizeMiB { get => _boardAdmissionSizeMiB; set => SetField(ref _boardAdmissionSizeMiB, value, nameof(BoardAdmissionSizeMiB)); }
    public decimal BoardAdmissionSizeMiBMinimum => Config.BoardAdmissionSizeMiBMinimum;
    public decimal BoardAdmissionSizeMiBMaximum => Config.BoardAdmissionSizeMiBMaximum;
    public string BoardAdmissionSizeMiBLabel => Texts.T("budgets.boardAdmissionSizeMiB");
    public string BoardAdmissionSizeMiBHint => Texts.T("budgets.boardAdmissionSizeMiBHint", Config.BoardAdmissionSizeMiBDefault, Config.BoardAdmissionSizeMiBMinimum, Config.BoardAdmissionSizeMiBMaximum);
    public string BoardAdmissionSizeMiBHelp => FieldHelp(nameof(BoardAdmissionSizeMiBInput), Texts.T("settings.unitMiB") + ". " + BoardAdmissionSizeMiBHint);

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
        ExportHistorySizeMiB = config.ExportHistorySizeMiB;
        ResetNumber(ExportHistorySizeMiBInput, config.ExportHistorySizeMiB);
        ExportHistoryEntryLimit = config.ExportHistoryEntryLimit;
        ResetNumber(ExportHistoryEntryLimitInput, config.ExportHistoryEntryLimit);
        BoardAdmissionSizeMiB = config.BoardAdmissionSizeMiB;
        ResetNumber(BoardAdmissionSizeMiBInput, config.BoardAdmissionSizeMiB);
        SlashTagSuggestionCount = config.SlashTagSuggestionCount;
        ResetNumber(SlashTagSuggestionCountInput, config.SlashTagSuggestionCount);
        VideoScrubCacheLimitMiB = config.VideoScrubCacheLimitMiB;
        ResetNumber(VideoScrubCacheLimitMiBInput, config.VideoScrubCacheLimitMiB);
    }

    private void ApplyLimitsDraft(Config candidate)
    {
        candidate.ArchiveTagFilterTagsPerList = GetInteger(ArchiveTagFilterTagsPerList, ArchiveTagFilterTagsPerListLabel, Config.ArchiveTagFilterTagsPerListMinimum, Config.ArchiveTagFilterTagsPerListMaximum);
        candidate.UrlTextExportLimitMiB = GetInteger(UrlTextExportLimitMiB, UrlTextExportLimitMiBLabel, Config.UrlTextExportLimitMiBMinimum, Config.UrlTextExportLimitMiBMaximum);
        candidate.ExportHistorySizeMiB = GetInteger(ExportHistorySizeMiB, ExportHistorySizeMiBLabel, Config.ExportHistorySizeMiBMinimum, Config.ExportHistorySizeMiBMaximum);
        candidate.ExportHistoryEntryLimit = GetInteger(ExportHistoryEntryLimit, ExportHistoryEntryLimitLabel, Config.ExportHistoryEntryLimitMinimum, Config.ExportHistoryEntryLimitMaximum);
        candidate.BoardAdmissionSizeMiB = GetInteger(BoardAdmissionSizeMiB, BoardAdmissionSizeMiBLabel, Config.BoardAdmissionSizeMiBMinimum, Config.BoardAdmissionSizeMiBMaximum);
        candidate.SlashTagSuggestionCount = GetInteger(SlashTagSuggestionCount, SlashTagSuggestionCountLabel, Config.SlashTagSuggestionCountMinimum, Config.SlashTagSuggestionCountMaximum);
        candidate.VideoScrubCacheLimitMiB = GetInteger(VideoScrubCacheLimitMiB, VideoScrubCacheLimitMiBLabel, Config.VideoScrubCacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMaximum);
        candidate.ShrinkWindowForBarebones = ShrinkWindowForBarebones;
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> LimitsNumericFields()
    {
        yield return (ArchiveTagFilterTagsPerListInput, ArchiveTagFilterTagsPerList, ArchiveTagFilterTagsPerListLabel, Texts.T("settings.unitCount"), Config.ArchiveTagFilterTagsPerListMinimum, Config.ArchiveTagFilterTagsPerListMaximum, true);
        yield return (UrlTextExportLimitMiBInput, UrlTextExportLimitMiB, UrlTextExportLimitMiBLabel, Texts.T("settings.unitMiB"), Config.UrlTextExportLimitMiBMinimum, Config.UrlTextExportLimitMiBMaximum, true);
        yield return (ExportHistorySizeMiBInput, ExportHistorySizeMiB, ExportHistorySizeMiBLabel, Texts.T("settings.unitMiB"), Config.ExportHistorySizeMiBMinimum, Config.ExportHistorySizeMiBMaximum, true);
        yield return (ExportHistoryEntryLimitInput, ExportHistoryEntryLimit, ExportHistoryEntryLimitLabel, Texts.T("settings.unitCount"), Config.ExportHistoryEntryLimitMinimum, Config.ExportHistoryEntryLimitMaximum, true);
        yield return (BoardAdmissionSizeMiBInput, BoardAdmissionSizeMiB, BoardAdmissionSizeMiBLabel, Texts.T("settings.unitMiB"), Config.BoardAdmissionSizeMiBMinimum, Config.BoardAdmissionSizeMiBMaximum, true);
        yield return (SlashTagSuggestionCountInput, SlashTagSuggestionCount, SlashTagSuggestionCountLabel, Texts.T("settings.unitCount"), Config.SlashTagSuggestionCountMinimum, Config.SlashTagSuggestionCountMaximum, true);
        yield return (VideoScrubCacheLimitMiBInput, VideoScrubCacheLimitMiB, VideoScrubCacheLimitMiBLabel, Texts.T("settings.unitMiB"), Config.VideoScrubCacheLimitMiBMinimum, Config.VideoScrubCacheLimitMiBMaximum, true);
    }

    private static readonly string[] LimitsHelpProperties =
    [
        nameof(ArchiveTagFilterTagsPerListHelp),
        nameof(UrlTextExportLimitMiBHelp),
        nameof(ExportHistorySizeMiBHelp),
        nameof(ExportHistoryEntryLimitHelp),
        nameof(BoardAdmissionSizeMiBHelp),
        nameof(SlashTagSuggestionCountHelp),
        nameof(VideoScrubCacheLimitMiBHelp),
    ];

    private void NotifyLimitsHelpChanged() => Raise(LimitsHelpProperties);
    private void RaiseLimitsTextsChanged() => Raise(new[]
    {
        nameof(ArchiveTagFilterTagsPerListLabel), nameof(ArchiveTagFilterTagsPerListHint),
        nameof(UrlTextExportLimitMiBLabel), nameof(UrlTextExportLimitMiBHint),
        nameof(ExportHistorySizeMiBLabel), nameof(ExportHistorySizeMiBHint),
        nameof(ExportHistoryEntryLimitLabel), nameof(ExportHistoryEntryLimitHint),
        nameof(BoardAdmissionSizeMiBLabel), nameof(BoardAdmissionSizeMiBHint),
        nameof(SlashTagSuggestionCountLabel), nameof(SlashTagSuggestionCountHint),
        nameof(VideoScrubCacheLimitMiBLabel), nameof(VideoScrubCacheLimitMiBHint),
        nameof(ShrinkWindowForBarebonesLabel), nameof(ShrinkWindowForBarebonesHint)
    }.Concat(LimitsHelpProperties));
}
