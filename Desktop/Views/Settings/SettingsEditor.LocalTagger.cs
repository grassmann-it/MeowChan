using MeowChan.Desktop.Localization;
using MeowChan.PluginHost.Taggers;

namespace MeowChan;

public partial class SettingsEditor
{
    internal decimal LocalTaggerManifestKiBMinimum => LocalTaggerModelSettings.ManifestKiBRange.Minimum;
    internal decimal LocalTaggerManifestKiBMaximum => LocalTaggerModelSettings.ManifestKiBRange.Maximum;
    internal decimal LocalTaggerWeightsMiBMinimum => LocalTaggerModelSettings.WeightsMiBRange.Minimum;
    internal decimal LocalTaggerWeightsMiBMaximum => LocalTaggerModelSettings.WeightsMiBRange.Maximum;
    internal decimal LocalTaggerInputTensorMiBMinimum => LocalTaggerModelSettings.InputTensorMiBRange.Minimum;
    internal decimal LocalTaggerInputTensorMiBMaximum => LocalTaggerModelSettings.InputTensorMiBRange.Maximum;

    internal string LocalTaggerLimitsLabel => Texts.T("localTagger.title");

    private decimal? _localTaggerManifestKiB;
    internal decimal? LocalTaggerManifestKiB { get => _localTaggerManifestKiB; set => SetField(ref _localTaggerManifestKiB, value, nameof(LocalTaggerManifestKiB)); }
    internal string LocalTaggerManifestKiBLabel => Texts.T("budgets.localTaggerManifest");
    internal string LocalTaggerManifestKiBHelp => FieldHelp(nameof(LocalTaggerManifestKiBInput), BudgetRestartHint);

    private decimal? _localTaggerWeightsMiB;
    internal decimal? LocalTaggerWeightsMiB { get => _localTaggerWeightsMiB; set => SetField(ref _localTaggerWeightsMiB, value, nameof(LocalTaggerWeightsMiB)); }
    internal string LocalTaggerWeightsMiBLabel => Texts.T("budgets.localTaggerWeights");
    internal string LocalTaggerWeightsMiBHelp => FieldHelp(nameof(LocalTaggerWeightsMiBInput), BudgetRestartHint);

    private decimal? _localTaggerInputTensorMiB;
    internal decimal? LocalTaggerInputTensorMiB { get => _localTaggerInputTensorMiB; set => SetField(ref _localTaggerInputTensorMiB, value, nameof(LocalTaggerInputTensorMiB)); }
    internal string LocalTaggerInputTensorMiBLabel => Texts.T("budgets.localTaggerInputTensor");
    internal string LocalTaggerInputTensorMiBHelp => FieldHelp(nameof(LocalTaggerInputTensorMiBInput), BudgetRestartHint);

    private void SaveLocalTaggerLimits(Config candidate)
    {
        candidate.LocalTaggerManifestKiB = GetInteger(LocalTaggerManifestKiB, LocalTaggerManifestKiBLabel,
            (int)LocalTaggerManifestKiBMinimum, (int)LocalTaggerManifestKiBMaximum);
        candidate.LocalTaggerWeightsMiB = GetInteger(LocalTaggerWeightsMiB, LocalTaggerWeightsMiBLabel,
            (int)LocalTaggerWeightsMiBMinimum, (int)LocalTaggerWeightsMiBMaximum);
        candidate.LocalTaggerInputTensorMiB = GetInteger(LocalTaggerInputTensorMiB, LocalTaggerInputTensorMiBLabel,
            (int)LocalTaggerInputTensorMiBMinimum, (int)LocalTaggerInputTensorMiBMaximum);
    }
}
