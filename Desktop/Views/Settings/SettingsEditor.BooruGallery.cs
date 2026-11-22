using System.Collections.Generic;
using Avalonia.Controls;
using MeowChan.Desktop.Localization;

namespace MeowChan;

public partial class SettingsEditor
{
    private decimal? _booruGalleryMaximumPixels;
    public decimal? BooruGalleryMaximumPixels { get => _booruGalleryMaximumPixels; set => SetField(ref _booruGalleryMaximumPixels, value, nameof(BooruGalleryMaximumPixels)); }
    public int BooruGalleryMaximumPixelsMinimum => Config.BooruGalleryMaximumPixelsMinimum;
    public int BooruGalleryMaximumPixelsMaximum => Config.BooruGalleryMaximumPixelsMaximum;
    public string BooruGalleryMaximumPixelsLabel => Texts.T("settings.limits.booruGalleryMaximumPixels");
    public string BooruGalleryMaximumPixelsHint => Texts.T("settings.limits.booruGalleryMaximumPixels.hint") + " "
        + Texts.T("settings.reader.range", BooruGalleryMaximumPixelsMinimum, BooruGalleryMaximumPixelsMaximum);
    public string BooruGalleryMaximumPixelsHelp => FieldHelp(nameof(BooruGalleryMaximumPixelsInput), BooruGalleryMaximumPixelsHint);

    private decimal? _booruGalleryMaximumWidth;
    public decimal? BooruGalleryMaximumWidth { get => _booruGalleryMaximumWidth; set => SetField(ref _booruGalleryMaximumWidth, value, nameof(BooruGalleryMaximumWidth)); }
    public int BooruGalleryMaximumWidthMinimum => Config.BooruGalleryMaximumWidthMinimum;
    public int BooruGalleryMaximumWidthMaximum => Config.BooruGalleryMaximumWidthMaximum;
    public string BooruGalleryMaximumWidthLabel => Texts.T("settings.limits.booruGalleryMaximumWidth");
    public string BooruGalleryMaximumWidthHint => Texts.T("settings.limits.booruGalleryMaximumWidth.hint") + " "
        + Texts.T("settings.reader.range", BooruGalleryMaximumWidthMinimum, BooruGalleryMaximumWidthMaximum);
    public string BooruGalleryMaximumWidthHelp => FieldHelp(nameof(BooruGalleryMaximumWidthInput), BooruGalleryMaximumWidthHint);

    private decimal? _booruGalleryTimeoutSeconds;
    public decimal? BooruGalleryTimeoutSeconds { get => _booruGalleryTimeoutSeconds; set => SetField(ref _booruGalleryTimeoutSeconds, value, nameof(BooruGalleryTimeoutSeconds)); }
    public int BooruGalleryTimeoutSecondsMinimum => Config.BooruGalleryTimeoutSecondsMinimum;
    public int BooruGalleryTimeoutSecondsMaximum => Config.BooruGalleryTimeoutSecondsMaximum;
    public string BooruGalleryTimeoutSecondsLabel => Texts.T("settings.limits.booruGalleryTimeoutSeconds");
    public string BooruGalleryTimeoutSecondsHint => Texts.T("settings.limits.booruGalleryTimeoutSeconds.hint") + " "
        + Texts.T("settings.reader.range", BooruGalleryTimeoutSecondsMinimum, BooruGalleryTimeoutSecondsMaximum);
    public string BooruGalleryTimeoutSecondsHelp => FieldHelp(nameof(BooruGalleryTimeoutSecondsInput), BooruGalleryTimeoutSecondsHint);

    private void LoadBooruGalleryDraft(Config config)
    {
        BooruGalleryMaximumPixels = config.BooruGalleryMaximumPixels;
        ResetNumber(BooruGalleryMaximumPixelsInput, config.BooruGalleryMaximumPixels);
        BooruGalleryMaximumWidth = config.BooruGalleryMaximumWidth;
        ResetNumber(BooruGalleryMaximumWidthInput, config.BooruGalleryMaximumWidth);
        BooruGalleryTimeoutSeconds = config.BooruGalleryTimeoutSeconds;
        ResetNumber(BooruGalleryTimeoutSecondsInput, config.BooruGalleryTimeoutSeconds);
    }

    private void ApplyBooruGalleryDraft(Config candidate)
    {
        candidate.BooruGalleryMaximumPixels = GetInteger(BooruGalleryMaximumPixels, BooruGalleryMaximumPixelsLabel, BooruGalleryMaximumPixelsMinimum, BooruGalleryMaximumPixelsMaximum);
        candidate.BooruGalleryMaximumWidth = GetInteger(BooruGalleryMaximumWidth, BooruGalleryMaximumWidthLabel, BooruGalleryMaximumWidthMinimum, BooruGalleryMaximumWidthMaximum);
        candidate.BooruGalleryTimeoutSeconds = GetInteger(BooruGalleryTimeoutSeconds, BooruGalleryTimeoutSecondsLabel, BooruGalleryTimeoutSecondsMinimum, BooruGalleryTimeoutSecondsMaximum);
    }

    private IEnumerable<(NumericUpDown Input, decimal? Value, string Label, string Unit, decimal Min, decimal Max, bool Integer)> BooruGalleryNumericFields()
    {
        yield return (BooruGalleryMaximumPixelsInput, BooruGalleryMaximumPixels, BooruGalleryMaximumPixelsLabel, string.Empty, BooruGalleryMaximumPixelsMinimum, BooruGalleryMaximumPixelsMaximum, true);
        yield return (BooruGalleryMaximumWidthInput, BooruGalleryMaximumWidth, BooruGalleryMaximumWidthLabel, string.Empty, BooruGalleryMaximumWidthMinimum, BooruGalleryMaximumWidthMaximum, true);
        yield return (BooruGalleryTimeoutSecondsInput, BooruGalleryTimeoutSeconds, BooruGalleryTimeoutSecondsLabel, string.Empty, BooruGalleryTimeoutSecondsMinimum, BooruGalleryTimeoutSecondsMaximum, true);
    }

    private void RaiseBooruGalleryTextsChanged()
    {
        Raise([
            nameof(BooruGalleryMaximumPixelsLabel), nameof(BooruGalleryMaximumPixelsHint),
            nameof(BooruGalleryMaximumWidthLabel), nameof(BooruGalleryMaximumWidthHint),
            nameof(BooruGalleryTimeoutSecondsLabel), nameof(BooruGalleryTimeoutSecondsHint)
        ]);
        NotifyBooruGalleryHelpChanged();
    }

    private void NotifyBooruGalleryHelpChanged() => Raise([
        nameof(BooruGalleryMaximumPixelsHelp),
        nameof(BooruGalleryMaximumWidthHelp),
        nameof(BooruGalleryTimeoutSecondsHelp)
    ]);
}
