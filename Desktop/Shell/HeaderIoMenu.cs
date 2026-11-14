using System;
using MeowChan.Desktop.Controls;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop;

/// <summary>
/// The context menu of the header's network and disk display: the arrangement as exclusive choices (the values of the
/// "Header graph" setting, the same texts and order as the setting's list), then the statistics page and the settings. Pure
/// composition: the window supplies the current value and what a choice, "open statistics" and "open settings" do.
/// </summary>
internal static class HeaderIoMenu
{
    /// <summary>The radio group of the arrangement.</summary>
    internal const string Group = "header.io.graph";

    /// <summary>The id of the entry that selects <paramref name="value"/> (a value of <see cref="IoStatsDisplay.HeaderGraphChoices"/>).</summary>
    internal static string EntryId(string value) => Group + "." + value;

    /// <summary>The text key of a value: the one the settings list shows for it.</summary>
    internal static string TextKey(string value) => value switch
    {
        IoStatsDisplay.HeaderGraphNetwork => "stats.headerGraph.network",
        IoStatsDisplay.HeaderGraphDisk => "stats.headerGraph.disk",
        IoStatsDisplay.HeaderGraphNetworkAndDisk => "stats.headerGraph.networkAndDisk",
        IoStatsDisplay.HeaderGraphSideBySide => "stats.headerGraph.sideBySide",
        IoStatsDisplay.HeaderGraphText => "stats.headerGraph.text",
        IoStatsDisplay.HeaderGraphOff => "stats.headerGraph.off",
        _ => "stats.headerGraph.auto"
    };

    /// <param name="current">The stored value of <c>Config.StatsHeaderGraph</c> (anything unknown reads as Auto, as the header does).</param>
    /// <param name="choose">Selects a value of <see cref="IoStatsDisplay.HeaderGraphChoices"/>; it is not called for the value already selected.</param>
    /// <param name="openStatistics">Opens the statistics page on the Status page (command <c>navigation.status</c>).</param>
    /// <param name="openSettings">Opens the settings at the statistics block (command <c>navigation.settings</c>).</param>
    internal static ContextMenuBuilder Compose(string? current, Action<string> choose, Action openStatistics, Action openSettings)
    {
        ArgumentNullException.ThrowIfNull(choose);
        ArgumentNullException.ThrowIfNull(openStatistics);
        ArgumentNullException.ThrowIfNull(openSettings);
        string selected = IoStatsDisplay.NormalizeHeaderGraph(current);
        ContextMenuBuilder menu = new ContextMenuBuilder().Named(Texts.T("header.io.menu.name"));
        foreach (string value in IoStatsDisplay.HeaderGraphChoices)
        {
            string chosen = value;
            menu.Radio(Group, EntryId(value), Texts.T(TextKey(value)), value == selected, () => { if (chosen != selected) choose(chosen); });
        }
        return menu.Section()
            .Command("navigation.status", openStatistics, label: Texts.T("header.io.menu.openStatistics"))
            .Command("navigation.settings", openSettings, label: Texts.T("header.io.menu.settings"));
    }
}
