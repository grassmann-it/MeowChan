using System;
using System.Globalization;
using MeowChan.Desktop.Localization;

namespace MeowChan.Desktop.Stats;

/// <summary>
/// Display rules of the header graph and the statistics page that are independent of controls: names for the keys the
/// transport uses, the screen-reader sentence of the graph button and the rule that decides when the window claims samples.
/// </summary>
internal static class IoStatsDisplay
{
    /// <summary>
    /// The text of a source row. The transport's own keys <c>other</c>, <c>local</c> and <c>unknown</c> get catalog texts;
    /// a real source id shows the display name <paramref name="displayName"/> finds, and an id nobody knows shows as written.
    /// </summary>
    internal static string SourceName(string key, Func<string, string?>? displayName = null)
    {
        switch (key)
        {
            case IoStatsKeys.Other: return Texts.T("stats.source.other");
            case MeowChan.Http.TransferMeter.LocalKey: return Texts.T("stats.source.local");
            case IoStatsKeys.Unknown: return Texts.T("stats.source.unknown");
        }
        string? name = displayName?.Invoke(key);
        return string.IsNullOrWhiteSpace(name) ? key : name;
    }

    /// <summary>The disk rate of the header: what MeowChan writes plus what it reads back.</summary>
    internal static double DiskRate(IoRates rate) => rate.DiskWrite + rate.DiskRead;

    /// <summary>The one sentence of the graph button, for the screen reader and the tooltip. Binary units, current language.</summary>
    internal static string GraphDescription(IoRates rate, IoActivity activity, CultureInfo? culture = null) =>
        Texts.T("stats.graph.name", ByteUnits.Rate(rate.NetDown, culture), ByteUnits.Rate(DiskRate(rate), culture), activity.Active);

    /// <summary>The values of <c>Config.StatsHeaderGraph</c>.</summary>
    internal const string HeaderGraphAuto = "Auto", HeaderGraphNetwork = "Network", HeaderGraphNetworkAndDisk = "NetworkAndDisk";

    /// <summary>Maps any text to one of the three values; anything unknown is <see cref="HeaderGraphAuto"/>.</summary>
    internal static string NormalizeHeaderGraph(string? value)
    {
        string? text = value?.Trim();
        return string.Equals(text, HeaderGraphNetwork, StringComparison.OrdinalIgnoreCase) ? HeaderGraphNetwork
            : string.Equals(text, HeaderGraphNetworkAndDisk, StringComparison.OrdinalIgnoreCase) ? HeaderGraphNetworkAndDisk
            : HeaderGraphAuto;
    }

    /// <summary>
    /// Whether the header graph draws the disk row: always for "NetworkAndDisk", never for "Network", and for "Auto" only in
    /// the Advanced work view (Simple and Barebones show the network only). The statistics page always shows both.
    /// </summary>
    internal static bool HeaderShowsDisk(string? setting, string? workspaceMode) => NormalizeHeaderGraph(setting) switch
    {
        HeaderGraphNetworkAndDisk => true,
        HeaderGraphNetwork => false,
        _ => string.Equals(workspaceMode?.Trim(), "Advanced", StringComparison.OrdinalIgnoreCase)
    };

    /// <summary>The graph sentence for a header that draws the network row only (no disk value is named).</summary>
    internal static string GraphDescriptionNetworkOnly(IoRates rate, IoActivity activity, CultureInfo? culture = null) =>
        Texts.T("stats.graph.nameNet", ByteUnits.Rate(rate.NetDown, culture), activity.Active);

    /// <summary>The graph sentence of the header for what it draws.</summary>
    internal static string GraphDescription(IoRates rate, IoActivity activity, bool showDisk, CultureInfo? culture = null) =>
        showDisk ? GraphDescription(rate, activity, culture) : GraphDescriptionNetworkOnly(rate, activity, culture);

    /// <summary>The seconds one history of <paramref name="samples"/> samples covers at the given sampling interval.</summary>
    internal static int HistorySeconds(int samples, int sampleIntervalMs) =>
        (int)Math.Round(Math.Max(samples, 0) * (double)Math.Max(sampleIntervalMs, 0) / 1000.0, MidpointRounding.AwayFromZero);

    /// <summary>
    /// A graph covers sample intervals, including the interval before the oldest retained sample. The extra position at
    /// the left is not a fabricated sample: it makes capacity × interval the actual time axis, even while filling up.
    /// A recent window keeps only sample instants within that window; no older spike participates in its scale.
    /// </summary>
    internal static (int Skip, int Capacity) GraphWindow(IoSeries series, int intervalMs, int recentSeconds = 0)
    {
        int intervals = Math.Max(series.Capacity, series.Count);
        if (recentSeconds > 0)
            intervals = Math.Min(intervals, (int)Math.Min(int.MaxValue - 1L, (long)recentSeconds * 1000 / Math.Max(intervalMs, 1)));
        int capacity = Math.Max(1, intervals + 1);
        return (Math.Max(0, series.Count - capacity), capacity);
    }

    /// <summary>Exact, culture-aware seconds for a relative axis. No wall clock, rounding to whole seconds or locale dates.</summary>
    internal static string HistorySecondsText(int samples, int intervalMs, CultureInfo? culture = null) =>
        (Math.Max(samples, 0) * (double)Math.Max(intervalMs, 0) / 1000).ToString("0.###", culture ?? Texts.Culture);

    /// <summary>Requests per second with one decimal below 10, whole above, in the current language.</summary>
    internal static string RequestRate(double perSecond, CultureInfo? culture = null)
    {
        if (!double.IsFinite(perSecond) || perSecond < 0) perSecond = 0;
        CultureInfo format = culture ?? Texts.Culture;
        // 9.96 rounds to 10.0 at one decimal: that already belongs to the whole-number form.
        return Math.Round(perSecond, 1, MidpointRounding.AwayFromZero) < 10
            ? perSecond.ToString("0.0", format)
            : Math.Round(perSecond, MidpointRounding.AwayFromZero).ToString("0", format);
    }

    /// <summary>
    /// Whether the window holds a <see cref="IoStatsVisibility.Window"/> claim: only while the window can be seen and
    /// something that shows the rates (the header graph or the statistics page) is visible too. A hidden, minimized or
    /// closing window, and a narrow window with another page open, claim nothing, so no timer runs for nobody.
    /// </summary>
    internal static IoStatsVisibility Claim(bool windowShown, bool graphVisible, bool pageVisible) =>
        windowShown && (graphVisible || pageVisible) ? IoStatsVisibility.Window : IoStatsVisibility.None;
}
