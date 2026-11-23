using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using MeowChan.Desktop;
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
    public int TagQueueGroupLimitMinimum => Config.TagQueueGroupLimitMinimum;
    public int TagQueueGroupLimitMaximum => Config.TagQueueGroupLimitMaximum;
    public int TagQueueGroupNameLimitMinimum => Config.TagQueueGroupNameLimitMinimum;
    public int TagQueueGroupNameLimitMaximum => Config.TagQueueGroupNameLimitMaximum;
    public int TagQueueConfirmAboveMinimum => Config.TagQueueConfirmAboveMinimum;
    public int TagQueueConfirmAboveMaximum => Config.TagQueueConfirmAboveMaximum;
    public int MediaTagListTopMinimum => Config.MediaTagListTopMinimum;
    public int MediaTagListTopMaximum => Config.MediaTagListTopMaximum;
    public int BooruResultTagsShownMinimum => Config.BooruResultTagsShownMinimum;
    public int BooruResultTagsShownMaximum => Config.BooruResultTagsShownMaximum;
    public int BooruSearchTabsMinimum => Config.BooruSearchTabsMinimum;
    public int BooruSearchTabsMaximum => Config.BooruSearchTabsMaximum;
    public int MediaTagListWidthMinimum => Config.MediaTagListWidthMinimum;
    public int MediaTagListWidthMaximum => Config.MediaTagListWidthMaximum;
    public int MediaPanelWidthMinimum => Config.MediaPanelWidthMinimum;
    public int MediaPanelWidthMaximum => Config.MediaPanelWidthMaximum;
    public int MediaPanelMinimumPageWidthMinimum => Config.MediaPanelMinimumPageWidthMinimum;
    public int MediaPanelMinimumPageWidthMaximum => Config.MediaPanelMinimumPageWidthMaximum;

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

    private decimal? _tagQueueGroupLimit;
    public decimal? TagQueueGroupLimit { get => _tagQueueGroupLimit; set => SetField(ref _tagQueueGroupLimit, value, nameof(TagQueueGroupLimit)); }
    public string TagQueueGroupLimitLabel => Texts.T("settings.limits.tagQueueGroups");
    public string TagQueueGroupLimitHint => Texts.T("settings.limits.tagQueueGroups.hint") + " " + Texts.T("settings.reader.range", Config.TagQueueGroupLimitMinimum, Config.TagQueueGroupLimitMaximum);
    public string TagQueueGroupLimitHelp => FieldHelp(nameof(TagQueueGroupLimitInput), TagQueueGroupLimitHint);

    private decimal? _tagQueueGroupNameLimit;
    public decimal? TagQueueGroupNameLimit { get => _tagQueueGroupNameLimit; set => SetField(ref _tagQueueGroupNameLimit, value, nameof(TagQueueGroupNameLimit)); }
    public string TagQueueGroupNameLimitLabel => Texts.T("settings.limits.tagQueueGroupName");
    public string TagQueueGroupNameLimitHint => Texts.T("settings.limits.tagQueueGroupName.hint") + " " + Texts.T("settings.reader.range", Config.TagQueueGroupNameLimitMinimum, Config.TagQueueGroupNameLimitMaximum);
    public string TagQueueGroupNameLimitHelp => FieldHelp(nameof(TagQueueGroupNameLimitInput), TagQueueGroupNameLimitHint);

    private decimal? _tagQueueConfirmAbove;
    public decimal? TagQueueConfirmAbove { get => _tagQueueConfirmAbove; set => SetField(ref _tagQueueConfirmAbove, value, nameof(TagQueueConfirmAbove)); }
    public string TagQueueConfirmAboveLabel => Texts.T("settings.limits.tagQueueConfirm");
    public string TagQueueConfirmAboveHint => Texts.T("settings.limits.tagQueueConfirm.hint") + " " + Texts.T("settings.reader.range", Config.TagQueueConfirmAboveMinimum, Config.TagQueueConfirmAboveMaximum);
    public string TagQueueConfirmAboveHelp => FieldHelp(nameof(TagQueueConfirmAboveInput), TagQueueConfirmAboveHint);

    // MP4: the top-N of each group of the media page's Booru-mode tag list.
    private decimal? _mediaTagListTop;
    public decimal? MediaTagListTop { get => _mediaTagListTop; set => SetField(ref _mediaTagListTop, value, nameof(MediaTagListTop)); }
    public string MediaTagListTopLabel => Texts.T("settings.limits.mediaTagListTop");
    public string MediaTagListTopHint => Texts.T("settings.limits.mediaTagListTop.hint") + " " + Texts.T("settings.reader.range", Config.MediaTagListTopMinimum, Config.MediaTagListTopMaximum);
    public string MediaTagListTopHelp => FieldHelp(nameof(MediaTagListTopInput), MediaTagListTopHint);

    // E2: how many tag chips a Danbooru result of the list shows before "+N more".
    private decimal? _booruResultTagsShown;
    public decimal? BooruResultTagsShown { get => _booruResultTagsShown; set => SetField(ref _booruResultTagsShown, value, nameof(BooruResultTagsShown)); }
    public string BooruResultTagsShownLabel => Texts.T("settings.limits.booruResultTagsShown");
    public string BooruResultTagsShownHint => Texts.T("settings.limits.booruResultTagsShown.hint") + " " + Texts.T("settings.reader.range", Config.BooruResultTagsShownMinimum, Config.BooruResultTagsShownMaximum);
    public string BooruResultTagsShownHelp => FieldHelp(nameof(BooruResultTagsShownInput), BooruResultTagsShownHint);

    // Danbooru search tabs: how many extra tabs the page may hold (they live for the session only).
    private decimal? _booruSearchTabs;
    public decimal? BooruSearchTabs { get => _booruSearchTabs; set => SetField(ref _booruSearchTabs, value, nameof(BooruSearchTabs)); }
    public string BooruSearchTabsLabel => Texts.T("settings.limits.booruSearchTabs");
    public string BooruSearchTabsHint => Texts.T("settings.limits.booruSearchTabs.hint") + " " + Texts.T("settings.reader.range", Config.BooruSearchTabsMinimum, Config.BooruSearchTabsMaximum);
    public string BooruSearchTabsHelp => FieldHelp(nameof(BooruSearchTabsInput), BooruSearchTabsHint);

    // MP5: the width (dip) of the tag list. It lives in the saved media layout (MediaLayout), next to the other parts of that layout; this field
    // reads and rewrites only its width, the drag handle on the list's edge sets the same value.
    private decimal? _mediaTagListWidth;
    private int _initialMediaTagListWidth = Config.MediaTagListWidthDefault; // as loaded (start, discard, after a save): the merge compares the box with it (M1)
    public decimal? MediaTagListWidth { get => _mediaTagListWidth; set => SetField(ref _mediaTagListWidth, value, nameof(MediaTagListWidth)); }
    public string MediaTagListWidthLabel => Texts.T("settings.limits.mediaTagListWidth");
    public string MediaTagListWidthHint => Texts.T("settings.limits.mediaTagListWidth.hint") + " " + Texts.T("settings.reader.range", Config.MediaTagListWidthMinimum, Config.MediaTagListWidthMaximum);
    public string MediaTagListWidthHelp => FieldHelp(nameof(MediaTagListWidthInput), MediaTagListWidthHint);

    // The media panel beside the grid: its width lives in the saved media layout like the tag list's (the drag handle on its edge sets the same
    // value); the page width below which it hides is a plain setting.
    private decimal? _mediaPanelWidth;
    private int _initialMediaPanelWidth = Config.MediaPanelWidthDefault;
    public decimal? MediaPanelWidth { get => _mediaPanelWidth; set => SetField(ref _mediaPanelWidth, value, nameof(MediaPanelWidth)); }
    public string MediaPanelWidthLabel => Texts.T("settings.limits.mediaPanelWidth");
    public string MediaPanelWidthHint => Texts.T("settings.limits.mediaPanelWidth.hint") + " " + Texts.T("settings.reader.range", Config.MediaPanelWidthMinimum, Config.MediaPanelWidthMaximum);
    public string MediaPanelWidthHelp => FieldHelp(nameof(MediaPanelWidthInput), MediaPanelWidthHint);

    private decimal? _mediaPanelMinimumPageWidth;
    public decimal? MediaPanelMinimumPageWidth { get => _mediaPanelMinimumPageWidth; set => SetField(ref _mediaPanelMinimumPageWidth, value, nameof(MediaPanelMinimumPageWidth)); }
    public string MediaPanelMinimumPageWidthLabel => Texts.T("settings.limits.mediaPanelMinimumPageWidth");
    public string MediaPanelMinimumPageWidthHint => Texts.T("settings.limits.mediaPanelMinimumPageWidth.hint") + " " + Texts.T("settings.reader.range", Config.MediaPanelMinimumPageWidthMinimum, Config.MediaPanelMinimumPageWidthMaximum);
    public string MediaPanelMinimumPageWidthHelp => FieldHelp(nameof(MediaPanelMinimumPageWidthInput), MediaPanelMinimumPageWidthHint);

    /// <summary>
    /// The queue limits and the media-page widths of <paramref name="config"/> (start, discard, after a save: <paramref name="captureBaseline"/>) or of
    /// the defaults (the section reset: false). The two widths live in the saved media layout and are written back only when the box differs from the
    /// value it was loaded with (M1), so a reset is an edit of the draft: it must not move that baseline, or the default would count as unchanged and
    /// never be saved. The tagging group has its own loader (<see cref="LoadTaggingDraft"/>), because it is shown in another section.
    /// </summary>
    private void LoadTagQueueDraft(Config config, bool captureBaseline = true)
    {
        TagQueueLimit = config.TagQueueLimit;
        ResetNumber(TagQueueLimitInput, TagQueueLimit.Value);
        TagQueueFileLimitMiB = config.TagQueueFileLimitMiB;
        ResetNumber(TagQueueFileLimitMiBInput, TagQueueFileLimitMiB.Value);
        TagQueueHistory = config.TagQueueHistory;
        ResetNumber(TagQueueHistoryInput, TagQueueHistory.Value);
        TagQueueGroupLimit = config.TagQueueGroupLimit;
        ResetNumber(TagQueueGroupLimitInput, TagQueueGroupLimit.Value);
        TagQueueGroupNameLimit = config.TagQueueGroupNameLimit;
        ResetNumber(TagQueueGroupNameLimitInput, TagQueueGroupNameLimit.Value);
        TagQueueConfirmAbove = config.TagQueueConfirmAbove;
        ResetNumber(TagQueueConfirmAboveInput, TagQueueConfirmAbove.Value);
        MediaTagListTop = config.MediaTagListTop;
        ResetNumber(MediaTagListTopInput, MediaTagListTop.Value);
        BooruResultTagsShown = config.BooruResultTagsShown;
        ResetNumber(BooruResultTagsShownInput, BooruResultTagsShown.Value);
        LoadBooruPostFilterDraft(config);
        BooruSearchTabs = config.BooruSearchTabs;
        ResetNumber(BooruSearchTabsInput, BooruSearchTabs.Value);
        MediaPageLayout layout = MediaPageLayout.Parse(config.MediaLayout, config.MediaBooruMode);
        MediaTagListWidth = layout.TagListWidth;
        ResetNumber(MediaTagListWidthInput, MediaTagListWidth.Value);
        MediaPanelWidth = layout.PanelWidth;
        ResetNumber(MediaPanelWidthInput, MediaPanelWidth.Value);
        if (captureBaseline)
        {
            _initialMediaTagListWidth = layout.TagListWidth;
            _initialMediaPanelWidth = layout.PanelWidth;
        }
        MediaPanelMinimumPageWidth = config.MediaPanelMinimumPageWidth;
        ResetNumber(MediaPanelMinimumPageWidthInput, MediaPanelMinimumPageWidth.Value);
    }

    private void ApplyTagQueueDraft(Config candidate)
    {
        candidate.TagQueueLimit = GetInteger(TagQueueLimit, TagQueueLimitLabel, Config.TagQueueLimitMinimum, Config.TagQueueLimitMaximum);
        candidate.TagQueueFileLimitMiB = GetInteger(TagQueueFileLimitMiB, TagQueueFileLimitMiBLabel, Config.TagQueueFileLimitMiBMinimum, Config.TagQueueFileLimitMiBMaximum);
        candidate.TagQueueHistory = GetInteger(TagQueueHistory, TagQueueHistoryLabel, Config.TagQueueHistoryMinimum, Config.TagQueueHistoryMaximum);
        candidate.TagQueueGroupLimit = GetInteger(TagQueueGroupLimit, TagQueueGroupLimitLabel, Config.TagQueueGroupLimitMinimum, Config.TagQueueGroupLimitMaximum);
        candidate.TagQueueGroupNameLimit = GetInteger(TagQueueGroupNameLimit, TagQueueGroupNameLimitLabel, Config.TagQueueGroupNameLimitMinimum, Config.TagQueueGroupNameLimitMaximum);
        candidate.TagQueueConfirmAbove = GetInteger(TagQueueConfirmAbove, TagQueueConfirmAboveLabel, Config.TagQueueConfirmAboveMinimum, Config.TagQueueConfirmAboveMaximum);
        candidate.MediaTagListTop = GetInteger(MediaTagListTop, MediaTagListTopLabel, Config.MediaTagListTopMinimum, Config.MediaTagListTopMaximum);
        candidate.BooruResultTagsShown = GetInteger(BooruResultTagsShown, BooruResultTagsShownLabel, Config.BooruResultTagsShownMinimum, Config.BooruResultTagsShownMaximum);
        ApplyBooruPostFilterDraft(candidate);
        candidate.BooruSearchTabs = GetInteger(BooruSearchTabs, BooruSearchTabsLabel, Config.BooruSearchTabsMinimum, Config.BooruSearchTabsMaximum);
        int tagListWidth = GetInteger(MediaTagListWidth, MediaTagListWidthLabel, Config.MediaTagListWidthMinimum, Config.MediaTagListWidthMaximum);
        int panelWidth = GetInteger(MediaPanelWidth, MediaPanelWidthLabel, Config.MediaPanelWidthMinimum, Config.MediaPanelWidthMaximum);
        candidate.MediaPanelMinimumPageWidth = GetInteger(MediaPanelMinimumPageWidth, MediaPanelMinimumPageWidthLabel, Config.MediaPanelMinimumPageWidthMinimum, Config.MediaPanelMinimumPageWidthMaximum);
        candidate.MediaLayout = (MediaPageLayout.Parse(candidate.MediaLayout, candidate.MediaBooruMode) with { TagListWidth = tagListWidth, PanelWidth = panelWidth }).Format();
        candidate.SettingsBaselineMediaTagListWidth = _initialMediaTagListWidth;
        candidate.SettingsBaselineMediaPanelWidth = _initialMediaPanelWidth;
        ApplyTaggingDraft(candidate);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> TagQueueNumericFields()
    {
        yield return (TagQueueLimitInput, TagQueueLimit, TagQueueLimitLabel, string.Empty, Config.TagQueueLimitMinimum, Config.TagQueueLimitMaximum, true);
        yield return (TagQueueFileLimitMiBInput, TagQueueFileLimitMiB, TagQueueFileLimitMiBLabel, string.Empty, Config.TagQueueFileLimitMiBMinimum, Config.TagQueueFileLimitMiBMaximum, true);
        yield return (TagQueueHistoryInput, TagQueueHistory, TagQueueHistoryLabel, string.Empty, Config.TagQueueHistoryMinimum, Config.TagQueueHistoryMaximum, true);
        yield return (TagQueueGroupLimitInput, TagQueueGroupLimit, TagQueueGroupLimitLabel, string.Empty, Config.TagQueueGroupLimitMinimum, Config.TagQueueGroupLimitMaximum, true);
        yield return (TagQueueGroupNameLimitInput, TagQueueGroupNameLimit, TagQueueGroupNameLimitLabel, string.Empty, Config.TagQueueGroupNameLimitMinimum, Config.TagQueueGroupNameLimitMaximum, true);
        yield return (TagQueueConfirmAboveInput, TagQueueConfirmAbove, TagQueueConfirmAboveLabel, string.Empty, Config.TagQueueConfirmAboveMinimum, Config.TagQueueConfirmAboveMaximum, true);
        yield return (MediaTagListTopInput, MediaTagListTop, MediaTagListTopLabel, string.Empty, Config.MediaTagListTopMinimum, Config.MediaTagListTopMaximum, true);
        yield return (BooruResultTagsShownInput, BooruResultTagsShown, BooruResultTagsShownLabel, string.Empty, Config.BooruResultTagsShownMinimum, Config.BooruResultTagsShownMaximum, true);
        yield return (BooruPostFilterScoreInput, BooruPostFilterScore, BooruPostFilterScoreLabel, string.Empty, Config.BooruPostFilterScoreMinimum, Config.BooruPostFilterScoreMaximum, true);
        yield return (BooruSearchTabsInput, BooruSearchTabs, BooruSearchTabsLabel, string.Empty, Config.BooruSearchTabsMinimum, Config.BooruSearchTabsMaximum, true);
        yield return (MediaTagListWidthInput, MediaTagListWidth, MediaTagListWidthLabel, string.Empty, Config.MediaTagListWidthMinimum, Config.MediaTagListWidthMaximum, true);
        yield return (MediaPanelWidthInput, MediaPanelWidth, MediaPanelWidthLabel, string.Empty, Config.MediaPanelWidthMinimum, Config.MediaPanelWidthMaximum, true);
        yield return (MediaPanelMinimumPageWidthInput, MediaPanelMinimumPageWidth, MediaPanelMinimumPageWidthLabel, string.Empty, Config.MediaPanelMinimumPageWidthMinimum, Config.MediaPanelMinimumPageWidthMaximum, true);
        foreach (var field in TaggingNumericFields()) yield return field;
    }

    private void RaiseTagQueueTextsChanged() => Raise(TagQueueTextProperties.Concat(TaggingTextProperties));
    private void NotifyTagQueueHelpChanged() => Raise(TagQueueHelpProperties.Concat(TaggingHelpProperties));

    private static readonly string[] TagQueueTextProperties =
    {
        nameof(TagQueueLimitLabel), nameof(TagQueueLimitHint), nameof(TagQueueLimitHelp),
        nameof(TagQueueFileLimitMiBLabel), nameof(TagQueueFileLimitMiBHint), nameof(TagQueueFileLimitMiBHelp),
        nameof(TagQueueHistoryLabel), nameof(TagQueueHistoryHint), nameof(TagQueueHistoryHelp),
        nameof(TagQueueGroupLimitLabel), nameof(TagQueueGroupLimitHint), nameof(TagQueueGroupLimitHelp),
        nameof(TagQueueGroupNameLimitLabel), nameof(TagQueueGroupNameLimitHint), nameof(TagQueueGroupNameLimitHelp),
        nameof(TagQueueConfirmAboveLabel), nameof(TagQueueConfirmAboveHint), nameof(TagQueueConfirmAboveHelp),
        nameof(MediaTagListTopLabel), nameof(MediaTagListTopHint), nameof(MediaTagListTopHelp),
        nameof(BooruResultTagsShownLabel), nameof(BooruResultTagsShownHint), nameof(BooruResultTagsShownHelp),
        nameof(BooruPostFilterScoreLabel), nameof(BooruPostFilterScoreHint), nameof(BooruPostFilterScoreHelp),
        nameof(BooruSearchTabsLabel), nameof(BooruSearchTabsHint), nameof(BooruSearchTabsHelp),
        nameof(MediaTagListWidthLabel), nameof(MediaTagListWidthHint), nameof(MediaTagListWidthHelp),
        nameof(MediaPanelWidthLabel), nameof(MediaPanelWidthHint), nameof(MediaPanelWidthHelp),
        nameof(MediaPanelMinimumPageWidthLabel), nameof(MediaPanelMinimumPageWidthHint), nameof(MediaPanelMinimumPageWidthHelp),
    };
    private static readonly string[] TagQueueHelpProperties =
    {
        nameof(TagQueueLimitHelp), nameof(TagQueueFileLimitMiBHelp), nameof(TagQueueHistoryHelp),
        nameof(TagQueueGroupLimitHelp), nameof(TagQueueGroupNameLimitHelp), nameof(TagQueueConfirmAboveHelp),
        nameof(BooruPostFilterScoreHelp), nameof(MediaTagListTopHelp), nameof(BooruResultTagsShownHelp), nameof(BooruSearchTabsHelp), nameof(MediaTagListWidthHelp), nameof(MediaPanelWidthHelp), nameof(MediaPanelMinimumPageWidthHelp),
    };
}
