using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

/// <summary>
/// The quick tagging limits on the "Storage and limits" page (TP5): suggestions shown, remembered recent tags, undo steps,
/// neighbouring media checked, file name words, source tags per medium and media marked in the gallery. Each is a bounded count
/// with its default, minimum and maximum from <see cref="Config"/>; the consumers (tag panel, tag popover) read them
/// through <see cref="MeowChan.Desktop.QuickTagSettings"/>. Texts are <see cref="QuickTagSettingsCatalog"/>.
/// </summary>
public partial class SettingsEditor
{
    private decimal? _quickTagSuggestionCount = Config.QuickTagSuggestionCountDefault;
    private decimal? _quickTagRecentCount = Config.QuickTagRecentCountDefault;
    private decimal? _quickTagUndoDepth = Config.QuickTagUndoDepthDefault;
    private decimal? _quickTagNeighbourScan = Config.QuickTagNeighbourScanDefault;
    private decimal? _quickTagFilenameTokens = Config.QuickTagFilenameTokensDefault;
    private decimal? _quickTagSourceTagsPerMedium = Config.QuickTagSourceTagsPerMediumDefault;
    private decimal? _quickTagMarkedMediaLimit = Config.QuickTagMarkedMediaLimitDefault;

    public string QuickTagGroupLabel => Texts.T(QuickTagSettingsCatalog.GroupKey);

    public decimal? QuickTagSuggestionCount { get => _quickTagSuggestionCount; set => SetField(ref _quickTagSuggestionCount, value, nameof(QuickTagSuggestionCount)); }
    public string QuickTagSuggestionCountLabel => Texts.T("budgets.quickTagSuggestions");
    public string QuickTagSuggestionCountHint => Texts.T("budgets.quickTagSuggestionsHint", Config.QuickTagSuggestionCountDefault, Config.QuickTagSuggestionCountMinimum, Config.QuickTagSuggestionCountMaximum);
    public string QuickTagSuggestionCountHelp => FieldHelp(nameof(QuickTagSuggestionCountInput), Texts.T("settings.unitCount") + ". " + QuickTagSuggestionCountHint);
    public decimal? QuickTagRecentCount { get => _quickTagRecentCount; set => SetField(ref _quickTagRecentCount, value, nameof(QuickTagRecentCount)); }
    public string QuickTagRecentCountLabel => Texts.T("budgets.quickTagRecent");
    public string QuickTagRecentCountHint => Texts.T("budgets.quickTagRecentHint", Config.QuickTagRecentCountDefault, Config.QuickTagRecentCountMinimum, Config.QuickTagRecentCountMaximum);
    public string QuickTagRecentCountHelp => FieldHelp(nameof(QuickTagRecentCountInput), Texts.T("settings.unitCount") + ". " + QuickTagRecentCountHint);
    public decimal? QuickTagUndoDepth { get => _quickTagUndoDepth; set => SetField(ref _quickTagUndoDepth, value, nameof(QuickTagUndoDepth)); }
    public string QuickTagUndoDepthLabel => Texts.T("budgets.quickTagUndo");
    public string QuickTagUndoDepthHint => Texts.T("budgets.quickTagUndoHint", Config.QuickTagUndoDepthDefault, Config.QuickTagUndoDepthMinimum, Config.QuickTagUndoDepthMaximum);
    public string QuickTagUndoDepthHelp => FieldHelp(nameof(QuickTagUndoDepthInput), Texts.T("settings.unitCount") + ". " + QuickTagUndoDepthHint);
    public decimal? QuickTagNeighbourScan { get => _quickTagNeighbourScan; set => SetField(ref _quickTagNeighbourScan, value, nameof(QuickTagNeighbourScan)); }
    public string QuickTagNeighbourScanLabel => Texts.T("budgets.quickTagNeighbours");
    public string QuickTagNeighbourScanHint => Texts.T("budgets.quickTagNeighboursHint", Config.QuickTagNeighbourScanDefault, Config.QuickTagNeighbourScanMinimum, Config.QuickTagNeighbourScanMaximum);
    public string QuickTagNeighbourScanHelp => FieldHelp(nameof(QuickTagNeighbourScanInput), Texts.T("settings.unitCount") + ". " + QuickTagNeighbourScanHint);
    public decimal? QuickTagFilenameTokens { get => _quickTagFilenameTokens; set => SetField(ref _quickTagFilenameTokens, value, nameof(QuickTagFilenameTokens)); }
    public string QuickTagFilenameTokensLabel => Texts.T("budgets.quickTagFilename");
    public string QuickTagFilenameTokensHint => Texts.T("budgets.quickTagFilenameHint", Config.QuickTagFilenameTokensDefault, Config.QuickTagFilenameTokensMinimum, Config.QuickTagFilenameTokensMaximum);
    public string QuickTagFilenameTokensHelp => FieldHelp(nameof(QuickTagFilenameTokensInput), Texts.T("settings.unitCount") + ". " + QuickTagFilenameTokensHint);
    public decimal? QuickTagSourceTagsPerMedium { get => _quickTagSourceTagsPerMedium; set => SetField(ref _quickTagSourceTagsPerMedium, value, nameof(QuickTagSourceTagsPerMedium)); }
    public string QuickTagSourceTagsPerMediumLabel => Texts.T("budgets.quickTagSourceTags");
    public string QuickTagSourceTagsPerMediumHint => Texts.T("budgets.quickTagSourceTagsHint", Config.QuickTagSourceTagsPerMediumDefault, Config.QuickTagSourceTagsPerMediumMinimum, Config.QuickTagSourceTagsPerMediumMaximum);
    public string QuickTagSourceTagsPerMediumHelp => FieldHelp(nameof(QuickTagSourceTagsPerMediumInput), Texts.T("settings.unitCount") + ". " + QuickTagSourceTagsPerMediumHint);
    public decimal? QuickTagMarkedMediaLimit { get => _quickTagMarkedMediaLimit; set => SetField(ref _quickTagMarkedMediaLimit, value, nameof(QuickTagMarkedMediaLimit)); }
    public string QuickTagMarkedMediaLimitLabel => Texts.T("budgets.quickTagMarked");
    public string QuickTagMarkedMediaLimitHint => Texts.T("budgets.quickTagMarkedHint", Config.QuickTagMarkedMediaLimitDefault, Config.QuickTagMarkedMediaLimitMinimum, Config.QuickTagMarkedMediaLimitMaximum);
    public string QuickTagMarkedMediaLimitHelp => FieldHelp(nameof(QuickTagMarkedMediaLimitInput), Texts.T("settings.unitCount") + ". " + QuickTagMarkedMediaLimitHint);

