using System;
using System.Collections.Generic;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.Stats;

namespace MeowChan.Desktop;

/// <summary>
/// What the tray statistics texts are built from. <paramref name="RatesKnown"/> is true while some sampling timer feeds the hub:
/// without one a rate of zero means "not measured", not "nothing is transferred", so the texts leave the rates out.
/// </summary>
internal readonly record struct TrayStatsInput(bool Paused, int Active, int Waiting, IoRates Rate, IoCounts Today, bool RatesKnown)
{
    internal bool ShowsRates => !Paused && Active > 0 && RatesKnown;

    internal static TrayStatsInput From(in IoStatsPeek peek, bool paused, int active, bool ratesKnown) =>
        new(paused, Math.Max(0, active), peek.Activity.Pending + peek.Activity.WaitingAdmission, peek.Rate, peek.Today, ratesKnown);
}

/// <summary>
/// The texts of the tray tooltip, as pure functions (binary units, the
/// selected language). The tooltip never exceeds <see cref="MaxTooltipLength"/> characters, the Windows limit minus a margin.
/// </summary>
internal static class TrayStatsModel
{
    internal const int MaxTooltipLength = 120;
    private const string Separator = " · ";

    /// <summary>The tooltip with statistics: rates and download count while downloads run, else the state and today's total.</summary>
    internal static string Tooltip(in TrayStatsInput state)
    {
        List<string> parts = [];
        if (state.Paused) parts.Add(Texts.T("tray.tooltipPaused"));
        else if (state.Active > 0)
        {
            if (state.RatesKnown) parts.Add(RatesText(state));
            parts.Add(Texts.Count("stats.tray.activeCount", state.Active));
        }
        else parts.Add(Texts.T("stats.tray.idle"));
        if (!state.ShowsRates && state.Today.NetDown > 0) parts.Add(TodayText(state));
        return Fit("MeowChan", parts);
    }

    private static string RatesText(in TrayStatsInput state) =>
        Texts.T("stats.tray.rates", ByteUnits.Rate(state.Rate.NetDown), ByteUnits.Rate(state.Rate.DiskWrite + state.Rate.DiskRead));

    private static string TodayText(in TrayStatsInput state) => Texts.T("stats.tray.today", ByteUnits.Size(state.Today.NetDown));

    /// <summary>Drops the last parts, then cuts, until the text fits; the leading part (state or rates) is the one that stays.</summary>
    internal static string Fit(string prefix, List<string> parts)
    {
        string text = prefix + Separator + string.Join(Separator, parts);
        while (text.Length > MaxTooltipLength && parts.Count > 1)
        {
            parts.RemoveAt(parts.Count - 1);
            text = prefix + Separator + string.Join(Separator, parts);
        }
        return text.Length <= MaxTooltipLength ? text : text[..(MaxTooltipLength - 1)] + "…";
    }
}
