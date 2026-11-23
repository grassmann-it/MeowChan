using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private decimal? _booruPostFilterScore;
    public int BooruPostFilterScoreMinimum => Config.BooruPostFilterScoreMinimum;
    public int BooruPostFilterScoreMaximum => Config.BooruPostFilterScoreMaximum;
    public decimal? BooruPostFilterScore { get => _booruPostFilterScore; set => SetField(ref _booruPostFilterScore, value, nameof(BooruPostFilterScore)); }
    public string BooruPostFilterScoreLabel => Texts.T("settings.booruPostFilterScore");
    public string BooruPostFilterScoreHint => Texts.T("settings.booruPostFilterScore.hint", Config.BooruPostFilterScoreDefault)
        + " " + Texts.T("settings.reader.range", Config.BooruPostFilterScoreMinimum, Config.BooruPostFilterScoreMaximum);
    public string BooruPostFilterScoreHelp => FieldHelp(nameof(BooruPostFilterScoreInput), BooruPostFilterScoreHint);

    private void LoadBooruPostFilterDraft(Config config)
    {
        BooruPostFilterScore = config.BooruPostFilterScore;
        ResetNumber(BooruPostFilterScoreInput, BooruPostFilterScore.Value);
    }

    private void ApplyBooruPostFilterDraft(Config candidate) => candidate.BooruPostFilterScore = GetInteger(
        BooruPostFilterScore, BooruPostFilterScoreLabel, Config.BooruPostFilterScoreMinimum, Config.BooruPostFilterScoreMaximum);
}
