using System;
using System.Collections.Generic;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private SettingsChoice? _selectedBooruNewSearchTarget;
    private bool _restoreBooruSearchTabs;
    private decimal? _booruSearchPages;
    private decimal? _booruSearchPostsPerPage;

    public bool RestoreBooruSearchTabs
    {
        get => _restoreBooruSearchTabs;
        set => SetField(ref _restoreBooruSearchTabs, value, nameof(RestoreBooruSearchTabs));
    }

    public string RestoreBooruSearchTabsLabel => Texts.T("settings.booru.restoreSearchTabs");
    public string RestoreBooruSearchTabsHint => Texts.T("settings.booru.restoreSearchTabs.hint");

    public SettingsChoice? SelectedBooruNewSearchTarget
    {
        get => _selectedBooruNewSearchTarget;
        set => SetField(ref _selectedBooruNewSearchTarget, value, nameof(SelectedBooruNewSearchTarget));
    }

    public IReadOnlyList<SettingsChoice> BooruSearchTargetChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public string BooruNewSearchTargetLabel => Texts.T("settings.booru.newSearchTarget");
    public string BooruNewSearchTargetHint => Texts.T("settings.booru.newSearchTarget.hint");

    public decimal? BooruSearchPages { get => _booruSearchPages; set => SetField(ref _booruSearchPages, value, nameof(BooruSearchPages)); }
    public int BooruSearchPagesMinimum => Config.BooruSearchPagesMinimum;
    public int BooruSearchPagesMaximum => Config.BooruSearchPagesMaximum;
    public string BooruSearchPagesLabel => Texts.T("settings.booru.searchPages");
    public string BooruSearchPagesHint => Texts.T("settings.booru.searchPages.hint") + " "
        + Texts.T("settings.reader.range", BooruSearchPagesMinimum, BooruSearchPagesMaximum);
    public string BooruSearchPagesHelp => FieldHelp(nameof(BooruSearchPagesInput), BooruSearchPagesHint);

    public decimal? BooruSearchPostsPerPage { get => _booruSearchPostsPerPage; set => SetField(ref _booruSearchPostsPerPage, value, nameof(BooruSearchPostsPerPage)); }
    public int BooruSearchPostsPerPageMinimum => Config.BooruSearchPostsPerPageMinimum;
    public int BooruSearchPostsPerPageMaximum => Config.BooruSearchPostsPerPageMaximum;
    public string BooruSearchPostsPerPageLabel => Texts.T("settings.booru.searchPostsPerPage");
    public string BooruSearchPostsPerPageHint => Texts.T("settings.booru.searchPostsPerPage.hint") + " "
        + Texts.T("settings.reader.range", BooruSearchPostsPerPageMinimum, BooruSearchPostsPerPageMaximum);
    public string BooruSearchPostsPerPageHelp => FieldHelp(nameof(BooruSearchPostsPerPageInput), BooruSearchPostsPerPageHint);

    private void LoadBooruSearchDraft(Config config)
    {
        RestoreBooruSearchTabs = config.RestoreBooruSearchTabs;
        RefreshBooruSearchChoices(config.BooruNewSearchTarget);
        BooruSearchPages = config.BooruSearchPages;
        ResetNumber(BooruSearchPagesInput, config.BooruSearchPages);
        BooruSearchPostsPerPage = config.BooruSearchPostsPerPage;
        ResetNumber(BooruSearchPostsPerPageInput, config.BooruSearchPostsPerPage);
    }

    private void ApplyBooruSearchDraft(Config candidate)
    {
        candidate.RestoreBooruSearchTabs = RestoreBooruSearchTabs;
        candidate.BooruNewSearchTarget = SelectedBooruNewSearchTarget?.Value ?? _config.BooruNewSearchTarget;
        candidate.BooruSearchPages = GetInteger(BooruSearchPages, BooruSearchPagesLabel, BooruSearchPagesMinimum, BooruSearchPagesMaximum);
        candidate.BooruSearchPostsPerPage = GetInteger(BooruSearchPostsPerPage, BooruSearchPostsPerPageLabel, BooruSearchPostsPerPageMinimum, BooruSearchPostsPerPageMaximum);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> BooruSearchNumericFields()
    {
        yield return (BooruSearchPagesInput, BooruSearchPages, BooruSearchPagesLabel, string.Empty, BooruSearchPagesMinimum, BooruSearchPagesMaximum, true);
        yield return (BooruSearchPostsPerPageInput, BooruSearchPostsPerPage, BooruSearchPostsPerPageLabel, string.Empty, BooruSearchPostsPerPageMinimum, BooruSearchPostsPerPageMaximum, true);
    }

    private void NotifyBooruSearchHelpChanged() => Raise([nameof(BooruSearchPagesHelp), nameof(BooruSearchPostsPerPageHelp)]);

    private void RefreshBooruSearchChoices(string selected)
    {
        BooruSearchTargetChoices = new SettingsChoice[]
        {
            new(Config.BooruSearchTargetNewTab, Texts.T("settings.booru.newSearchTarget.new")),
            new(Config.BooruSearchTargetCurrentTab, Texts.T("settings.booru.newSearchTarget.current"))
        };
        Raise([nameof(BooruSearchTargetChoices), nameof(BooruNewSearchTargetLabel), nameof(BooruNewSearchTargetHint),
            nameof(RestoreBooruSearchTabsLabel), nameof(RestoreBooruSearchTabsHint), nameof(BooruSearchPagesLabel),
            nameof(BooruSearchPagesHint), nameof(BooruSearchPostsPerPageLabel), nameof(BooruSearchPostsPerPageHint)]);
        NotifyBooruSearchHelpChanged();
        SelectedBooruNewSearchTarget = Choice(BooruSearchTargetChoices, selected);
    }
}
