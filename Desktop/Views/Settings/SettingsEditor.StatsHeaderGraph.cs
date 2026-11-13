using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan;

/// <summary>
/// The "Header graph" setting of the Statistics block: what the graph at the top shows (Auto, Network, NetworkAndDisk).
/// Kept in its own file so the other statistics settings can be added beside it without touching this one.
/// </summary>
public partial class SettingsEditor
{
    private SettingsChoice? _selectedStatsHeaderGraph;

    public string StatsHeaderGraphBlockTitle => Texts.T("stats.headerGraph.block");
    public string StatsHeaderGraphLabel => Texts.T("stats.headerGraph.label");
    public string StatsHeaderGraphHint => Texts.T("stats.headerGraph.hint");
    public IReadOnlyList<SettingsChoice> StatsHeaderGraphChoices { get; private set; } = Array.Empty<SettingsChoice>();

    public SettingsChoice? SelectedStatsHeaderGraph
    {
        get => _selectedStatsHeaderGraph;
        set => SetField(ref _selectedStatsHeaderGraph, value, nameof(SelectedStatsHeaderGraph));
    }

    /// <summary>Rebuilds the three choices in the current language and selects <paramref name="value"/>; also refreshes the block's labels.</summary>
    private void RefreshStatsHeaderGraphChoices(string value)
    {
        StatsHeaderGraphChoices = new SettingsChoice[]
        {
            new(IoStatsDisplay.HeaderGraphAuto, Texts.T("stats.headerGraph.auto")),
            new(IoStatsDisplay.HeaderGraphNetwork, Texts.T("stats.headerGraph.network")),
            new(IoStatsDisplay.HeaderGraphNetworkAndDisk, Texts.T("stats.headerGraph.networkAndDisk"))
        };
        foreach (string property in new[] { nameof(StatsHeaderGraphBlockTitle), nameof(StatsHeaderGraphLabel), nameof(StatsHeaderGraphHint), nameof(StatsHeaderGraphChoices) })
            RaiseStatsHeaderGraphChanged(property);
        string normalized = IoStatsDisplay.NormalizeHeaderGraph(value);
        SelectedStatsHeaderGraph = StatsHeaderGraphChoices.First(item => item.Value == normalized);
    }

    private void RaiseStatsHeaderGraphChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
