using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan;

/// <summary>
/// The "Header graph" setting of the Statistics block: what the header shows and how it is laid out (Auto, Network, Disk, NetworkAndDisk, SideBySide, Text).
/// Kept in its own file so the other statistics settings can be added beside it without touching this one.
/// </summary>
public partial class SettingsEditor
{
    private SettingsChoice? _selectedStatsHeaderGraph;
    private bool _statsHeaderIcons;

    public string StatsHeaderIconsLabel => Texts.T("stats.headerIcons.label");
    public string StatsHeaderIconsHint => Texts.T("stats.headerIcons.hint");
    public bool StatsHeaderIcons
    {
        get => _statsHeaderIcons;
        set => SetField(ref _statsHeaderIcons, value, nameof(StatsHeaderIcons));
    }

    public string StatsHeaderGraphLabel => Texts.T("stats.headerGraph.label");
    public string StatsHeaderGraphHint => Texts.T("stats.headerGraph.hint");
    public IReadOnlyList<SettingsChoice> StatsHeaderGraphChoices { get; private set; } = Array.Empty<SettingsChoice>();

    public SettingsChoice? SelectedStatsHeaderGraph
    {
        get => _selectedStatsHeaderGraph;
        set => SetField(ref _selectedStatsHeaderGraph, value, nameof(SelectedStatsHeaderGraph));
    }

    /// <summary>Rebuilds the six choices in the current language and selects <paramref name="value"/>; also refreshes the block's labels.</summary>
    private void RefreshStatsHeaderGraphChoices(string value)
    {
        StatsHeaderGraphChoices = new SettingsChoice[]
        {
            new(IoStatsDisplay.HeaderGraphAuto, Texts.T("stats.headerGraph.auto")),
            new(IoStatsDisplay.HeaderGraphNetwork, Texts.T("stats.headerGraph.network")),
            new(IoStatsDisplay.HeaderGraphDisk, Texts.T("stats.headerGraph.disk")),
            new(IoStatsDisplay.HeaderGraphNetworkAndDisk, Texts.T("stats.headerGraph.networkAndDisk")),
            new(IoStatsDisplay.HeaderGraphSideBySide, Texts.T("stats.headerGraph.sideBySide")),
            new(IoStatsDisplay.HeaderGraphText, Texts.T("stats.headerGraph.text"))
        };
        foreach (string property in new[] { nameof(StatsHeaderGraphLabel), nameof(StatsHeaderGraphHint), nameof(StatsHeaderGraphChoices), nameof(StatsHeaderIconsLabel), nameof(StatsHeaderIconsHint) })
            RaiseStatsHeaderGraphChanged(property);
        string normalized = IoStatsDisplay.NormalizeHeaderGraph(value);
        SelectedStatsHeaderGraph = StatsHeaderGraphChoices.First(item => item.Value == normalized);
    }

    /// <summary>
    /// The header's context menu changed the stored arrangement: the row shows it too. Only this row follows; an unrelated draft stays.
    /// </summary>
    internal void RefreshConfirmedStatsHeaderGraph()
    {
        if (_isClosed) return;
        string value = IoStatsDisplay.NormalizeHeaderGraph(_config.StatsHeaderGraph);
        SelectedStatsHeaderGraph = StatsHeaderGraphChoices.FirstOrDefault(item => item.Value == value) ?? SelectedStatsHeaderGraph;
    }

    /// <summary>Opens the page that holds the "Header graph" row, unfolds its groups and puts the focus on the row (the header menu's "Settings: Statistics…").</summary>
    internal void RevealStatsHeaderGraph()
    {
        if (_isClosed) return;
        SelectCategoryContaining(StatsHeaderGraphInput);
        foreach (Expander group in StatsHeaderGraphInput.GetLogicalAncestors().OfType<Expander>()) group.IsExpanded = true;
        // The groups were just unfolded: scrolling and focus need their layout.
        Dispatcher.UIThread.Post(() =>
        {
            if (_isClosed) return;
            StatsHeaderGraphInput.BringIntoView();
            StatsHeaderGraphInput.Focus(NavigationMethod.Tab);
        }, DispatcherPriority.Loaded);
    }

    private void RaiseStatsHeaderGraphChanged(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