    private static readonly string[] QuickTagHelpProperties =
    {
        nameof(QuickTagSuggestionCountHelp),
        nameof(QuickTagRecentCountHelp),
        nameof(QuickTagUndoDepthHelp),
        nameof(QuickTagNeighbourScanHelp),
        nameof(QuickTagFilenameTokensHelp),
        nameof(QuickTagSourceTagsPerMediumHelp),
        nameof(QuickTagMarkedMediaLimitHelp)
    };

    private static readonly string[] QuickTagTextProperties =
    {
        nameof(QuickTagGroupLabel),
        nameof(QuickTagSuggestionCountLabel), nameof(QuickTagSuggestionCountHint),
        nameof(QuickTagRecentCountLabel), nameof(QuickTagRecentCountHint),
        nameof(QuickTagUndoDepthLabel), nameof(QuickTagUndoDepthHint),
        nameof(QuickTagNeighbourScanLabel), nameof(QuickTagNeighbourScanHint),
        nameof(QuickTagFilenameTokensLabel), nameof(QuickTagFilenameTokensHint),
        nameof(QuickTagSourceTagsPerMediumLabel), nameof(QuickTagSourceTagsPerMediumHint),
        nameof(QuickTagMarkedMediaLimitLabel), nameof(QuickTagMarkedMediaLimitHint)
    };

    /// <summary>A language change refreshes text only; it is not a change of any value.</summary>
    private void RaiseQuickTagTextsChanged() => Raise(QuickTagTextProperties.Concat(QuickTagHelpProperties));

    private void NotifyQuickTagHelpChanged() => Raise(QuickTagHelpProperties);

    /// <summary>The limits from <paramref name="config"/> (start, discard, after a save).</summary>
    private void LoadQuickTagDraft(Config config)
    {
        QuickTagSuggestionCount = config.QuickTagSuggestionCount;
        QuickTagRecentCount = config.QuickTagRecentCount;
        QuickTagUndoDepth = config.QuickTagUndoDepth;
        QuickTagNeighbourScan = config.QuickTagNeighbourScan;
        QuickTagFilenameTokens = config.QuickTagFilenameTokens;
        QuickTagSourceTagsPerMedium = config.QuickTagSourceTagsPerMedium;
        QuickTagMarkedMediaLimit = config.QuickTagMarkedMediaLimit;
        ResetNumber(QuickTagSuggestionCountInput, config.QuickTagSuggestionCount);
        ResetNumber(QuickTagRecentCountInput, config.QuickTagRecentCount);
        ResetNumber(QuickTagUndoDepthInput, config.QuickTagUndoDepth);
        ResetNumber(QuickTagNeighbourScanInput, config.QuickTagNeighbourScan);
        ResetNumber(QuickTagFilenameTokensInput, config.QuickTagFilenameTokens);
        ResetNumber(QuickTagSourceTagsPerMediumInput, config.QuickTagSourceTagsPerMedium);
        ResetNumber(QuickTagMarkedMediaLimitInput, config.QuickTagMarkedMediaLimit);
    }

