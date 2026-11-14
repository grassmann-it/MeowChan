using System;
using System.Collections.Generic;
using System.Globalization;
using MeowChan.Desktop.Localization;
using MeowChan.Desktop.WorkViews;

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

    /// <summary>
    /// The main (solid, filled) disk rate of the header: MeowChan reading its own media files. Writing is
    /// the dashed second line of the same disk graph and a value of its own in the header text.
    /// </summary>
    internal static double HeaderDiskRate(IoRates rate) => rate.DiskRead;

    // --- the sentence of the graph button ----------------------------------------------------------------------------------

    /// <summary>The one sentence of the graph button for both rows: network download and upload, disk written and read, active downloads.</summary>
    internal static string GraphDescription(IoRates rate, IoActivity activity, CultureInfo? culture = null) =>
        Texts.T("stats.graph.name", ByteUnits.Rate(rate.NetDown, culture), ByteUnits.Rate(rate.NetUp, culture),
            ByteUnits.Rate(rate.DiskWrite, culture), ByteUnits.Rate(rate.DiskRead, culture), activity.Active);

    /// <summary>The sentence for a header that shows the network values only (no disk value is named).</summary>
    internal static string GraphDescriptionNetworkOnly(IoRates rate, IoActivity activity, CultureInfo? culture = null) =>
        Texts.T("stats.graph.nameNet", ByteUnits.Rate(rate.NetDown, culture), ByteUnits.Rate(rate.NetUp, culture), activity.Active);

    /// <summary>The sentence for a header that shows the disk values only (no network value is named).</summary>
    internal static string GraphDescriptionDiskOnly(IoRates rate, IoActivity activity, CultureInfo? culture = null) =>
        Texts.T("stats.graph.nameDisk", ByteUnits.Rate(rate.DiskWrite, culture), ByteUnits.Rate(rate.DiskRead, culture), activity.Active);

    /// <summary>The sentence of the header for the values it shows: both, network only or disk only.</summary>
    internal static string GraphDescription(IoRates rate, IoActivity activity, bool showNet, bool showDisk, CultureInfo? culture = null) =>
        showNet && showDisk ? GraphDescription(rate, activity, culture)
        : showNet ? GraphDescriptionNetworkOnly(rate, activity, culture)
        : GraphDescriptionDiskOnly(rate, activity, culture);

    // --- the labelled values of the header and the tray ------------------------------------------------------------------

    /// <summary>The heading of the network block: "Network".</summary>
    internal static string NetworkTitle() => Texts.T("stats.header.networkTitle");

    /// <summary>The heading of the disk block: "Disk".</summary>
    internal static string DiskTitle() => Texts.T("stats.header.diskTitle");

    /// <summary>The network values: download and upload, for example "↓ 2 MiB/s ↑ 12 KiB/s".</summary>
    internal static string NetworkValues(IoRates rate, CultureInfo? culture = null) =>
        Texts.T("stats.header.netValues", ByteUnits.Rate(rate.NetDown, culture), ByteUnits.Rate(rate.NetUp, culture));

    /// <summary>The disk values: read and write with their short letters, for example "R 1.1 MiB/s W 37.8 MiB/s".</summary>
    internal static string DiskValues(IoRates rate, CultureInfo? culture = null) =>
        Texts.T("stats.header.diskValues", ByteUnits.Rate(rate.DiskRead, culture), ByteUnits.Rate(rate.DiskWrite, culture));

    /// <summary>Heading and values on one line ("Network ↓ 2 MiB/s ↑ 12 KiB/s"), as the tray and the one-line rows show them.</summary>
    internal static string NetworkLine(IoRates rate, CultureInfo? culture = null) => NetworkTitle() + " " + NetworkValues(rate, culture);

    /// <summary>Heading and values on one line ("Disk R 1.1 MiB/s W 37.8 MiB/s").</summary>
    internal static string DiskLine(IoRates rate, CultureInfo? culture = null) => DiskTitle() + " " + DiskValues(rate, culture);

    // --- the header setting and its layouts ---------------------------------------------------------------------------------

    /// <summary>
    /// The values of <c>Config.StatsHeaderGraph</c>. Auto, Network and NetworkAndDisk are the original three; their source
    /// selection stays unchanged. Disk, SideBySide and Text were added later. Auto adapts the arrangement to the window width.
    /// </summary>
    internal const string HeaderGraphAuto = "Auto", HeaderGraphNetwork = "Network", HeaderGraphNetworkAndDisk = "NetworkAndDisk",
        HeaderGraphDisk = "Disk", HeaderGraphSideBySide = "SideBySide", HeaderGraphText = "Text";

    private static readonly string[] HeaderGraphValues =
        [HeaderGraphAuto, HeaderGraphNetwork, HeaderGraphDisk, HeaderGraphNetworkAndDisk, HeaderGraphSideBySide, HeaderGraphText];

    /// <summary>All values in the order the settings row lists them.</summary>
    internal static IReadOnlyList<string> HeaderGraphChoices => HeaderGraphValues;

    /// <summary>Maps any text to one of the values; anything unknown is <see cref="HeaderGraphAuto"/>.</summary>
    internal static string NormalizeHeaderGraph(string? value)
    {
        string? text = value?.Trim();
        foreach (string known in HeaderGraphValues)
            if (string.Equals(text, known, StringComparison.OrdinalIgnoreCase)) return known;
        return HeaderGraphAuto;
    }

    /// <summary>The window widths (DIPs) at which the richer header forms fit without touching the window controls.</summary>
    internal const double HeaderGraphsMinimumWidth = 1000, HeaderBlocksMinimumWidth = 1280;

    /// <summary>
    /// How the header is arranged. <see cref="Rows"/>: one row per value, a half-height graph and a one-line label each.
    /// <see cref="GraphBlocks"/>: two blocks side by side, each a full-height graph and a two-line word/rate matrix (solid series first).
    /// <see cref="TextBlocks"/>: the same two-line labels without graphs. <see cref="TextRows"/>: one-line labels, one under the
    /// other, without graphs (the narrow form).
    /// </summary>
    internal enum HeaderArrangement { Rows, GraphBlocks, TextBlocks, TextRows }

    /// <summary>What the header draws: which values (labelled rates) and which graphs, and how they are arranged.</summary>
    internal readonly record struct HeaderLayout(bool NetValue, bool DiskValue, bool NetGraph, bool DiskGraph, HeaderArrangement Arrangement)
    {
        /// <summary>Whether each block stacks its directional rates on two lines.</summary>
        internal bool TwoLineLabels => Arrangement is HeaderArrangement.GraphBlocks or HeaderArrangement.TextBlocks;
    }

    /// <summary>
    /// The layout for a setting and the window width (the work view does not matter). Below <see cref="HeaderGraphsMinimumWidth"/>
    /// every graph is dropped; two metrics become compact rows while a single metric keeps its stacked directions.
    /// Two graph blocks need <see cref="HeaderBlocksMinimumWidth"/> and fall back to rows in between. Auto: two blocks in a wide window, otherwise aligned rows, in every work view.
    /// Network / Disk: that block only. NetworkAndDisk: both rows with graphs. SideBySide: two graph blocks. Text: two text blocks.
    /// </summary>
    internal static HeaderLayout ResolveHeaderLayout(string? setting, double width = double.PositiveInfinity)
    {
        bool graphs = width >= HeaderGraphsMinimumWidth, blocks = width >= HeaderBlocksMinimumWidth;
        HeaderArrangement rows = graphs ? HeaderArrangement.Rows : HeaderArrangement.TextRows;
        return NormalizeHeaderGraph(setting) switch
        {
            HeaderGraphNetwork => new(true, false, graphs, false, graphs ? HeaderArrangement.GraphBlocks : HeaderArrangement.TextBlocks),
            HeaderGraphDisk => new(false, true, false, graphs, graphs ? HeaderArrangement.GraphBlocks : HeaderArrangement.TextBlocks),
            HeaderGraphNetworkAndDisk => new(true, true, graphs, graphs, rows),
            HeaderGraphSideBySide => blocks ? new(true, true, true, true, HeaderArrangement.GraphBlocks) : new(true, true, graphs, graphs, rows),
            HeaderGraphText => new(true, true, false, false, graphs ? HeaderArrangement.TextBlocks : HeaderArrangement.TextRows),
            _ => new(true, true, graphs, graphs, blocks ? HeaderArrangement.GraphBlocks : rows)
        };
    }

    /// <summary>Whether the header draws the disk graph for a setting in a window wide enough for graphs.</summary>
    internal static bool HeaderShowsDisk(string? setting) => ResolveHeaderLayout(setting).DiskGraph;

    /// <summary>Whether the header shows the labelled disk values: in every view unless the setting is "Network".</summary>
    internal static bool HeaderShowsDiskValue(string? setting) => ResolveHeaderLayout(setting, 0).DiskValue;

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
