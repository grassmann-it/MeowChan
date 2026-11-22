using System;
using System.Collections.Generic;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private SettingsChoice? _selectedBooruNewSearchTarget;

    public SettingsChoice? SelectedBooruNewSearchTarget
    {
        get => _selectedBooruNewSearchTarget;
        set => SetField(ref _selectedBooruNewSearchTarget, value, nameof(SelectedBooruNewSearchTarget));
    }

    public IReadOnlyList<SettingsChoice> BooruSearchTargetChoices { get; private set; } = Array.Empty<SettingsChoice>();
    public string BooruNewSearchTargetLabel => Texts.T("settings.booru.newSearchTarget");
    public string BooruNewSearchTargetHint => Texts.T("settings.booru.newSearchTarget.hint");

    private void LoadBooruSearchDraft(Config config) => RefreshBooruSearchChoices(config.BooruNewSearchTarget);

    private void RefreshBooruSearchChoices(string selected)
    {
        BooruSearchTargetChoices = new SettingsChoice[]
        {
            new(Config.BooruSearchTargetNewTab, Texts.T("settings.booru.newSearchTarget.new")),
            new(Config.BooruSearchTargetCurrentTab, Texts.T("settings.booru.newSearchTarget.current"))
        };
        Raise([nameof(BooruSearchTargetChoices), nameof(BooruNewSearchTargetLabel), nameof(BooruNewSearchTargetHint)]);
        SelectedBooruNewSearchTarget = Choice(BooruSearchTargetChoices, selected);
    }
}