    /// <summary>Writes the draft into <paramref name="candidate"/>; a value outside its range names the setting and the range.</summary>
    private void ApplyQuickTagDraft(Config candidate)
    {
        candidate.QuickTagSuggestionCount = GetInteger(QuickTagSuggestionCount, QuickTagSuggestionCountLabel, Config.QuickTagSuggestionCountMinimum, Config.QuickTagSuggestionCountMaximum);
        candidate.QuickTagRecentCount = GetInteger(QuickTagRecentCount, QuickTagRecentCountLabel, Config.QuickTagRecentCountMinimum, Config.QuickTagRecentCountMaximum);
        candidate.QuickTagUndoDepth = GetInteger(QuickTagUndoDepth, QuickTagUndoDepthLabel, Config.QuickTagUndoDepthMinimum, Config.QuickTagUndoDepthMaximum);
        candidate.QuickTagNeighbourScan = GetInteger(QuickTagNeighbourScan, QuickTagNeighbourScanLabel, Config.QuickTagNeighbourScanMinimum, Config.QuickTagNeighbourScanMaximum);
        candidate.QuickTagFilenameTokens = GetInteger(QuickTagFilenameTokens, QuickTagFilenameTokensLabel, Config.QuickTagFilenameTokensMinimum, Config.QuickTagFilenameTokensMaximum);
        candidate.QuickTagSourceTagsPerMedium = GetInteger(QuickTagSourceTagsPerMedium, QuickTagSourceTagsPerMediumLabel, Config.QuickTagSourceTagsPerMediumMinimum, Config.QuickTagSourceTagsPerMediumMaximum);
        candidate.QuickTagMarkedMediaLimit = GetInteger(QuickTagMarkedMediaLimit, QuickTagMarkedMediaLimitLabel, Config.QuickTagMarkedMediaLimitMinimum, Config.QuickTagMarkedMediaLimitMaximum);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> QuickTagNumericFields()
    {
        yield return (QuickTagSuggestionCountInput, QuickTagSuggestionCount, QuickTagSuggestionCountLabel, Texts.T("settings.unitCount"),
            Config.QuickTagSuggestionCountMinimum, Config.QuickTagSuggestionCountMaximum, true);
        yield return (QuickTagRecentCountInput, QuickTagRecentCount, QuickTagRecentCountLabel, Texts.T("settings.unitCount"),
            Config.QuickTagRecentCountMinimum, Config.QuickTagRecentCountMaximum, true);
        yield return (QuickTagUndoDepthInput, QuickTagUndoDepth, QuickTagUndoDepthLabel, Texts.T("settings.unitCount"),
            Config.QuickTagUndoDepthMinimum, Config.QuickTagUndoDepthMaximum, true);
        yield return (QuickTagNeighbourScanInput, QuickTagNeighbourScan, QuickTagNeighbourScanLabel, Texts.T("settings.unitCount"),
            Config.QuickTagNeighbourScanMinimum, Config.QuickTagNeighbourScanMaximum, true);
        yield return (QuickTagFilenameTokensInput, QuickTagFilenameTokens, QuickTagFilenameTokensLabel, Texts.T("settings.unitCount"),
            Config.QuickTagFilenameTokensMinimum, Config.QuickTagFilenameTokensMaximum, true);
        yield return (QuickTagSourceTagsPerMediumInput, QuickTagSourceTagsPerMedium, QuickTagSourceTagsPerMediumLabel, Texts.T("settings.unitCount"),
            Config.QuickTagSourceTagsPerMediumMinimum, Config.QuickTagSourceTagsPerMediumMaximum, true);
        yield return (QuickTagMarkedMediaLimitInput, QuickTagMarkedMediaLimit, QuickTagMarkedMediaLimitLabel, Texts.T("settings.unitCount"),
            Config.QuickTagMarkedMediaLimitMinimum, Config.QuickTagMarkedMediaLimitMaximum, true);
    }
}
