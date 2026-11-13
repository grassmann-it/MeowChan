using System;
using System.Collections.Generic;
using System.Linq;
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
/// The texts of the tray tooltip and the two statistics lines at the top of the tray menu, as pure functions (binary units, the
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

    /// <summary>
    /// The two menu lines. Line one is the current rates (or the state when there are none to show), line two the counts and
    /// today's totals. The lines are plain text; both are enabled because a disabled native item is drawn unreadably grey.
    /// </summary>
    internal static (string Rates, string Detail) MenuLines(in TrayStatsInput state)
    {
        if (state.ShowsRates)
            return (RatesText(state), Join(ActiveText(state), WaitingText(state), TodayText(state)));
        string first = state.Paused ? Texts.T("stats.tray.menuPaused")
            : state.Active > 0 ? Join(ActiveText(state), WaitingText(state))
            : Texts.T("stats.tray.menuIdle");
        return (first, Join(TodayText(state), Texts.T("stats.tray.diskWritten", ByteUnits.Size(state.Today.DiskWritten))));
    }

    private static string RatesText(in TrayStatsInput state) =>
        Texts.T("stats.tray.rates", ByteUnits.Rate(state.Rate.NetDown), ByteUnits.Rate(IoStatsDisplay.HeaderDiskRate(state.Rate)));

    private static string ActiveText(in TrayStatsInput state) => Texts.Count("stats.tray.activeCount", state.Active);

    private static string WaitingText(in TrayStatsInput state) => state.Waiting > 0 ? Texts.T("stats.tray.waiting", state.Waiting) : string.Empty;

    private static string TodayText(in TrayStatsInput state) => Texts.T("stats.tray.today", ByteUnits.Size(state.Today.NetDown));

    private static string Join(params string[] parts) => string.Join(Separator, parts.Where(part => part.Length > 0));

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
